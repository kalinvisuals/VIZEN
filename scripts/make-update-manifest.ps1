# Writes and signs the update manifest for a release:
#   artifacts\update\latest.json and artifacts\update\latest.json.sig
# Upload both to vizen.ro (https://vizen.ro/updates/) and attach both to the GitHub release, next to the installer.
#
#   powershell -ExecutionPolicy Bypass -File scripts\make-update-manifest.ps1 -NotesEn "..." -NotesRo "..."
#
# -Installer    the installer to describe (default artifacts\installer\VIZEN-Setup-x64.exe)
# -Key          the private update key (default %USERPROFILE%\.vizen\update-signing-key.pem; "env:NAME" reads a variable)
# -WebsiteMirror also list https://vizen.ro/download/VIZEN-Setup-x64-<version>.exe (only if that file is uploaded)
param(
  [string]$NotesEn = '',
  [string]$NotesRo = '',
  [string]$Installer = '',
  [string]$Key = "$env:USERPROFILE\.vizen\update-signing-key.pem",
  [switch]$WebsiteMirror
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Installer) { $Installer = Join-Path $root 'artifacts\installer\VIZEN-Setup-x64.exe' }
if (-not (Test-Path $Installer)) { throw "installer not found: $Installer" }
$version = ([xml](Get-Content (Join-Path $root 'VIZEN.App\VIZEN.App.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# The installer must say VIZEN and this version (the app checks the same before running it).
$info = (Get-Item $Installer).VersionInfo
$name = "$($info.ProductName)".Trim(); $fileVersion = "$($info.ProductVersion)".Trim() # Inno Setup pads these with spaces
if ($name -ne 'VIZEN' -or $fileVersion -ne $version) { throw "installer is '$name $fileVersion', expected 'VIZEN $version'" }

$file = Get-Item $Installer
$sha = (Get-FileHash $Installer -Algorithm SHA256).Hash.ToLowerInvariant()
$mirrors = @()
if ($WebsiteMirror) { $mirrors += "https://vizen.ro/download/VIZEN-Setup-x64-$version.exe" }
if (-not $NotesEn) { $NotesEn = "VIZEN $version." }
if (-not $NotesRo) { $NotesRo = $NotesEn }

function J([string]$s) { ($s | ConvertTo-Json -Compress) } # JSON string literal
$mirrorJson = ($mirrors | ForEach-Object { J $_ }) -join ', '
$json = @"
{
  "schema": 1,
  "product": "VIZEN",
  "channel": "stable",
  "version": $(J $version),
  "releaseDate": $(J (Get-Date -Format 'yyyy-MM-dd')),
  "notes": { "en": $(J $NotesEn), "ro": $(J $NotesRo) },
  "notesUrl": $(J "https://github.com/kalinvisuals/VIZEN/releases/tag/v$version"),
  "installer": {
    "url": $(J "https://github.com/kalinvisuals/VIZEN/releases/download/v$version/VIZEN-Setup-x64.exe"),
    "mirrors": [$mirrorJson],
    "size": $($file.Length),
    "sha256": $(J $sha)
  }
}
"@

$out = Join-Path $root 'artifacts\update'
New-Item -ItemType Directory -Force $out | Out-Null
$manifest = Join-Path $out 'latest.json'
[IO.File]::WriteAllText($manifest, $json.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
dotnet run (Join-Path $root 'scripts\update-signing.cs') -- sign $Key $manifest
if ($LASTEXITCODE -ne 0) { throw 'signing failed' }
"$manifest (VIZEN $version, $($file.Length) bytes, sha256 $sha)"
