const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("pc", {
  scan: () => ipcRenderer.invoke("scan"),
  pairBegin: (id) => ipcRenderer.invoke("pair-begin", id),
  pairPin: (id, pin) => ipcRenderer.invoke("pair-pin", id, pin),
  sendVideo: (device) => ipcRenderer.invoke("send-video", device),
  sendAudio: (device) => ipcRenderer.invoke("send-audio", device),
  shareScreen: (device, fps) => ipcRenderer.invoke("share-screen", device, fps),
  mirrorCode: (code) => ipcRenderer.invoke("mirror-code", code),
  stop: () => ipcRenderer.invoke("stop"),
  onLog: (cb) => ipcRenderer.on("log", (_e, t) => cb(t)),
  onPlayback: (cb) => ipcRenderer.on("playback", (_e, m) => cb(m)),
});
