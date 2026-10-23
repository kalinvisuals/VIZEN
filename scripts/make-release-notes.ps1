# Writes the GitHub release text and the checksums for a release:
#   artifacts\release\RELEASE_NOTES.md   (the release description: notes, install steps, SHA-256)
#   artifacts\release\SHA256SUMS.txt     (sha256sum format: installer and update manifest)
# Run after scripts\build-installer.ps1 and scripts\make-update-manifest.ps1 (the workflow does both).
# Notes come from docs\release-notes\<version>.en.txt.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'VIZEN.App\VIZEN.App.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$installer = Join-Path $root 'artifacts\installer\VIZEN-Setup-x64.exe'
$manifest = Join-Path $root 'artifacts\update\latest.json'
$signature = Join-Path $root 'artifacts\update\latest.json.sig'
$notesFile = Join-Path $root "docs\release-notes\$version.en.txt"
foreach ($f in $installer, $manifest, $signature, $notesFile) { if (-not (Test-Path $f)) { throw "missing: $f" } }

# The manifest must describe exactly this installer.
$m = Get-Content $manifest -Raw | ConvertFrom-Json
$sha = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($m.version -ne $version -or $m.installer.sha256 -ne $sha -or $m.installer.size -ne (Get-Item $installer).Length) {
  throw "latest.json describes VIZEN $($m.version) ($($m.installer.sha256)), not this installer ($version, $sha)"
}

$out = Join-Path $root 'artifacts\release'
New-Item -ItemType Directory -Force $out | Out-Null
$sums = @($installer, $manifest, $signature | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)" })
[IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS.txt'), ($sums -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))

$notes = (Get-Content $notesFile -Raw -Encoding UTF8).Trim()
$body = @"
## VIZEN $version

$notes

### Install
Download **VIZEN-Setup-x64.exe** below and run it. Windows asks for administrator permission once (VIZEN installs in Program Files). Your settings and data are kept. VIZEN 0.4.6 or later finds this version by itself (Settings → Updates) and installs it after you confirm.

SHA-256 of VIZEN-Setup-x64.exe: ``$sha``

The installer is not digitally signed yet, so Windows SmartScreen may show "Windows protected your PC": click **More info → Run anyway**. ``SHA256SUMS.txt`` lists the checksums of every file. ``latest.json`` and ``latest.json.sig`` are the signed update information read by VIZEN.

Microsoft Store copies of VIZEN are updated by the Microsoft Store.

Guide: https://vizen.ro/help/ · What's new: https://vizen.ro/changelog/ · Contact: hi@vizen.ro
"@
[IO.File]::WriteAllText((Join-Path $out 'RELEASE_NOTES.md'), $body.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
"release text and checksums for VIZEN $version in $out"
$sums
