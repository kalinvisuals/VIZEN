# VIZEN — Microsoft Store listing

Everything to paste into Partner Center (Apps and games → VIZEN). The package is built by `scripts\build-msix.ps1`
with the values from Product management → Product identity.

## Pricing and availability

- Markets: all · Visibility: Public · Pricing: **Free** · Free trial: none

## Properties

- Category: **Productivity** (subcategory, if asked: Utilities & tools)
- Privacy policy URL: `https://github.com/kalinvisuals/VIZEN/blob/main/PRIVACY.md`
- Website: `https://github.com/kalinvisuals/VIZEN` (later `https://www.vizen.ro`)
- Support contact: `https://github.com/kalinvisuals/VIZEN/issues` (or `hi@vizen.ro` once it receives mail)
- System requirements: Windows 10 version 2004 or later, 64-bit; DirectX 11 graphics; 4 GB RAM minimum, 8 GB recommended
- Product declarations: none of the special cases apply (no in-app purchases, no ads)

## Age ratings (IARC questionnaire)

Category: **Productivity / Utility**. Answer *No* to violence, sexual content, gambling, drugs, language. Answer *No* to
sharing location and to user-to-user communication (VIZEN shows windows the user already has open; it has no chat or
network features of its own). It can show web pages the user opens in a browser source: answer *Yes* to "unrestricted
internet access through a browser" if the questionnaire asks.

## Store listing — English (United States)

**Product name:** VIZEN

**Short description:**
Free, open-source overlays for Windows: any window or region on top of your game, in OBS or in a composition.

**Description:**

VIZEN turns any window into a live overlay. Pick a video call, a live chat, a video, a web page or a part of a game,
crop the area you need and keep it on top of your game, in a clean window for OBS, or in a composition.

GAMING
• Live overlays from any window or cropped region, always on top of windowed and borderless games
• Click-through, opacity, snap and one-key show / hide, even while you play
• Measured performance: source and display frame rate, dropped frames and latency for every capture

CALLS
• Discord, Zoom, Teams, WhatsApp and browser calls detected automatically
• Mirror a whole call, or crop each participant into an overlay or an OBS window

STUDIO / OBS
• Clean output windows for OBS Window Capture with exact sizes and a chroma-key background
• Compositions: several captures, logos, GIFs and videos in one window
• Media layers and schedules for sponsor logos and promos

SMART TOOLS
• Smart Notes: notes and checklists that float over your desktop and games
• Smart Draw: draw arrows, shapes and text over anything, with pass-through
• Smart Audio Mixer: a volume for every app, profiles, a floating mixer and Smart Audio Focus, which lowers your music
  while your friends talk
• Browser tabs: with the optional VIZEN Connector extension for Edge and Chrome, every tab becomes its own source

SAFE AND PRIVATE
• No DLL injection, no game memory access, no anti-cheat bypass: only public Windows APIs
• No account, no ads, no tracking: everything stays on your PC
• Free and open source (GPL-3.0): github.com/kalinvisuals/VIZEN

Games must run in windowed or borderless mode; exclusive fullscreen games cannot show overlays without injection,
which VIZEN never uses.

**What's new in this version:** First Microsoft Store release.

**Product features** (one per line, up to 20):
- Live overlays from any window or region
- Click-through, opacity and hotkeys during games
- Calls mode with per-participant crops
- Clean OBS output windows and compositions
- Smart Notes, Smart Draw and Smart Audio Mixer
- Smart Audio Focus lowers music while friends talk
- Every browser tab as its own source (with VIZEN Connector)
- Measured frame rate, dropped frames and latency
- No injection, no account, no tracking
- Free and open source (GPL-3.0)

**Search terms** (up to 7): overlay, OBS, streaming, gaming overlay, audio mixer, screen draw, picture in picture

**Screenshots** (1920 x 1008, from `docs\screenshots`, in this order): `dashboard.png`, `gaming.png`, `sources.png`,
`audio-mixer.png`, `calls.png`, `studio-obs.png`, `smart-draw.png`, `smart-notes.png`, `browser-tabs.png`

**Store logos:** 1:1 box art 300 x 300: `store\logo-300.png` (optional ones can stay empty)

## Store listing — Română

**Short description:**
Overlay-uri gratuite și open source pentru Windows: orice fereastră sau zonă peste joc, în OBS sau într-o compoziție.

**Description:**

VIZEN transformă orice fereastră într-un overlay live. Alegi un apel video, un chat live, un video, o pagină web sau o
parte dintr-un joc, decupezi zona de care ai nevoie și o păstrezi peste joc, într-o fereastră curată pentru OBS sau
într-o compoziție.

GAMING
• Overlay-uri live din orice fereastră sau zonă decupată, mereu peste jocurile în fereastră și fără margini
• Click-through, transparență, aliniere și afișare / ascundere dintr-o tastă, chiar în timpul jocului
• Performanță măsurată: cadre pe secundă la sursă și pe ecran, cadre pierdute și latență pentru fiecare captură

APELURI
• Apelurile din Discord, Zoom, Teams, WhatsApp și browser sunt detectate automat
• Oglindești tot apelul sau decupezi fiecare participant într-un overlay sau într-o fereastră OBS

STUDIO / OBS
• Ferestre curate pentru OBS Window Capture, cu dimensiuni exacte și fundal chroma-key
• Compoziții: mai multe capturi, logo-uri, GIF-uri și video într-o singură fereastră
• Straturi media și programări pentru logo-uri de sponsori și promo-uri

UNELTE SMART
• Smart Notes: notițe și liste care plutesc peste desktop și jocuri
• Smart Draw: desenezi săgeți, forme și text peste orice, cu trecerea clickurilor prin desen
• Smart Audio Mixer: volum separat pentru fiecare aplicație, profiluri, mixer plutitor și Smart Audio Focus, care
  coboară muzica atunci când vorbesc prietenii
• Taburi din browser: cu extensia opțională VIZEN Connector pentru Edge și Chrome, fiecare tab devine o sursă separată

SIGUR ȘI PRIVAT
• Fără injecție DLL, fără acces la memoria jocurilor, fără ocolirea anti-cheat: doar API-uri publice Windows
• Fără cont, fără reclame, fără urmărire: totul rămâne pe PC-ul tău
• Gratuit și open source (GPL-3.0): github.com/kalinvisuals/VIZEN

Jocurile trebuie să ruleze în fereastră sau fără margini; jocurile în ecran complet exclusiv nu pot afișa overlay-uri
fără injecție, pe care VIZEN nu o folosește niciodată.

**Noutăți:** Prima versiune în Microsoft Store.

**Search terms:** overlay, OBS, streaming, gaming overlay, mixer audio, desen pe ecran, picture in picture

## Submission options — restricted capabilities

Partner Center asks why the package uses restricted capabilities:

- **runFullTrust:** "VIZEN is a desktop (WPF, .NET) app: it captures windows with Windows Graphics Capture, shows
  always-on-top overlay windows, registers global hotkeys and controls per-app audio volume, which need a full-trust
  desktop process."
- **unvirtualizedResources:** "VIZEN keeps its user data in %LOCALAPPDATA%\VIZEN (shared with the VIZEN desktop
  installer, so users keep their settings when they switch), and its optional browser extension, VIZEN Connector,
  needs a native messaging host registration under HKEY_CURRENT_USER\Software\Google\Chrome\NativeMessagingHosts and
  ...\Microsoft\Edge\NativeMessagingHosts that the browsers can read. With write virtualization the browsers would not
  see that registration. VIZEN writes only these locations, only for the current user, and removes the registration
  when the user presses Disconnect."

## Notes for certification

VIZEN is a free, open-source overlay app (GPL-3.0, https://github.com/kalinvisuals/VIZEN). No account or sign-in is
needed. To test: start VIZEN, open any window (for example a browser with a video), select it under Sources and press
"Create overlay": a live copy appears on top of other windows. Smart Notes, Smart Draw and Smart Audio Mixer are on the
left menu. The optional browser extension (VIZEN Connector, Edge Add-ons / Chrome Web Store) is not required.
VIZEN does not inject into other processes and uses only public Windows APIs (Windows Graphics Capture, Core Audio,
RegisterHotKey).
