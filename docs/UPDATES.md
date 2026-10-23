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
folders, copied to offline USB drives) and in the GitHub secret `UPDATE_SIGNING_KEY`. A second, backup key
(`update-signing-key-backup.pem`) is also trusted by VIZEN and kept only offline: if the main key is lost or exposed,
releases are signed with the backup key and the next version replaces the lost key with a new one.

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

A published version is never changed: its installer, manifest and checksums stay as they are. Any change, even a
small fix, gets a new version (for example 0.4.7 after 0.4.6). The workflow refuses a tag whose release already exists.

1. Set `<Version>` in `VIZEN.App/VIZEN.App.csproj` (for example `0.4.7`). This one value becomes the About page
   version, the EXE version information, the installer version and its Installed apps entry, the tag, the manifest and
   the version the website shows.
2. Write `docs/release-notes/0.4.7.en.txt` and `0.4.7.ro.txt` (required; shown in VIZEN's update notice and on GitHub)
   and add the version to `website/site/changelog/index.html` (EN and RO).
3. Commit to `main` (private repository), export to the public repository, check that CI passes there.
4. Approve the release by pushing a tag with the same version in the public repository:
   `git tag v0.4.7` and `git push origin v0.4.7`. Ordinary commits never make a release.
5. The **Release** workflow (`.github/workflows/release.yml`) then, in order, and stops at the first failure:
   checks that the tag matches the version, is on `main`, has release notes and has no release yet; restores and runs
   the tests; builds the self-contained Release build and the installer; installs the installer silently on the
   runner, checks the installed files, version and Installed apps entry, and uninstalls it
   (`scripts\test-installer.ps1`); writes and signs `latest.json` with the `UPDATE_SIGNING_KEY` secret and verifies the
   signature with the keys built into VIZEN; writes the release text and `SHA256SUMS.txt`
   (`scripts\make-release-notes.ps1`); keeps `latest.json` + `.sig` as the workflow artifact `vizen-ro-updates-<version>`;
   and only then creates a **draft** GitHub release with `VIZEN-Setup-x64.exe`, `latest.json`, `latest.json.sig` and
   `SHA256SUMS.txt`. If the repository variables `MSIX_IDENTITY_NAME` and `MSIX_PUBLISHER` are set, it also builds the
   Microsoft Store package as a workflow artifact.
6. Review the draft (notes, size, SHA-256) and press **Publish release** on GitHub. From now on GitHub's fallback
   offers the new version to installed copies.
7. Upload the two files from the `vizen-ro-updates-<version>` artifact (identical to the release assets), unchanged,
   to `https://vizen.ro/updates/`, together with the website files if they changed (see `website/README.md`). From
   now on vizen.ro offers the version too, and its download section shows it (the page reads the same `latest.json`).
8. Check: `https://vizen.ro/updates/latest.json` and the GitHub release show the same version and SHA-256, and an
   installed older VIZEN finds the update (Settings → Updates → Check for updates).
9. Microsoft Store (only when a new Store build is wanted): `scripts\build-msix.ps1` (or the workflow artifact), then
   a new submission in Partner Center. The Store updates Store copies itself; they never use vizen.ro or GitHub.

Without GitHub Actions the same is done locally: `scripts\build-installer.ps1`, `scripts\test-installer.ps1` (as
administrator, on a PC without VIZEN installed), `scripts\make-update-manifest.ps1 -NotesEn "…" -NotesRo "…"` (uses the
local key), `scripts\make-release-notes.ps1`, then create the release by hand with the files from
`artifacts\installer`, `artifacts\update` and `artifacts\release`.

One-time setup for the workflow: add the repository secret `UPDATE_SIGNING_KEY` with the full content of the private
key file (GitHub → Settings → Secrets and variables → Actions → New repository secret).

## Tests

Automated (`VIZEN.Tests/UpdateTests.cs`, fake HTTP server and test key): no update; new version on vizen.ro; vizen.ro
down → GitHub; GitHub down → vizen.ro; both down; manifest changed after signing; manifest signed with another key;
different versions on the two sources; same version with different files; older version never offered; update from an
older version; unsigned GitHub release shown but not installable; non-HTTPS addresses refused; verified download;
interrupted download resumed with a range request; changed installer refused and deleted; mirror used when the main
address fails; file that is not a VIZEN installer refused; verified download reused; channel detection of a
non-installed build; installer never deletes user data; the built-in key is valid; downloaded installers of the
installed version or older are removed at start.

End to end (`tools/perf/updateflow.ps1`, private tools): a local feed signed with the real key announced 0.4.6 for the
real 0.4.5 installer on GitHub; VIZEN showed the update, downloaded 56 MB and refused it ("installer version does not
match"), leaving no file; two overlays stayed at 0 % dropped frames before and after five checks and the download.

By hand, 2026-10-23 (0.4.7): the official GitHub 0.4.6 installer installed, then updated by VIZEN 0.4.6 itself to 0.4.7
(signed local test manifest, real key; the installer placed where VIZEN downloads it, because the v0.4.7 address did
not exist yet): verified, "Restart and install", Windows' administrator prompt, VIZEN restarted as 0.4.7, one
Installed apps entry, data unchanged, the used installer removed at start. Also: vizen.ro unreachable -> the real
GitHub release read and verified ("up to date"); a changed installer refused and deleted; a missing download (404)
reported, nothing kept.

Not tested automatically: the real installation over an older version (Program Files, administrator rights; done by
hand as above), a real Microsoft Store copy (needs the Store-signed package), and the App Installer channel (not
offered).
