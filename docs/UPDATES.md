# VIZEN updates

How VIZEN finds, verifies and installs new versions, and how a release reaches GitHub and vizen.ro.

## Installation channels

VIZEN detects how it was installed (`UpdateChannels.Detect`) and never mixes the channels:

| Channel | Detected by | How it is updated |
|---|---|---|
| Classic installer (Inno Setup) | runs from the folder in the VIZEN uninstall key | VIZEN downloads, verifies and runs the new installer, after the user confirms |
| Microsoft Store | MSIX package signed by the Store | Microsoft Store only; Settings → Updates shows the status and opens the Store's updates page |
| Direct MSIX (App Installer) | MSIX installed from an `.appinstaller` feed | Windows App Installer; not offered yet (it needs a paid code-signing certificate), template in `installer/msix/VIZEN.appinstaller.template` |
| Hand-installed MSIX | any other package | no automatic updates; Settings says so |
| Not installed (development build, copied folder) | none of the above | only tells that a new version exists and opens the download page; it never installs over Program Files |

A Store copy never downloads anything from vizen.ro or GitHub.

## Release information

Every release has a machine-readable manifest, `latest.json`, and a detached signature, `latest.json.sig`:

```json
{
  "schema": 1,
  "product": "VIZEN",
  "channel": "stable",
  "version": "0.4.6",
  "releaseDate": "2026-10-20",
  "notes": { "en": "…", "ro": "…" },
  "notesUrl": "https://github.com/kalinvisuals/VIZEN/releases/tag/v0.4.6",
  "installer": {
    "url": "https://github.com/kalinvisuals/VIZEN/releases/download/v0.4.6/VIZEN-Setup-x64.exe",
    "mirrors": [],
    "size": 58731034,
    "sha256": "…"
  }
}
```

The signature is ECDSA P-256 with SHA-256 over the exact bytes of `latest.json` (IEEE P1363, base64). The public key is
built into VIZEN (`UpdateSignature.TrustedKeys`; more than one key can be listed for a key change). The private key is
never in the repository: it stays on the release PC (`%USERPROFILE%\.vizen\update-signing-key.pem`, outside synced
folders, with a backup kept offline) and in the GitHub secret `UPDATE_SIGNING_KEY`.

The same two files are published in two places:

- `https://vizen.ro/updates/latest.json` and `latest.json.sig` (read first);
- as assets of the latest GitHub release (fallback, through the GitHub Releases API).

## What VIZEN checks

- **When:** about 45 seconds after start and every 6 hours if "Check for updates automatically" is on (default), and
  when the user presses "Check for updates". The check runs in the background, reads a few kilobytes and does not touch
  capture or rendering.
- **Trust:** a manifest is used only if its signature matches the built-in key, its schema and product are right, and
  every address is HTTPS. A GitHub release without a signed manifest is shown with a link to its page and never
  installed.
- **Choice:** the newest correctly signed version wins. If the sources describe different files for the same version,
  VIZEN installs nothing. Older versions are never offered (no downgrades).
- **Download:** only when the user asks, or in the background if "Download updates in the background" is on (default
  off). Saved to `%LOCALAPPDATA%\VIZEN\updates`, resumed after an interruption (HTTP range), retried three times per
  address, then the mirrors. The file is kept only if its size and SHA-256 match the manifest, its version information
  says VIZEN and the announced version, and, if it is digitally signed, Windows accepts the signature. A file that fails
  is deleted; nothing unverified is ever run. The file is checked again right before it is run.
- **Install:** only after the user presses "Restart and install" (the confirmation names the open overlays and OBS
  outputs that will close). VIZEN starts the installer with `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS
  /RELAUNCH=1` and closes cleanly; Windows asks for administrator permission; the installer replaces the program files
  and starts VIZEN again. If the permission request is declined, nothing changes.
- **User data** (settings, Smart Notes, Smart Draw drawings, audio profiles, sources, overlay profiles, media library)
  lives in `%LOCALAPPDATA%\VIZEN`, which the installer never touches.
- **Browser extension:** the update does not change the VIZEN Connector registration; the native host manifest is
  rewritten at the next start with the current extension IDs (Chrome Web Store, Edge Add-ons, unpacked).

## Making a release

1. Set `<Version>` in `VIZEN.App/VIZEN.App.csproj` (for example `0.4.6`), and optionally write
   `docs/release-notes/0.4.6.en.txt` and `0.4.6.ro.txt`.
2. Commit, then push a tag with the same version: `git tag v0.4.6` and `git push origin v0.4.6` (public repository).
3. The **Release** workflow (`.github/workflows/release.yml`) tests VIZEN, builds the installer, writes and signs the
   manifest, and creates a **draft** GitHub release with `VIZEN-Setup-x64.exe`, `latest.json` and `latest.json.sig`.
   If the repository variables `MSIX_IDENTITY_NAME` and `MSIX_PUBLISHER` are set, it also builds the Microsoft Store
   package as a workflow artifact.
4. Review the draft and publish it on GitHub. From now on GitHub's fallback offers the new version.
5. Download `latest.json` and `latest.json.sig` from the release and upload them, unchanged, to
   `https://vizen.ro/updates/` (see `website/README.md`). From now on vizen.ro offers it too.
6. Microsoft Store: upload the MSIX in Partner Center as a new submission (the Store updates Store copies itself).

Without GitHub Actions the same is done locally: `scripts\build-installer.ps1`, then
`scripts\make-update-manifest.ps1 -NotesEn "…" -NotesRo "…"` (uses the local key), then create the release by hand
with the three files from `artifacts\installer` and `artifacts\update`.

One-time setup for the workflow: add the repository secret `UPDATE_SIGNING_KEY` with the full content of the private
key file (GitHub → Settings → Secrets and variables → Actions → New repository secret).

## Tests

Automated (`VIZEN.Tests/UpdateTests.cs`, fake HTTP server and test key): no update; new version on vizen.ro; vizen.ro
down → GitHub; GitHub down → vizen.ro; both down; manifest changed after signing; manifest signed with another key;
different versions on the two sources; same version with different files; older version never offered; update from an
older version; unsigned GitHub release shown but not installable; non-HTTPS addresses refused; verified download;
interrupted download resumed with a range request; changed installer refused and deleted; mirror used when the main
address fails; file that is not a VIZEN installer refused; verified download reused; channel detection of a
non-installed build; installer never deletes user data; the built-in key is valid.

End to end (`tools/perf/updateflow.ps1`, private tools): a local feed signed with the real key announced 0.4.6 for the
real 0.4.5 installer on GitHub; VIZEN showed the update, downloaded 56 MB and refused it ("installer version does not
match"), leaving no file; two overlays stayed at 0 % dropped frames before and after five checks and the download.

Not tested automatically: a real installation over an older installed version (it installs into Program Files and needs
administrator rights; test it by hand: install VIZEN 0.4.5 with the installer, publish a newer signed release, use
Settings → Updates), a real Microsoft Store copy (needs the Store-signed package), and the App Installer channel (not
offered).
