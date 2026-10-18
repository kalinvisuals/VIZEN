# Packages the VIZEN Connector extension for the stores (Microsoft Edge Add-ons, Chrome Web Store):
# artifacts/extension/vizen-connector-<version>.zip, manifest.json at the root, forward-slash paths.
# The "key" field is left out: it only fixes the ID of the copy loaded from its folder; each store gives the extension
# its own ID, which must be added to BrowserTabs.ExtensionIds in VIZEN.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'browser-extension'
$manifestText = [IO.File]::ReadAllText((Join-Path $source 'manifest.json'))
$version = ([regex]::Match($manifestText, '"version":\s*"([^"]+)"')).Groups[1].Value
$storeManifest = [regex]::Replace($manifestText, '\r?\n\s*"key":\s*"[^"]*",', '')
if ($storeManifest -match '"key"') { throw 'could not remove the key field' }

$outDir = Join-Path $root 'artifacts\extension'
New-Item -ItemType Directory -Force $outDir | Out-Null
$zipPath = Join-Path $outDir "vizen-connector-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }

$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
  foreach ($file in Get-ChildItem $source -Recurse -File) {
    $relative = $file.FullName.Substring($source.Length + 1).Replace('\', '/')
    if ($relative -eq 'manifest.json') {
      $entry = $zip.CreateEntry($relative, 'Optimal')
      $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding $false))
      $writer.Write($storeManifest); $writer.Dispose()
    }
    else {
      [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, 'Optimal') | Out-Null
    }
  }
}
finally { $zip.Dispose() }

$check = [IO.Compression.ZipFile]::OpenRead($zipPath)
"$zipPath ($([int]((Get-Item $zipPath).Length / 1kb)) KB, $($check.Entries.Count) files)"
$check.Entries | ForEach-Object { "  $($_.FullName)" }
$check.Dispose()
