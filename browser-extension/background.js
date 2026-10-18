// VIZEN Connector - background service worker.
// Sends VIZEN (running on this PC) the list of open tabs: title, site name (host only, never the full address),
// whether a tab plays sound or is muted. Incognito tabs are never sent. VIZEN can ask to switch to a tab, mute it,
// or move it into its own window - only when the user asks for it in VIZEN. Everything stays on this computer:
// Chrome's native messaging talks to VIZEN directly, nothing is sent over the network.

const HOST = "com.vizen.connector";
let port = null;
let sendTimer = null;
let retryTimer = null;

function browserName() {
  const brands = (navigator.userAgentData && navigator.userAgentData.brands) || [];
  if (brands.some(b => /Edge/i.test(b.brand))) return "edge";
  if (navigator.brave) return "brave";
  return "chrome";
}

function siteOf(url) {
  try {
    const u = new URL(url);
    return u.protocol === "http:" || u.protocol === "https:" ? u.hostname.replace(/^www\./, "") : "";
  } catch {
    return "";
  }
}

function connect() {
  if (port) return;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch {
    port = null;
    scheduleRetry();
    return;
  }
  port.onMessage.addListener(onCommand);
  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError; // VIZEN not connected yet (or closed): try again later
    port = null;
    scheduleRetry();
  });
  port.postMessage({ type: "hello", browser: browserName(), version: chrome.runtime.getManifest().version });
  scheduleSend();
}

function scheduleRetry() {
  if (retryTimer) return;
  retryTimer = setTimeout(() => { retryTimer = null; connect(); }, 30000);
}

// Many tab events arrive together (a page loading): send one list shortly after the last one.
function scheduleSend() {
  if (sendTimer) clearTimeout(sendTimer);
  sendTimer = setTimeout(sendTabs, 300);
}

async function sendTabs() {
  sendTimer = null;
  if (!port) { connect(); return; }
  const all = await chrome.tabs.query({});
  const tabs = all.filter(t => !t.incognito).map(t => ({
    id: t.id,
    windowId: t.windowId,
    title: t.title || "",
    site: siteOf(t.url || t.pendingUrl || ""),
    audible: !!t.audible,
    muted: !!(t.mutedInfo && t.mutedInfo.muted),
    active: !!t.active,
  }));
  try {
    port.postMessage({ type: "tabs", browser: browserName(), tabs });
  } catch {
    port = null;
  }
}

async function onCommand(message) {
  const tabId = message && message.tabId;
  try {
    switch (message && message.type) {
      case "activate": {
        const tab = await chrome.tabs.update(tabId, { active: true });
        await chrome.windows.update(tab.windowId, { focused: true });
        break;
      }
      case "mute":
        await chrome.tabs.update(tabId, { muted: !!message.muted });
        break;
      case "popout":
        // Only on the user's request in VIZEN: the tab moves into a window of its own, which VIZEN can capture.
        await chrome.windows.create({ tabId, focused: true });
        break;
      case "refresh":
        break;
      default:
        return;
    }
    reply(message.type, tabId, true, null);
  } catch (error) {
    reply(message.type, tabId, false, String((error && error.message) || error));
  }
  scheduleSend();
}

function reply(command, tabId, ok, error) {
  try {
    if (port) port.postMessage({ type: "result", browser: browserName(), command, tabId, ok, error });
  } catch {
    // VIZEN went away
  }
}

chrome.tabs.onCreated.addListener(scheduleSend);
chrome.tabs.onRemoved.addListener(scheduleSend);
chrome.tabs.onActivated.addListener(scheduleSend);
chrome.tabs.onAttached.addListener(scheduleSend);
chrome.tabs.onDetached.addListener(scheduleSend);
chrome.tabs.onReplaced.addListener(scheduleSend);
chrome.tabs.onUpdated.addListener((_, change) => {
  if ("title" in change || "url" in change || "audible" in change || "mutedInfo" in change) scheduleSend();
});
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);

// The popup asks for the connection state.
chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message && message.type === "status") {
    if (!port) connect();
    sendResponse({ connected: !!port });
  }
});

connect();
