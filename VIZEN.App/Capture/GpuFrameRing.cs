using System.Runtime.InteropServices;
using System.Windows;
using Windows.Graphics;

namespace VIZEN.App.Capture;

/// <summary>
/// GPU side of one window capture. The newest Windows frame is kept (not copied) until the next one arrives, and native
/// presenters draw straight from its texture into their own swap chains: no extra texture and no GPU copy per frame.
/// With two pool buffers Windows always has one free buffer to render the next frame into while VIZEN holds the
/// newest. All methods that touch the device context must run under the capture device's ContextLock.
/// The drawing forces alpha to opaque (some windows report undefined alpha), like the CPU path's Bgr32.
/// </summary>
internal sealed unsafe class GpuFrameRing : IDisposable
{
    private const string Shaders = """
        cbuffer Source : register(b0) { float4 rect; };   // xy = top-left, zw = size, in 0..1 of the source texture
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        V vs(uint id : SV_VertexID)
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            V v; v.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1); v.uv = uv; return v;
        }
        Texture2D<float4> src : register(t0);
        SamplerState linearClamp : register(s0);
        float4 ps(V v) : SV_Target { return float4(src.Sample(linearClamp, rect.xy + v.uv * rect.zw).rgb, 1); }
        """;

    private static byte[]? _vsCode;
    private static byte[]? _psCode;

    private readonly CaptureDevice _device;
    // Shader views of the pool textures (Windows reuses the same few textures), with a reference on each texture.
    private readonly List<(IntPtr Texture, IntPtr View)> _views = [];
    private readonly IntPtr _vs;
    private readonly IntPtr _ps;
    private readonly IntPtr _sampler;
    private readonly IntPtr _constants;
    private readonly IntPtr[] _markerStaging = new IntPtr[2];
    private int _markerNext;
    private (uint W, uint H) _markerPreviousSize;
    private byte[] _markerBytes = [];
    private uint _latestWidth;
    private uint _latestHeight;
    private IntPtr _latestView;
    private Action? _releaseLatest;
    private SizeInt32 _latestContent;

    public GpuFrameRing(CaptureDevice device)
    {
        _device = device;
        _vsCode ??= D3D11Interop.CompileShader(Shaders, "vs", "vs_4_0");
        _psCode ??= D3D11Interop.CompileShader(Shaders, "ps", "ps_4_0");
        Marshal.ThrowExceptionForHR(D3D11Interop.CreateShader(device.Device, false, _vsCode, out _vs));
        Marshal.ThrowExceptionForHR(D3D11Interop.CreateShader(device.Device, true, _psCode, out _ps));
        var sampler = new D3D11Interop.SamplerDesc
        {
            Filter = 0x15, // MIN_MAG_MIP_LINEAR
            AddressU = 3, AddressV = 3, AddressW = 3, // CLAMP
            MaxAnisotropy = 1,
            ComparisonFunc = 1, // NEVER
            MaxLOD = float.MaxValue,
        };
        Marshal.ThrowExceptionForHR(D3D11Interop.CreateSamplerState(device.Device, ref sampler, out _sampler));
        var constants = new D3D11Interop.BufferDesc
        {
            ByteWidth = 16,
            Usage = D3D11Interop.D3D11_USAGE_DYNAMIC,
            BindFlags = D3D11Interop.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = D3D11Interop.D3D11_CPU_ACCESS_WRITE,
        };
        Marshal.ThrowExceptionForHR(D3D11Interop.CreateBuffer(device.Device, ref constants, out _constants));
    }

    public CaptureDevice Device => _device;

    /// <summary>Content size of the newest frame (0×0 before the first one).</summary>
    public SizeInt32 LatestContent => _latestContent;

    /// <summary>
    /// Capture thread, under ContextLock: makes a Windows frame the newest one. The ring keeps the frame until the next
    /// <see cref="Hold"/> or <see cref="ReleaseLatest"/> and then calls <paramref name="release"/> to give it back.
    /// Returns the frame-number marker when <paramref name="readMarker"/> is set (test sources; a few KB).
    /// </summary>
    public uint? Hold(IntPtr texture, in D3D11Interop.Texture2DDesc desc, SizeInt32 content, Action release, bool readMarker)
    {
        var view = ViewFor(texture);
        var previous = _releaseLatest;
        _latestView = view;
        _latestWidth = desc.Width;
        _latestHeight = desc.Height;
        _latestContent = content;
        _releaseLatest = release;
        previous?.Invoke();
        return readMarker ? ReadMarker(texture, content) : null;
    }

    /// <summary>Under ContextLock: gives the held frame back to Windows (before the pool is recreated or stopped).</summary>
    public void ReleaseLatest()
    {
        _latestView = IntPtr.Zero;
        var release = _releaseLatest;
        _releaseLatest = null;
        release?.Invoke();
        ReleaseViews();
    }

    /// <summary>
    /// Under ContextLock: draws the newest frame (its <paramref name="viewbox"/> part, 0..1) into a render target,
    /// scaled uniformly and centred, on <paramref name="background"/>. False when there is no frame yet.
    /// </summary>
    public bool Draw(IntPtr renderTarget, int targetWidth, int targetHeight, Rect viewbox, (float R, float G, float B) background)
    {
        var context = _device.Context;
        D3D11Interop.ClearRenderTarget(context, renderTarget, background.R, background.G, background.B);
        if (_latestView == IntPtr.Zero || _latestContent.Width <= 0 || _latestContent.Height <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return false;
        }

        // Region of the ring texture to show: the viewbox of the content area (the texture can be larger than the content).
        float u = (float)(viewbox.X * _latestContent.Width / _latestWidth);
        float v = (float)(viewbox.Y * _latestContent.Height / _latestHeight);
        float uw = (float)(viewbox.Width * _latestContent.Width / _latestWidth);
        float vh = (float)(viewbox.Height * _latestContent.Height / _latestHeight);
        D3D11Interop.WriteConstants(context, _constants, [u, v, uw, vh]);

        // Uniform fit, centred (like Stretch="Uniform").
        var shownW = viewbox.Width * _latestContent.Width;
        var shownH = viewbox.Height * _latestContent.Height;
        var scale = Math.Min(targetWidth / shownW, targetHeight / shownH);
        var w = (float)Math.Round(shownW * scale);
        var h = (float)Math.Round(shownH * scale);
        var x = (float)Math.Round((targetWidth - w) / 2);
        var y = (float)Math.Round((targetHeight - h) / 2);

        D3D11Interop.IASetInputLayout(context, IntPtr.Zero);
        D3D11Interop.IASetPrimitiveTopology(context, D3D11Interop.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11Interop.SetShader(context, false, _vs);
        D3D11Interop.SetShader(context, true, _ps);
        D3D11Interop.PSSetConstantBuffer(context, _constants);
        D3D11Interop.PSSetSampler(context, _sampler);
        D3D11Interop.PSSetShaderResource(context, _latestView);
        D3D11Interop.OMSetRenderTarget(context, renderTarget);
        D3D11Interop.RSSetViewport(context, x, y, w, h);
        D3D11Interop.Draw(context, 3);
        D3D11Interop.PSSetShaderResource(context, IntPtr.Zero);
        D3D11Interop.OMSetRenderTarget(context, IntPtr.Zero);
        return true;
    }

    private IntPtr ViewFor(IntPtr texture)
    {
        foreach (var (t, v) in _views)
        {
            if (t == texture)
            {
                return v;
            }
        }

        if (_views.Count >= 4)
        {
            // Not the pool we knew (recreated): forget the old textures. The held frame's view is in the list too,
            // but it is replaced by this frame before anything draws again.
            _latestView = IntPtr.Zero;
            ReleaseViews();
        }

        Marshal.ThrowExceptionForHR(D3D11Interop.CreateShaderResourceView(_device.Device, texture, out var view));
        Marshal.AddRef(texture);
        _views.Add((texture, view));
        return view;
    }

    /// <summary>
    /// Reads the top-left marker strip of a test source, one frame late: this frame's strip is copied now and the
    /// previous frame's copy (long finished on the GPU) is read, so the capture thread never waits for the GPU while it
    /// holds the shared device lock. Skipped/repeated counts are unaffected by the constant one-frame offset.
    /// </summary>
    private uint? ReadMarker(IntPtr texture, SizeInt32 content)
    {
        const int stripWidth = 640, stripHeight = 24;
        var w = (uint)Math.Min(stripWidth, content.Width);
        var h = (uint)Math.Min(stripHeight, content.Height);
        for (var i = 0; i < _markerStaging.Length; i++)
        {
            if (_markerStaging[i] == IntPtr.Zero)
            {
                var desc = new D3D11Interop.Texture2DDesc
                {
                    Width = stripWidth,
                    Height = stripHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = D3D11Interop.DXGI_FORMAT_B8G8R8A8_UNORM,
                    SampleCount = 1,
                    Usage = D3D11Interop.D3D11_USAGE_STAGING,
                    CPUAccessFlags = D3D11Interop.D3D11_CPU_ACCESS_READ,
                };
                Marshal.ThrowExceptionForHR(D3D11Interop.CreateTexture2D(_device.Device, ref desc, out _markerStaging[i]));
            }
        }

        var current = _markerNext;
        _markerNext = 1 - current;
        D3D11Interop.CopySubresourceRegion(_device.Context, _markerStaging[current], texture, new D3D11Interop.Box { Right = w, Bottom = h, Back = 1 });
        var previous = _markerStaging[1 - current];
        var (pw, ph) = _markerPreviousSize;
        _markerPreviousSize = (w, h);
        if (pw == 0 || D3D11Interop.Map(_device.Context, previous, out var mapped) < 0)
        {
            return null;
        }

        try
        {
            var pitch = (int)mapped.RowPitch;
            var length = pitch * (int)ph;
            if (_markerBytes.Length < length)
            {
                _markerBytes = new byte[length];
            }

            Marshal.Copy(mapped.pData, _markerBytes, 0, length);
            return FrameMarker.Decode(_markerBytes, (int)pw, (int)ph, pitch);
        }
        finally
        {
            D3D11Interop.Unmap(_device.Context, previous);
        }
    }

    private void ReleaseViews()
    {
        foreach (var (texture, view) in _views)
        {
            Marshal.Release(view);
            Marshal.Release(texture);
        }

        _views.Clear();
    }

    internal static void Release(ref IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            Marshal.Release(comObject);
            comObject = IntPtr.Zero;
        }
    }

    /// <summary>Under ContextLock.</summary>
    public void Dispose()
    {
        ReleaseLatest();
        Release(ref _markerStaging[0]);
        Release(ref _markerStaging[1]);
        var vs = _vs;
        var ps = _ps;
        var sampler = _sampler;
        var constants = _constants;
        Release(ref vs);
        Release(ref ps);
        Release(ref sampler);
        Release(ref constants);
    }
}
