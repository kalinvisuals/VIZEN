// Popup: connection state and the live mirror of the current tab (tabCapture needs this click on the tab).
const t = key => chrome.i18n.getMessage(key);
document.getElementById("intro").textContent = t("popupIntro");
document.getElementById("mirror").textContent = t("popupMirror");
document.getElementById("mirrorHint").textContent = t("popupMirrorHint");

chrome.runtime.sendMessage({ type: "status" }, response => {
  const state = document.getElementById("state");
  const connected = !!(response && response.connected);
  state.textContent = t(connected ? "popupConnected" : "popupNotConnected");
  state.classList.toggle("ok", connected);
});

document.getElementById("mirror").addEventListener("click", async () => {
  const button = document.getElementById("mirror");
  button.disabled = true;
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    // Allowed because the user opened this popup on that tab (the click is the permission).
    const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tab.id });
    const page = "mirror.html?id=" + encodeURIComponent(streamId) + "&title=" + encodeURIComponent(tab.title || "");
    await chrome.windows.create({ url: chrome.runtime.getURL(page), type: "popup", width: 960, height: 580, focused: false });
    window.close();
  } catch (error) {
    document.getElementById("error").textContent = t("popupMirrorFailed") + " " + ((error && error.message) || error);
    button.disabled = false;
  }
});
