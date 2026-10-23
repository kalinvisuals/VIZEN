<#
.SYNOPSIS
  Installs the built installer silently into a test folder, checks what it installed, then uninstalls it.

.DESCRIPTION
  Used by the Release workflow (GitHub's runner is a clean, disposable PC) before anything is published. Checks:
  the installer's own version information; that it installs without errors; the installed VIZEN.App.exe (product name,
  version) and the files VIZEN needs; the single Installed apps entry and its version; that the uninstaller removes the
  program and its entry and leaves the user data folder alone.

  Needs administrator rights (the installer writes to Program Files and HKLM). Refuses to run when VIZEN is already
  installed on this PC, so it never replaces or removes a real installation.

.PARAMETER Installer
  The setup EXE (default artifacts\installer\VIZEN-Setup-x64.exe).
#>
[CmdletBinding()]
param([string]$Installer = '')

$ErrorActionPreference = 'Stop'
function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

$root = Split-Path -Parent $PSScriptRoot
if (-not $Installer) { $Installer = Join-Path $root 'artifacts\installer\VIZEN-Setup-x64.exe' }
if (-not (Test-Path $Installer)) { Fail "Installer not found: $Installer" }
$version = ([xml](Get-Content (Join-Path $root 'VIZEN.App\VIZEN.App.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3B2C71-6E4A-4D7B-9B1E-5A0C2E7D4F19}_is1'
$dir = Join-Path $env:ProgramFiles 'VIZEN-installer-test'
$data = Join-Path $env:LOCALAPPDATA 'VIZEN'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'Run this as administrator (the installer writes to Program Files).'
}
if (Test-Path $uninstallKey) { Fail "VIZEN is already installed on this PC ($((Get-ItemProperty $uninstallKey).InstallLocation)); this test only runs where it is not." }

Step "Installer version information (expected VIZEN $version)"
$info = (Get-Item $Installer).VersionInfo
if ("$($info.ProductName)".Trim() -ne 'VIZEN' -or "$($info.ProductVersion)".Trim() -ne $version) { Fail "installer says '$($info.ProductName) $($info.ProductVersion)'" }
Write-Host "OK: $("$($info.ProductName)".Trim()) $("$($info.ProductVersion)".Trim()), $([math]::Round((Get-Item $Installer).Length / 1MB, 1)) MB"
$dataBefore = Test-Path $data

Step "Silent install into $dir"
$log = Join-Path $env:TEMP 'vizen-installer-test.log'
$p = Start-Process $Installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"", "/LOG=`"$log`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { Get-Content $log -Tail 30 -ErrorAction SilentlyContinue; Fail "installer exit code $($p.ExitCode)" }

Step 'Installed files'
$exe = Join-Path $dir 'VIZEN.App.exe'
if (-not (Test-Path $exe)) { Fail "VIZEN.App.exe missing in $dir" }
$exeInfo = (Get-Item $exe).VersionInfo
if ($exeInfo.ProductName -ne 'VIZEN' -or -not "$($exeInfo.ProductVersion)".StartsWith($version)) { Fail "installed exe says '$($exeInfo.ProductName) $($exeInfo.ProductVersion)'" }
foreach ($required in 'VIZEN.App.dll', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll', 'WebView2Loader.dll', 'NAudio.Wasapi.dll', 'Microsoft.Windows.SDK.NET.dll') {
    if (-not (Test-Path (Join-Path $dir $required))) { Fail "installed copy is missing $required" }
}
$dll = [IO.File]::ReadAllBytes((Join-Path $dir 'VIZEN.App.dll')); $text = [Text.Encoding]::Unicode.GetString($dll) + [Text.Encoding]::UTF8.GetString($dll)
if (-not $text.Contains('chromewebstore.google.com/detail/eifpggcoejhihbdighpghkacpghehnjb')) { Fail 'installed VIZEN.App.dll has no Chrome Web Store address' }
$files = @(Get-ChildItem $dir -Recurse -File)
Write-Host ("OK: {0} {1}, {2} files, {3:N1} MB" -f $exeInfo.ProductName, $exeInfo.ProductVersion, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

Step 'Installed apps entry'
$entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
if (-not $entry) { Fail 'no Installed apps entry' }
if ($entry.DisplayVersion -ne $version -or $entry.DisplayName -ne 'VIZEN') { Fail "entry says '$($entry.DisplayName) $($entry.DisplayVersion)'" }
Write-Host "OK: $($entry.DisplayName) $($entry.DisplayVersion), $($entry.Publisher)"

Step 'Silent uninstall'
$u = Start-Process (Join-Path $dir 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
if ($u.ExitCode -ne 0) { Fail "uninstaller exit code $($u.ExitCode)" }
for ($i = 0; $i -lt 30 -and (Test-Path $exe); $i++) { Start-Sleep 1 } # the uninstaller finishes from a temporary copy
if (Test-Path $exe) { Fail 'VIZEN.App.exe is still there after uninstall' }
if (Test-Path $uninstallKey) { Fail 'Installed apps entry is still there after uninstall' }
if ((Test-Path $data) -ne $dataBefore) { Fail 'the user data folder changed' }
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }

Step 'Done'
Write-Host "Installer VIZEN $version installs, runs from Program Files and uninstalls cleanly." -ForegroundColor Green
