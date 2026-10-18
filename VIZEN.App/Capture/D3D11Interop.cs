using System.Runtime.InteropServices;

namespace VIZEN.App.Capture;

/// <summary>
/// Minimal Direct3D 11 / WinRT interop needed to read Windows Graphics Capture frames on the CPU.
/// COM methods are invoked through their vtable slots (order as declared in d3d11.h) to avoid
/// pulling in a full DirectX wrapper library.
/// </summary>
internal static unsafe class D3D11Interop
{
    public const uint D3D11_SDK_VERSION = 7;
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D_DRIVER_TYPE_WARP = 5;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS = 0x8;

    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const uint D3D11_USAGE_STAGING = 3;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_MAP_READ = 1;

    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    public static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    public static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MappedSubresource
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags, IntPtr pFeatureLevels, uint featureLevels,
        uint sdkVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll")]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    private static IntPtr* VTable(IntPtr comObject) => *(IntPtr**)comObject;

    // ID3D11Device::CreateTexture2D (slot 5)
    public static int CreateTexture2D(IntPtr device, ref Texture2DDesc desc, out IntPtr texture)
    {
        IntPtr result;
        int hr;
        fixed (Texture2DDesc* pDesc = &desc)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)VTable(device)[5])(
                device, pDesc, IntPtr.Zero, &result);
        }

        texture = result;
        return hr;
    }

    // ID3D11Texture2D::GetDesc (slot 10)
    public static Texture2DDesc GetDesc(IntPtr texture)
    {
        Texture2DDesc desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)VTable(texture)[10])(texture, &desc);
        return desc;
    }

    // ID3D11DeviceContext::Map (slot 14)
    public static int Map(IntPtr context, IntPtr resource, out MappedSubresource mapped)
    {
        MappedSubresource result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)VTable(context)[14])(
            context, resource, 0, D3D11_MAP_READ, 0, &result);
        mapped = result;
        return hr;
    }

    // ID3D11DeviceContext::Unmap (slot 15)
    public static void Unmap(IntPtr context, IntPtr resource)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)VTable(context)[15])(context, resource, 0);

    // ID3D11DeviceContext::CopyResource (slot 47)
    public static void CopyResource(IntPtr context, IntPtr destination, IntPtr source)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)VTable(context)[47])(context, destination, source);

    // ----- GPU presentation path (shared texture → WPF D3DImage) -----

    public const uint D3D11_USAGE_DEFAULT = 0;
    public const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    public const uint D3D11_BIND_RENDER_TARGET = 0x20;
    public const uint D3D11_RESOURCE_MISC_SHARED = 0x2;
    public const uint D3D11_QUERY_EVENT = 0;
    public const uint D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST = 4;
    public static readonly Guid IID_IDXGIResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");

    [StructLayout(LayoutKind.Sequential)]
    public struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Viewport
    {
        public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
    }

    // ID3D11Device::CreateShaderResourceView (slot 7), default view
    public static int CreateShaderResourceView(IntPtr device, IntPtr resource, out IntPtr view)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)VTable(device)[7])(device, resource, IntPtr.Zero, &result);
        view = result;
        return hr;
    }

    // ID3D11Device::CreateRenderTargetView (slot 9), default view
    public static int CreateRenderTargetView(IntPtr device, IntPtr resource, out IntPtr view)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)VTable(device)[9])(device, resource, IntPtr.Zero, &result);
        view = result;
        return hr;
    }

    // ID3D11Device::CreateVertexShader (slot 12) / CreatePixelShader (slot 15)
    public static int CreateShader(IntPtr device, bool pixel, byte[] bytecode, out IntPtr shader)
    {
        IntPtr result;
        int hr;
        fixed (byte* code = bytecode)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)VTable(device)[pixel ? 15 : 12])(
                device, code, (nuint)bytecode.Length, IntPtr.Zero, &result);
        }

        shader = result;
        return hr;
    }

    // ID3D11Device::CreateQuery (slot 24)
    public static int CreateEventQuery(IntPtr device, out IntPtr query)
    {
        var desc = stackalloc uint[2] { D3D11_QUERY_EVENT, 0 };
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int>)VTable(device)[24])(device, desc, &result);
        query = result;
        return hr;
    }

    // ID3D11DeviceContext::PSSetShaderResources (slot 8)
    public static void PSSetShaderResource(IntPtr context, IntPtr view)
    {
        var views = stackalloc IntPtr[1] { view };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)VTable(context)[8])(context, 0, 1, views);
    }

    // ID3D11DeviceContext::PSSetShader (slot 9) / VSSetShader (slot 11)
    public static void SetShader(IntPtr context, bool pixel, IntPtr shader)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)VTable(context)[pixel ? 9 : 11])(context, shader, IntPtr.Zero, 0);

    // ID3D11DeviceContext::Draw (slot 13)
    public static void Draw(IntPtr context, uint vertexCount)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)VTable(context)[13])(context, vertexCount, 0);

    // ID3D11DeviceContext::IASetInputLayout (slot 17)
    public static void IASetInputLayout(IntPtr context, IntPtr layout)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)VTable(context)[17])(context, layout);

    // ID3D11DeviceContext::IASetPrimitiveTopology (slot 24)
    public static void IASetPrimitiveTopology(IntPtr context, uint topology)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)VTable(context)[24])(context, topology);

    // ID3D11DeviceContext::End (slot 28)
    public static void End(IntPtr context, IntPtr async)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)VTable(context)[28])(context, async);

    // ID3D11DeviceContext::GetData (slot 29): S_OK (0) when the GPU finished, S_FALSE (1) while still busy
    public static int GetData(IntPtr context, IntPtr async)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, int>)VTable(context)[29])(context, async, IntPtr.Zero, 0, 0);

    // ID3D11DeviceContext::OMSetRenderTargets (slot 33)
    public static void OMSetRenderTarget(IntPtr context, IntPtr view)
    {
        var views = stackalloc IntPtr[1] { view };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)VTable(context)[33])(context, view == IntPtr.Zero ? 0u : 1u, views, IntPtr.Zero);
    }

    // ID3D11DeviceContext::RSSetViewports (slot 44)
    public static void RSSetViewport(IntPtr context, float width, float height)
    {
        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, Viewport*, void>)VTable(context)[44])(context, 1, &viewport);
    }

    // ID3D11DeviceContext::CopySubresourceRegion (slot 46)
    public static void CopySubresourceRegion(IntPtr context, IntPtr destination, IntPtr source, Box box)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)VTable(context)[46])(
            context, destination, 0, 0, 0, 0, source, 0, &box);

    // ----- Native presentation (DXGI swap chain on a window, as OBS does for previews and projectors) -----

    public const uint D3D11_BIND_CONSTANT_BUFFER = 0x4;
    public const uint D3D11_USAGE_DYNAMIC = 2;
    public const uint D3D11_CPU_ACCESS_WRITE = 0x10000;
    public const uint D3D11_MAP_WRITE_DISCARD = 4;
    public const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;
    public const uint DXGI_SWAP_EFFECT_DISCARD = 0;
    public const uint DXGI_SWAP_EFFECT_FLIP_DISCARD = 4;
    public static readonly Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferDesc
    {
        public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SamplerDesc
    {
        public uint Filter, AddressU, AddressV, AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy, ComparisonFunc;
        public float Border0, Border1, Border2, Border3, MinLOD, MaxLOD;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SwapChainDesc1
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    // ID3D11Device::CreateBuffer (slot 3), no initial data
    public static int CreateBuffer(IntPtr device, ref BufferDesc desc, out IntPtr buffer)
    {
        IntPtr result;
        int hr;
        fixed (BufferDesc* pDesc = &desc)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, BufferDesc*, IntPtr, IntPtr*, int>)VTable(device)[3])(device, pDesc, IntPtr.Zero, &result);
        }

        buffer = result;
        return hr;
    }

    // ID3D11Device::CreateSamplerState (slot 23)
    public static int CreateSamplerState(IntPtr device, ref SamplerDesc desc, out IntPtr sampler)
    {
        IntPtr result;
        int hr;
        fixed (SamplerDesc* pDesc = &desc)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, SamplerDesc*, IntPtr*, int>)VTable(device)[23])(device, pDesc, &result);
        }

        sampler = result;
        return hr;
    }

    // ID3D11DeviceContext::PSSetSamplers (slot 10)
    public static void PSSetSampler(IntPtr context, IntPtr sampler)
    {
        var samplers = stackalloc IntPtr[1] { sampler };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)VTable(context)[10])(context, 0, 1, samplers);
    }

    // ID3D11DeviceContext::PSSetConstantBuffers (slot 16)
    public static void PSSetConstantBuffer(IntPtr context, IntPtr buffer)
    {
        var buffers = stackalloc IntPtr[1] { buffer };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)VTable(context)[16])(context, 0, 1, buffers);
    }

    /// <summary>ID3D11DeviceContext::Map (slot 14) with WRITE_DISCARD, copies <paramref name="values"/>, then Unmap.</summary>
    public static void WriteConstants(IntPtr context, IntPtr buffer, ReadOnlySpan<float> values)
    {
        MappedSubresource mapped;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)VTable(context)[14])(
            context, buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        Marshal.ThrowExceptionForHR(hr);
        values.CopyTo(new Span<float>((void*)mapped.pData, values.Length));
        Unmap(context, buffer);
    }

    // ID3D11DeviceContext::ClearRenderTargetView (slot 50)
    public static void ClearRenderTarget(IntPtr context, IntPtr view, float r, float g, float b)
    {
        var color = stackalloc float[4] { r, g, b, 1 };
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)VTable(context)[50])(context, view, color);
    }

    /// <summary>RSSetViewports (slot 44) with an offset.</summary>
    public static void RSSetViewport(IntPtr context, float x, float y, float width, float height)
    {
        var viewport = new Viewport { TopLeftX = x, TopLeftY = y, Width = width, Height = height, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, Viewport*, void>)VTable(context)[44])(context, 1, &viewport);
    }

    /// <summary>The IDXGIFactory2 that created <paramref name="device"/>'s adapter (owned by the caller).</summary>
    public static IntPtr GetFactory2(IntPtr device)
    {
        var iidDevice = IID_IDXGIDevice;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iidDevice, out var dxgiDevice));
        try
        {
            // IDXGIDevice::GetAdapter (slot 7)
            IntPtr adapter;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VTable(dxgiDevice)[7])(dxgiDevice, &adapter));
            try
            {
                // IDXGIObject::GetParent (slot 6)
                var iidFactory = IID_IDXGIFactory2;
                IntPtr factory;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)VTable(adapter)[6])(adapter, &iidFactory, &factory));
                return factory;
            }
            finally
            {
                Marshal.Release(adapter);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    // IDXGIFactory2::CreateSwapChainForHwnd (slot 15)
    public static int CreateSwapChainForHwnd(IntPtr factory, IntPtr device, IntPtr hwnd, ref SwapChainDesc1 desc, out IntPtr swapChain)
    {
        IntPtr result;
        int hr;
        fixed (SwapChainDesc1* pDesc = &desc)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, SwapChainDesc1*, IntPtr, IntPtr, IntPtr*, int>)VTable(factory)[15])(
                factory, device, hwnd, pDesc, IntPtr.Zero, IntPtr.Zero, &result);
        }

        swapChain = result;
        return hr;
    }

    // IDXGISwapChain::Present (slot 8)
    public static int Present(IntPtr swapChain, uint syncInterval, uint flags = 0)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)VTable(swapChain)[8])(swapChain, syncInterval, flags);

    // IDXGISwapChain::GetBuffer (slot 9) — buffer 0 as ID3D11Texture2D
    public static int GetBackBuffer(IntPtr swapChain, out IntPtr texture)
    {
        var iid = IID_ID3D11Texture2D;
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)VTable(swapChain)[9])(swapChain, 0, &iid, &result);
        texture = result;
        return hr;
    }

    // IDXGISwapChain::ResizeBuffers (slot 13), keeping buffer count and format
    public static int ResizeBuffers(IntPtr swapChain, uint width, uint height)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int>)VTable(swapChain)[13])(swapChain, 0, width, height, 0, 0);

    // ID3D11DeviceContext::UpdateSubresource (slot 48): writes CPU data into a box of a GPU resource
    public static void UpdateSubresource(IntPtr context, IntPtr resource, Box box, void* data, uint rowPitch)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, Box*, void*, uint, uint, void>)VTable(context)[48])(context, resource, 0, &box, data, rowPitch, 0);

    [StructLayout(LayoutKind.Sequential)]
    public struct FrameStatistics
    {
        public uint PresentCount, PresentRefreshCount, SyncRefreshCount;
        public long SyncQpcTime, SyncGpuTime;
    }

    // IDXGISwapChain::GetFrameStatistics (slot 16)
    public static int GetFrameStatistics(IntPtr swapChain, out FrameStatistics stats)
    {
        FrameStatistics result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, FrameStatistics*, int>)VTable(swapChain)[16])(swapChain, &result);
        stats = result;
        return hr;
    }

    /// <summary>
    /// GraphicsCaptureSession.MinUpdateInterval through IGraphicsCaptureSession5 (Windows 11 24H2, SDK 26100; VIZEN builds
    /// against an older SDK projection). False when this Windows build does not have it.
    /// </summary>
    public static bool TrySetMinUpdateInterval(Windows.Graphics.Capture.GraphicsCaptureSession session, TimeSpan interval)
    {
        var iid = new Guid("67c0ea62-1f85-5061-925a-239be0ac09cb"); // IGraphicsCaptureSession5
        var abi = ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;
        if (Marshal.QueryInterface(abi, in iid, out var session5) < 0)
        {
            return false;
        }

        try
        {
            // IInspectable (6 slots), then get_MinUpdateInterval (6), put_MinUpdateInterval (7); TimeSpan = 100 ns ticks.
            return ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)VTable(session5)[7])(session5, interval.Ticks) >= 0;
        }
        finally
        {
            Marshal.Release(session5);
        }
    }

    // ID3D11DeviceContext::ClearState (slot 110): unbinds everything so released objects can be destroyed
    public static void ClearState(IntPtr context)
        => ((delegate* unmanaged[Stdcall]<IntPtr, void>)VTable(context)[110])(context);

    // ID3D11DeviceContext::Flush (slot 111)
    public static void Flush(IntPtr context)
        => ((delegate* unmanaged[Stdcall]<IntPtr, void>)VTable(context)[111])(context);

    /// <summary>IDXGIResource::GetSharedHandle (slot 8) of a texture created with D3D11_RESOURCE_MISC_SHARED.</summary>
    public static IntPtr GetSharedHandle(IntPtr texture)
    {
        var iid = IID_IDXGIResource;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, in iid, out var resource));
        try
        {
            IntPtr handle;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VTable(resource)[8])(resource, &handle));
            return handle;
        }
        finally
        {
            Marshal.Release(resource);
        }
    }

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
    private static extern int D3DCompileNative(byte[] source, nuint length, [MarshalAs(UnmanagedType.LPStr)] string? sourceName,
        IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    /// <summary>Compiles HLSL with the system shader compiler (d3dcompiler_47.dll, part of Windows 10/11).</summary>
    public static byte[] CompileShader(string hlsl, string entryPoint, string target)
    {
        var source = System.Text.Encoding.ASCII.GetBytes(hlsl);
        var hr = D3DCompileNative(source, (nuint)source.Length, null, IntPtr.Zero, IntPtr.Zero, entryPoint, target, 0, 0, out var code, out var errors);
        try
        {
            if (hr < 0 || code == IntPtr.Zero)
            {
                var message = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors)) : null;
                throw new COMException($"Shader compilation failed: {message}", hr);
            }

            var bytes = new byte[(int)BlobSize(code)];
            Marshal.Copy(BlobPointer(code), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (code != IntPtr.Zero)
            {
                Marshal.Release(code);
            }

            if (errors != IntPtr.Zero)
            {
                Marshal.Release(errors);
            }
        }
    }

    // ID3DBlob::GetBufferPointer (slot 3) / GetBufferSize (slot 4)
    private static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)VTable(blob)[3])(blob);

    private static nuint BlobSize(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)VTable(blob)[4])(blob);

    /// <summary>IDirect3DDxgiInterfaceAccess::GetInterface (slot 3) — returns the ID3D11Texture2D behind a WinRT surface.</summary>
    public static IntPtr GetTextureFromSurface(IntPtr surface)
    {
        var iidAccess = IID_IDirect3DDxgiInterfaceAccess;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in iidAccess, out var access));
        try
        {
            var iidTexture = IID_ID3D11Texture2D;
            IntPtr texture;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)VTable(access)[3])(access, &iidTexture, &texture);
            Marshal.ThrowExceptionForHR(hr);
            return texture;
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    /// <summary>
    /// IGraphicsCaptureItemInterop::CreateForWindow (slot 3). Returns an owned IGraphicsCaptureItem pointer.
    /// </summary>
    public static IntPtr CreateCaptureItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        try
        {
            var iidInterop = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iidInterop, out var factory));
            try
            {
                var iidItem = IID_IGraphicsCaptureItem;
                IntPtr item;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)VTable(factory)[3])(
                    factory, hwnd, &iidItem, &item);
                Marshal.ThrowExceptionForHR(hr);
                return item;
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }
}
