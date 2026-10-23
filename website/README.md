# vizen.ro — update and download files

What VIZEN reads from the website, and what to upload for every release. Nothing here is uploaded automatically.

```
https://vizen.ro/
├── download/                      ← the download page (HTML, for people; VIZEN never reads it)
│   └── VIZEN-Setup-x64-<version>.exe   (optional mirror of the installer, see below)
└── updates/
    ├── latest.json                ← machine-readable release description (read by VIZEN)
    └── latest.json.sig            ← its signature (VIZEN refuses latest.json without a valid one)
```

## For every release

1. Build the installer (`scripts\build-installer.ps1`) or let the GitHub Actions release workflow do it.
2. Make the manifest (`scripts\make-update-manifest.ps1`, also done by the workflow). It writes
   `artifacts\update\latest.json` and `artifacts\update\latest.json.sig`.
3. Publish the GitHub release with three files: `VIZEN-Setup-x64.exe`, `latest.json`, `latest.json.sig`.
4. Upload **the same** `latest.json` and `latest.json.sig` to `https://vizen.ro/updates/` (replace the old ones).
   Upload them byte for byte (FTP binary mode / file manager upload, no editing): any change breaks the signature.
5. Nothing to change on the pages for the download itself: `https://vizen.ro/download` leads to the download section
   of the home page, whose buttons point to
   `https://github.com/kalinvisuals/VIZEN/releases/latest/download/VIZEN-Setup-x64.exe`. When the page loads,
   `assets/site.js` reads `/updates/latest.json` (the same signed file VIZEN reads) and shows its version and size and
   links the buttons to the exact installer it describes, so the website and the app always agree. If that file
   cannot be read it asks the GitHub API instead, and without either the built-in values stay.
   Upload the updated `latest.json` only after the GitHub release is published (its installer address must exist).

Optional mirror: to serve the installer from vizen.ro too, upload it as
`https://vizen.ro/download/VIZEN-Setup-x64-<version>.exe` **before** making the manifest with `-WebsiteMirror`. VIZEN
then falls back to it when GitHub cannot be reached; the SHA-256 in the signed manifest protects both copies.

## Server requirements

- HTTPS only (VIZEN refuses plain HTTP addresses).
- `latest.json` served as `application/json` (any type works; no redirects to HTML pages).
- Short caching for `/updates/` (for example `Cache-Control: max-age=300`), so a new release is seen within minutes.

## How VIZEN uses these files

- About 45 seconds after start and then every 6 hours (only when "Check for updates automatically" is on), and when
  the user presses "Check for updates".
- vizen.ro is read first; the GitHub release (the same signed manifest attached as assets) is the fallback. If both
  answer with different versions, VIZEN offers the newest one that is correctly signed. If they describe different
  files for the same version, VIZEN installs nothing.
- The installer is downloaded only after the user asks (or in the background when the user turned that on), checked
  against the size and SHA-256 in the signed manifest and its own version information, and run only after the user
  confirms the restart.
- Microsoft Store copies of VIZEN ignore these files: the Store updates them.
