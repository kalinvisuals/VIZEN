<#
.SYNOPSIS
  Builds the VIZEN Windows installer (artifacts\installer\VIZEN-Setup-x64.exe).

.DESCRIPTION
  1. Locates the VIZEN project.  2. Builds it in Release.  3. Publishes a self-contained win-x64 copy.
  4. Starts the published app once to check it launches (isolated test data folder, then closes it).
  5. Locates Inno Setup 6 (ISCC.exe).  6. Compiles installer\VIZEN.iss.  7. Verifies and prints the setup EXE.

  Only artifacts\ is written. The source project and your VIZEN settings are never modified.

.PARAMETER SkipSmokeTest
  Do not launch the published app before packaging.
#>
[CmdletBinding()]
param([switch]$SkipSmokeTest)

$ErrorActionPreference = 'Stop'
function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

# 1. Project
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'VIZEN.App\VIZEN.App.csproj'
$iss = Join-Path $root 'installer\VIZEN.iss'
$publishDir = Join-Path $root 'artifacts\publish\win-x64'
$outputExe = Join-Path $root 'artifacts\installer\VIZEN-Setup-x64.exe'
if (-not (Test-Path $project)) { Fail "Project not found: $project" }
if (-not (Test-Path $iss)) { Fail "Installer script not found: $iss" }

$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail 'No <Version> in VIZEN.App.csproj' }
Step "VIZEN $version  ($project)"

# 2. Release build
Step 'Building Release'
dotnet build $project -c Release -nologo -v minimal
if ($LASTEXITCODE -ne 0) { Fail 'dotnet build failed' }

# 3. Self-contained publish (fresh folder, so no stale files end up in the installer)
Step 'Publishing self-contained win-x64'
if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir -nologo -v minimal
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed' }
$appExe = Join-Path $publishDir 'VIZEN.App.exe'
if (-not (Test-Path $appExe)) { Fail "Published executable missing: $appExe" }
foreach ($required in 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll', 'WebView2Loader.dll', 'NAudio.Wasapi.dll') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { Fail "Published output is missing $required" }
}
$files = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
Write-Host ("Published {0} files, {1:N1} MB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

# 4. Smoke test: the published app must start and show its window without a separate .NET install
if (-not $SkipSmokeTest) {
    Step 'Launching the published app (isolated test data folder)'
    $testData = Join-Path $root 'artifacts\smoketest-data'
    if (Test-Path $testData) { Remove-Item -LiteralPath $testData -Recurse -Force }
    $previous = $env:VIZEN_DATA_DIR
    $env:VIZEN_DATA_DIR = $testData
    try {
        $proc = Start-Process -FilePath $appExe -PassThru
        $deadline = (Get-Date).AddSeconds(25)
        do { Start-Sleep -Milliseconds 500; $proc.Refresh() } until ($proc.HasExited -or $proc.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
        if ($proc.HasExited) { Fail "Published app exited during start-up (exit code $($proc.ExitCode))" }
        if ($proc.MainWindowHandle -eq 0) { $proc.Kill(); Fail 'Published app did not show its window within 25 s' }
        Write-Host "OK: '$($proc.MainWindowTitle)' window shown (pid $($proc.Id))" -ForegroundColor Green
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
    }
    finally {
        $env:VIZEN_DATA_DIR = $previous
        if (Test-Path $testData) { Remove-Item -LiteralPath $testData -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# 5. Inno Setup
Step 'Locating Inno Setup 6'
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { Fail 'Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup) and run this script again.' }
Write-Host $iscc

# 6. Compile
Step 'Compiling the installer'
if (Test-Path $outputExe) { Remove-Item -LiteralPath $outputExe -Force }
& $iscc "/DMyAppVersion=$version" "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) { Fail "ISCC failed (exit code $LASTEXITCODE)" }

# 7. Verify
if (-not (Test-Path $outputExe)) { Fail "Installer was not created: $outputExe" }
$setup = Get-Item -LiteralPath $outputExe
Step 'Done'
Write-Host ("Installer: {0}" -f $setup.FullName) -ForegroundColor Green
Write-Host ("Size:      {0:N1} MB" -f ($setup.Length / 1MB))
Write-Host ("Version:   {0}" -f $version)
