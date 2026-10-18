// Live mirror of one tab (chrome.tabCapture stream), in its own small window that VIZEN captures like any window.
// The tab keeps working in the background; its sound plays here (a captured tab is otherwise silent in Chrome).
const params = new URLSearchParams(location.search);
const id = params.get("id");
const title = params.get("title") || "";
document.title = "VIZEN Tab · " + title;

function show(text) {
  const message = document.getElementById("message");
  message.textContent = text;
  message.style.display = "flex";
}

navigator.mediaDevices.getUserMedia({
  audio: { mandatory: { chromeMediaSource: "tab", chromeMediaSourceId: id } },
  video: { mandatory: { chromeMediaSource: "tab", chromeMediaSourceId: id, maxWidth: 1920, maxHeight: 1080, maxFrameRate: 60 } },
}).then(stream => {
  document.getElementById("video").srcObject = stream;
  keepDrawingWhenCovered();
  stream.getVideoTracks()[0].addEventListener("ended", () => show(chrome.i18n.getMessage("mirrorEnded")));
}).catch(error => show(chrome.i18n.getMessage("mirrorFailed") + " " + ((error && error.message) || error)));

// Windows does not draw a minimized window, so nothing (VIZEN, OBS) can capture it: it would turn black. Minimizing
// this window instead puts it back, behind the browser window the user was using, where it is still captured.
document.addEventListener("visibilitychange", async () => {
  if (document.visibilityState !== "hidden") {
    return;
  }

  const me = await chrome.windows.getCurrent();
  if (me.state !== "minimized") {
    return;
  }

  const behind = await chrome.windows.getLastFocused({ windowTypes: ["normal"] }).catch(() => null);
  await chrome.windows.update(me.id, { state: "normal", focused: false });
  if (behind && behind.id !== me.id && behind.state !== "minimized") {
    await chrome.windows.update(behind.id, { focused: true });
  }
});

// Chrome stops drawing its windows while they are completely covered by other windows (measured: 1 frame in 2 s
// instead of ~110), so VIZEN would show a frozen picture. A page that is being captured is always drawn, so this
// window holds a tiny capture of itself (64 x 64, 1 frame per second, never shown or sent anywhere).
let selfStream = null;

async function keepDrawingWhenCovered() {
  try {
    const me = await chrome.tabs.getCurrent();
    const selfId = await chrome.tabCapture.getMediaStreamId({ targetTabId: me.id, consumerTabId: me.id });
    selfStream = await navigator.mediaDevices.getUserMedia({
      audio: false,
      video: { mandatory: { chromeMediaSource: "tab", chromeMediaSourceId: selfId, maxWidth: 64, maxHeight: 64, maxFrameRate: 1 } },
    });
  } catch {
    askToKeepDrawing(); // Chrome wants the user's OK for this window to capture itself
  }
}

function askToKeepDrawing() {
  const bar = document.getElementById("keep");
  document.getElementById("keepText").textContent = chrome.i18n.getMessage("mirrorKeepText");
  const button = document.getElementById("keepButton");
  button.textContent = chrome.i18n.getMessage("mirrorKeepButton");
  bar.style.display = "flex";
  button.onclick = async () => {
    try {
      selfStream = await navigator.mediaDevices.getDisplayMedia({
        video: { width: 64, height: 64, frameRate: 1 }, audio: false, preferCurrentTab: true, selfBrowserSurface: "include",
      });
      bar.style.display = "none";
    } catch {
      // declined: the mirror still works while the window is at least partly visible
    }
  };
}
