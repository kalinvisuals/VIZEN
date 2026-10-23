// VIZEN website: English / Romanian, screenshot gallery, reveal on scroll, latest release from GitHub.
(() => {
  const ro = {
    "skip": "Sari la conținut",
    "nav.features": "Funcții", "nav.tour": "Tur", "nav.safety": "Siguranță", "nav.download": "Descarcă", "nav.get": "Ia VIZEN",
    "hero.pillTag": "Gratuit · Open source", "hero.pill": "Overlay-uri pentru Windows 10 și 11",
    "hero.title1": "Orice fereastră.", "hero.title2": "Peste jocul tău.",
    "hero.lead": "Alegi un apel video, un chat live, un video sau o parte dintr-un joc, decupezi ce îți trebuie și îl păstrezi live peste joc, într-o fereastră curată pentru OBS sau într-o compoziție.",
    "hero.download": "Descarcă pentru Windows", "hero.tour": "Vezi cum arată",
    "hero.meta1": "Windows 10 / 11, 64-bit", "hero.meta2": "Fără cont", "hero.meta3": "GPL-3.0",
    "hero.alt": "Modul Gaming din VIZEN cu un overlay live al unui joc, la 53 de cadre pe secundă și fără cadre pierdute",
    "features.over": "01 — Ce face", "features.title": "O aplicație. Toate overlay-urile de care ai nevoie.",
    "features.lead": "Construit pe aceeași tehnologie de captură ca Instrumentul de decupare din Windows și OBS, măsurat cadru cu cadru.",
    "f.gaming.t": "Overlay-uri pentru jocuri", "f.gaming.d": "Orice fereastră sau zonă decupată, mereu peste jocurile în fereastră și fără margini. Click-through și afișare / ascundere dintr-o tastă.",
    "f.calls.t": "Modul Apeluri", "f.calls.d": "Apelurile din Discord, Zoom, Teams, WhatsApp și browser sunt găsite automat. Oglindești apelul sau decupezi fiecare persoană.",
    "f.obs.t": "Studio / OBS", "f.obs.d": "Ferestre curate pentru OBS Window Capture, dimensiuni exacte, chroma key și compoziții cu logo-uri, GIF-uri și video.",
    "f.draw.t": "Smart Draw și Notes", "f.draw.d": "Desenezi săgeți, forme și text peste orice. Notițe și liste care plutesc peste desktop și jocuri.",
    "f.audio.t": "Smart Audio Mixer", "f.audio.d": "Volum separat pentru fiecare aplicație, profiluri, mixer plutitor și Smart Audio Focus, care coboară muzica atunci când vorbesc prietenii.",
    "f.tabs.t": "Fiecare tab din browser", "f.tabs.d": "Cu VIZEN Connector, gratuit pentru Edge și Chrome, fiecare tab e o sursă separată: treci la el, îl pui pe mute, îl muți sau îl oglindești live.",
    "tour.over": "02 — Tur", "tour.title": "Făcut pentru felul în care joci și faci stream.",
    "r1.over": "Surse", "r1.t": "Tot ce e deschis, sortat pentru tine.", "r1.d": "Ferestrele sunt grupate în Browsere, Apeluri, Media, Camere și Altele, cu previzualizare live pentru cea aleasă.",
    "r1.l1": "Un click până la overlay sau fereastră OBS", "r1.l2": "Mai multe zone decupate dintr-o singură fereastră", "r1.l3": "Taburile din browser apar separat",
    "r1.alt": "Pagina Surse din VIZEN cu ferestre demo, previzualizare live și taburi din browser",
    "r2.over": "Smart Audio Mixer", "r2.t": "Mixul tău, peste joc.", "r2.d": "Setezi volumul fiecărei aplicații, salvezi mixuri pentru gaming, stream sau ședințe și le controlezi dintr-un mixer mic, plutitor.",
    "r2.l1": "Smart Audio Focus coboară muzica când vorbesc prietenii", "r2.l2": "Mute pe taburi separate din browser", "r2.l3": "Urmează volumul din Windows",
    "r2.alt": "Smart Audio Mixer din VIZEN cu volum pentru fiecare aplicație și taburi din browser",
    "r3.over": "Performanță", "r3.t": "Măsurat, nu ghicit.", "r3.d": "Cadre pe secundă la sursă și pe ecran, cadre pierdute, latență și ritmul cadrelor pentru fiecare captură, plus procesorul, placa video și memoria folosite de VIZEN.",
    "r3.l1": "Auto Performance explică cadrele pierdute", "r3.l2": "Calea GPU: cadrele rămân pe placa video", "r3.l3": "Imagine de test ca să-ți verifici PC-ul",
    "r3.alt": "Pagina Performanță din VIZEN cu cadre pe secundă măsurate",
    "gal.over": "03 — Fiecare pagină", "gal.title": "Privește mai de aproape.", "gal.dashboard": "Panou", "gal.calls": "Apeluri", "gal.tabs": "Taburi din browser", "gal.settings": "Setări",
    "gal.caption": "Capturile folosesc conținut demo.",
    "safe.over": "04 — Siguranța în jocuri", "safe.title": "Overlay-uri fără să atingi jocul.",
    "safe.lead": "Overlay-urile VIZEN sunt ferestre obișnuite, mereu deasupra, ca cele afișate de Discord, Steam sau NVIDIA.",
    "safe.yes": "Ce folosește VIZEN", "safe.y1": "Windows Graphics Capture, API-ul public al Instrumentului de decupare și al OBS",
    "safe.y2": "Ferestre obișnuite, mereu deasupra, pentru overlay-uri", "safe.y3": "Sesiunile audio Windows pentru volumul pe aplicații", "safe.y4": "Scurtături înregistrate, fără interceptarea tastaturii",
    "safe.no": "Ce nu face VIZEN niciodată", "safe.n1": "Injectează cod sau DLL-uri în jocuri", "safe.n2": "Citește sau scrie memoria jocurilor", "safe.n3": "Ocolește anti-cheat sau DRM", "safe.n4": "Automatizează tastatura sau trimite date undeva",
    "safe.note": "Rulează jocurile în fereastră sau fără margini. Nimeni nu poate promite cum decide fiecare anti-cheat; VIZEN nu face nimic din ce interzic ele.",
    "os.over": "Gratuit și open source", "os.title": "Fără reclame. Fără cont. Fără urmărire.",
    "os.lead": "VIZEN este software liber, sub licența GNU GPL v3. Oricine poate citi codul, verifica ce face și ajuta la îmbunătățirea lui.",
    "os.github": "Vezi pe GitHub", "os.privacy": "Politica de confidențialitate",
    "dl.over": "05 — Descarcă", "dl.title": "Ia VIZEN gratuit.", "dl.lead": "Windows 10 (2004 sau mai nou) sau Windows 11, 64-bit, cu placă video DirectX 11.",
    "dl.ready": "Disponibil", "dl.review": "În verificare", "dl.edgeReady": "Edge: disponibil", "dl.chromeReview": "Chrome: în verificare", "dl.edgeGet": "Instalează în Microsoft Edge",
    "dl.direct.t": "Installer", "dl.direct.d": "Ultima versiune de pe GitHub. VIZEN caută singur actualizări, le verifică și le instalează doar când îi spui tu.",
    "dl.direct.b": "Descarcă VIZEN-Setup-x64.exe", "dl.version": "Versiunea", "dl.notes": "note și SHA-256",
    "dl.smartscreen": "Installerul nu are încă semnătură digitală: dacă Windows afișează „Windows a protejat PC-ul”, alege Mai multe informații → Rulează oricum.",
    "dl.store.d": "Semnat de Microsoft, se instalează dintr-un click și se actualizează automat.",
    "dl.ext.d": "Extensie opțională pentru browser: fiecare tab din Edge, Chrome sau Brave devine o sursă separată. Trimite doar titlurile taburilor și numele site-urilor, doar către VIZEN de pe PC-ul tău.",
    "req.title": "Cerințe de sistem", "req.min": "Minim", "req.rec": "Recomandat",
    "req.os.min": "Windows 10 versiunea 2004 (build 19041) sau Windows 11, 64-bit", "req.os.rec": "Windows 11, 64-bit",
    "req.cpu": "Procesor", "req.cpu.min": "Intel Core i3-6100 / i3-8130U, AMD Ryzen 3 2200G sau mai nou (4 fire)", "req.cpu.rec": "Intel Core i5-10400 / i5-8250U, AMD Ryzen 5 3600 / 5500U sau mai nou (8 fire sau mai multe)",
    "req.ram": "Memorie", "req.gpu": "Placă video", "req.gpu.min": "DirectX 11 cu accelerare hardware (merge și cea integrată)", "req.gpu.rec": "Placă video dedicată sau una integrată recentă",
    "req.disk": "Spațiu pe disc", "req.expect": "La ce să te aștepți", "req.expect.min": "Unul sau două overlay-uri la 720p, 30 fps", "req.expect.rec": "Mai multe surse 1080p la 60 fps",
    "req.note": "Sursele browser folosesc Microsoft Edge WebView2 (inclus în Windows 11 și pe majoritatea PC-urilor cu Windows 10). VIZEN Connector are nevoie de Edge, Chrome sau Brave 116 sau mai nou. Pagina Performanță din VIZEN îți măsoară propriul PC.",
    "faq.title": "Întrebări",
    "q1": "VIZEN e chiar gratuit?", "a1": "Da. VIZEN e gratuit și open source (GNU GPL v3), fără reclame, fără cont și fără funcții plătite.",
    "q2": "Merge cu jocuri pe tot ecranul?", "a2": "Merge cu jocurile în fereastră și fără margini (ecran complet în fereastră). Ecranul complet exclusiv desenează direct pe ecran, așa că nicio fereastră nu poate apărea peste el fără injecție în joc, pe care VIZEN nu o face niciodată. VIZEN îți spune când detectează ecran complet exclusiv.",
    "q3": "Pot primi ban?", "a3": "VIZEN nu atinge jocul: fără injecție, fără acces la memorie, fără automatizarea tastaturii. Overlay-urile sunt ferestre normale, ca ale Discord sau Steam. Nimeni nu poate garanta fiecare decizie anti-cheat, dar VIZEN nu face nimic din ce se știe că duce la ban.",
    "q4": "VIZEN îmi colectează datele?", "a4": "Nu. Nu există telemetrie și nici cont; setările rămân în folderul tău de utilizator. Vezi politica de confidențialitate.",
    "q5": "Cum se fac actualizările?", "a5": "Versiunea din Microsoft Store se actualizează prin Store. Versiunea cu installer verifică vizen.ro și GitHub, verifică semnătura digitală și fișierul și instalează doar după ce confirmi.",
    "foot.license": "Software liber, sub licența GNU GPL v3", "nav.help": "Ghid", "foot.help": "Ghid", "foot.changelog": "Noutăți", "foot.privacy": "Confidențialitate", "foot.support": "Suport",
  };

  const english = new Map();

  // Replaces only the text of an element, keeping icons and other child elements.
  function setText(el, text) {
    const nodes = [...el.childNodes].filter(n => n.nodeType === Node.TEXT_NODE && n.textContent.trim());
    if (nodes.length) {
      nodes[nodes.length - 1].textContent = text;
      nodes.slice(0, -1).forEach(n => (n.textContent = ""));
    } else if (!el.children.length) {
      el.textContent = text;
    } else {
      el.append(text);
    }
  }

  function apply(lang) {
    document.documentElement.lang = lang;
    document.querySelectorAll("[data-i18n]").forEach(el => {
      const key = el.dataset.i18n;
      if (!english.has(key)) english.set(key, el.textContent.trim());
      setText(el, lang === "ro" && ro[key] ? ro[key] : english.get(key));
    });
    document.querySelectorAll("[data-i18n-alt]").forEach(el => {
      const key = el.dataset.i18nAlt;
      if (!english.has("alt:" + key)) english.set("alt:" + key, el.alt);
      el.alt = lang === "ro" && ro[key] ? ro[key] : english.get("alt:" + key);
    });
    document.querySelectorAll("[data-src-ro]").forEach(el => {
      if (!el.dataset.srcEn) el.dataset.srcEn = el.src;
      el.src = lang === "ro" ? el.dataset.srcRo : el.dataset.srcEn;
    });
    document.querySelectorAll("[data-set-lang]").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.setLang === lang)));
    try { localStorage.setItem("vizen-lang", lang); } catch { /* private mode */ }
  }

  let saved = null;
  try { saved = localStorage.getItem("vizen-lang"); } catch { /* private mode */ }
  const start = saved || ((navigator.language || "en").toLowerCase().startsWith("ro") ? "ro" : "en");
  document.querySelectorAll("[data-set-lang]").forEach(b => b.addEventListener("click", () => apply(b.dataset.setLang)));
  apply(start);

  // gallery
  const img = document.getElementById("gallery-img");
  if (img) document.querySelectorAll("[data-shot]").forEach(tab => tab.addEventListener("click", () => {
    document.querySelectorAll("[data-shot]").forEach(t => t.setAttribute("aria-selected", String(t === tab)));
    img.src = `/assets/img/${tab.dataset.shot}.png`;
    img.alt = `VIZEN — ${tab.textContent.trim()}`;
  }));

  // reveal on scroll
  const io = "IntersectionObserver" in window ? new IntersectionObserver(entries => entries.forEach(e => {
    if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); }
  }), { rootMargin: "0px 0px -8% 0px" }) : null;
  document.querySelectorAll(".reveal").forEach(el => io ? io.observe(el) : el.classList.add("in"));

  // latest release (version, size and address of the installer): the update manifest on vizen.ro, the same file VIZEN
  // reads, so the page and the app always agree; the public GitHub API when it cannot be read
  if (document.querySelector("[data-release-version]")) {
    const official = /^https:\/\/(github\.com\/kalinvisuals\/VIZEN\/releases\/download\/|vizen\.ro\/download\/)/;
    const show = (version, size, url) => {
      document.querySelectorAll("[data-release-version]").forEach(el => (el.textContent = el.textContent.startsWith("v") ? `v${version}` : version));
      if (size) document.querySelectorAll("[data-release-size]").forEach(el => (el.textContent = `${Math.round(size / 1048576)} MB`));
      if (url && official.test(url)) document.querySelectorAll("[data-release-download]").forEach(el => (el.href = url));
    };
    const fromGitHub = () => fetch("https://api.github.com/repos/kalinvisuals/VIZEN/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
      .then(r => (r.ok ? r.json() : null))
      .then(release => {
        if (!release || !release.tag_name) return;
        const asset = (release.assets || []).find(a => a.name === "VIZEN-Setup-x64.exe");
        show(release.tag_name.replace(/^v/, ""), asset && asset.size, asset && asset.browser_download_url);
      });
    fetch("/updates/latest.json", { cache: "no-cache" })
      .then(r => (r.ok ? r.json() : Promise.reject(new Error("no manifest"))))
      .then(m => {
        if (m.product !== "VIZEN" || !/^\d+\.\d+\.\d+$/.test(m.version || "") || !m.installer) throw new Error("bad manifest");
        show(m.version, m.installer.size, m.installer.url);
      })
      .catch(() => fromGitHub())
      .catch(() => { /* offline: keep the built-in values */ });
  }
})();
