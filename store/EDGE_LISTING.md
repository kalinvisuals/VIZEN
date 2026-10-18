# VIZEN Connector — Microsoft Edge Add-ons listing

Everything to paste into Partner Center (https://partner.microsoft.com/dashboard/microsoftedge). Images are in this
folder; the package is built by `scripts/package-extension.ps1` into `artifacts/extension/`.

## Package

- File: `artifacts/extension/vizen-connector-1.0.0.zip`
- Availability: Public, all markets
- Category: **Productivity**

## Properties

- Privacy policy URL: `https://github.com/kalinvisuals/VIZEN/blob/main/PRIVACY.md` (later: `https://www.vizen.ro/privacy`)
- Website URL: `https://www.vizen.ro`
- Support contact: `hi@vizen.ro`
- Mature content: No

## Store listing — English (United States)

**Name:** VIZEN Connector

**Short description** (shown in search):
See every browser tab in the VIZEN app: switch to it, mute it, pop it out or mirror it live for capture. Stays on your PC.

**Description:**

VIZEN Connector links your browser to VIZEN, the free and open-source overlay app for Windows. Windows can only capture
windows, not tabs; with this extension VIZEN sees every tab on its own.

What you can do from VIZEN:
• See every tab as its own source, with its title, site and type (calls, media, other)
• Switch to a tab in one click
• Mute or unmute a tab, also from VIZEN's Smart Audio Mixer
• Move a tab into its own window, only when you ask
• Mirror a tab live: a small window shows the tab even when it is in the background, ready to capture as an overlay
  or in OBS

Private by design:
• Only tab titles and site names (never full addresses or page content) go to the VIZEN app on your PC
• Incognito / InPrivate tabs are never shared
• No servers, no accounts, no analytics: nothing leaves your computer
• No access to page content, no scripts added to web pages

Requires the free VIZEN app for Windows 10/11: https://github.com/kalinvisuals/VIZEN
In VIZEN, open Browser and press Connect once.

**Search terms** (up to 7): overlay, tabs, OBS, streaming, capture, mute tab, picture in picture

**Logo:** `logo-300.png` · **Small promotional tile:** `promo-440x280.png`
**Screenshots (1280 x 800):** `screenshot-1.png`, `screenshot-2.png`, `screenshot-3.png`

## Store listing — Română (opțional, „Add language”)

**Name:** VIZEN Connector

**Short description:**
Vezi fiecare tab în aplicația VIZEN: treci la el, pune-l pe mute, mută-l sau oglindește-l live. Totul rămâne pe PC.

**Description:**

VIZEN Connector leagă browserul de VIZEN, aplicația gratuită și open source de overlay-uri pentru Windows. Windows poate
captura doar ferestre, nu taburi; cu această extensie VIZEN vede fiecare tab separat.

Ce poți face din VIZEN:
• Vezi fiecare tab ca sursă separată, cu titlul, site-ul și tipul lui (apeluri, media, altele)
• Treci la un tab dintr-un click
• Oprești sau pornești sunetul unui tab, și din Smart Audio Mixer
• Muți un tab într-o fereastră proprie, doar când ceri
• Oglindești un tab live: o fereastră mică arată tabul chiar și când e în fundal, gata de captură ca overlay sau în OBS

Privat din start:
• Doar titlurile taburilor și numele site-urilor (niciodată adresa completă sau conținutul paginii) ajung la aplicația
  VIZEN de pe PC-ul tău
• Taburile incognito / InPrivate nu sunt trimise niciodată
• Fără servere, fără conturi, fără statistici: nimic nu pleacă de pe calculator
• Fără acces la conținutul paginilor, fără scripturi adăugate în pagini

Necesită aplicația gratuită VIZEN pentru Windows 10/11: https://github.com/kalinvisuals/VIZEN
În VIZEN, deschide Browser și apasă o dată Conectează.

## Notes for certification (for the Microsoft testers)

VIZEN Connector works together with the free desktop app VIZEN (Windows 10/11), through native messaging
(host name `com.vizen.connector`). Without the app the popup shows "VIZEN not connected" and nothing else happens.

To test:
1. Install VIZEN from https://github.com/kalinvisuals/VIZEN/releases (VIZEN-Setup-x64.exe) and start it.
2. In VIZEN, open the "Browser" page and press "Connect" (this registers the native messaging host for the current
   user; no administrator rights).
3. Wait up to 30 seconds (or restart Edge). The extension popup now shows "Connected" and VIZEN lists every tab under "Sources → Browser tabs".
4. From VIZEN: switch to a tab, mute a tab, or press "Separate window". Tabs playing sound also appear in VIZEN's
   "Smart Audio Mixer" page with a mute button.
5. Live mirror: open the extension popup on a tab and press "Mirror this tab live for VIZEN". A small window titled
   "VIZEN Tab · …" shows the tab; Edge asks once to let it stay live while covered ("Keep live").

Permissions: `tabs` (titles and host names for the list), `nativeMessaging` (local link to the VIZEN app),
`tabCapture` (live mirror, only after the user's click). No host permissions, no content scripts, no network requests.
Source code (GPL-3.0): https://github.com/kalinvisuals/VIZEN/tree/main/browser-extension
