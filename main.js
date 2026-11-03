const { app, BrowserWindow, ipcMain, dialog } = require("electron");
const { spawn } = require("node:child_process");
const readline = require("node:readline");
const crypto = require("node:crypto");
const http = require("node:http");
const path = require("node:path");
const fs = require("node:fs");
const os = require("node:os");

let win;
const send = (channel, payload) => win && !win.isDestroyed() && win.webContents.send(channel, payload);
const log = (text) => send("log", text);

// --- Puente AirPlay (Python + pyatv) -------------------------------------------------------

let bridge, nextId = 1;
const waiting = new Map();

// Rutas que cambian según el sistema: ejecutables con o sin .exe, Python del venv y WinGet.
function platformPaths() {
  const win = process.platform === "win32";
  const exe = (name) => (win ? `${name}.exe` : name);
  return {
    bridge: path.join(process.resourcesPath, "bridge", exe("bridge")),
    ffmpeg: path.join(process.resourcesPath, "ffmpeg", exe("ffmpeg")),
    python: win ? path.join(__dirname, ".venv", "Scripts", "python.exe") : path.join(__dirname, ".venv", "bin", "python"),
    winget: win && process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, "Microsoft", "WinGet", "Packages") : null,
  };
}

// Empaquetada: bridge(.exe) (PyInstaller) en resources/bridge. En desarrollo: Python del .venv.
function bridgeCommand(creds) {
  const paths = platformPaths();
  if (app.isPackaged) return [paths.bridge, [creds]];
  return [paths.python, [path.join(__dirname, "bridge.py"), creds]];
}

function startBridge() {
  const creds = path.join(app.getPath("userData"), "credentials.json");
  const [exe, args] = bridgeCommand(creds);
  bridge = spawn(exe, args, { windowsHide: true });
  readline.createInterface({ input: bridge.stdout }).on("line", (line) => {
    let msg; try { msg = JSON.parse(line); } catch { return log(line); }
    if (msg.event) return send("playback", msg);
    const p = waiting.get(msg.id); if (!p) return;
    waiting.delete(msg.id);
    msg.ok ? p.resolve(msg.result) : p.reject(new Error(msg.error));
  });
  bridge.stderr.on("data", (d) => log(d.toString()));
  bridge.on("exit", (code) => log(`El puente AirPlay terminó (código ${code})\n`));
}

function call(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    const id = nextId++;
    waiting.set(id, { resolve, reject });
    bridge.stdin.write(JSON.stringify({ id, cmd, ...args }) + "\n");
  });
}

// --- Servidor de medios: la TV descarga de aquí los vídeos enviados --------------------------

const shared = new Map(); // token -> ruta de archivo
const types = { ".mp4": "video/mp4", ".m4v": "video/x-m4v", ".mov": "video/quicktime" };
// Mismo rango fijo que ports.py (TCP), para abrir solo esos puertos en el cortafuegos.
const MEDIA_PORTS = { first: 27450, last: 27459 };
let mediaPort;

const media = http.createServer((req, res) => {
  const url = decodeURIComponent(req.url.split("?")[0]);
  const file = url.startsWith("/file/") ? shared.get(url.split("/")[2]) : null;
  if (!file || !fs.existsSync(file)) { res.writeHead(404); return res.end(); }

  const size = fs.statSync(file).size;
  const headers = { "Content-Type": types[path.extname(file).toLowerCase()] || "application/octet-stream", "Accept-Ranges": "bytes", "Cache-Control": "no-cache" };
  const range = /bytes=(\d*)-(\d*)/.exec(req.headers.range || "");
  if (range) {
    const start = range[1] ? Number(range[1]) : size - Number(range[2]);
    const end = range[1] && range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
    if (start > end || start >= size) { res.writeHead(416, { "Content-Range": `bytes */${size}` }); return res.end(); }
    res.writeHead(206, { ...headers, "Content-Range": `bytes ${start}-${end}/${size}`, "Content-Length": end - start + 1 });
    return req.method === "HEAD" ? res.end() : fs.createReadStream(file, { start, end }).pipe(res);
  }
  res.writeHead(200, { ...headers, "Content-Length": size });
  req.method === "HEAD" ? res.end() : fs.createReadStream(file).pipe(res);
});

// Escucha en el primer puerto libre del rango; si están todos ocupados, en uno aleatorio.
function listenMedia(port = MEDIA_PORTS.first) {
  const onError = (err) => {
    media.off("listening", onListening);
    if (err.code !== "EADDRINUSE" && err.code !== "EACCES") throw err;
    listenMedia(port < MEDIA_PORTS.last ? port + 1 : 0);
  };
  const onListening = () => {
    media.off("error", onError);
    mediaPort = media.address().port;
    if (port === 0) log(`Puertos ${MEDIA_PORTS.first}-${MEDIA_PORTS.last} ocupados: los vídeos se sirven en el ${mediaPort}\n`);
  };
  media.once("error", onError);
  media.once("listening", onListening);
  media.listen(port, "0.0.0.0");
}

// IP de este PC en la misma red que el receptor.
function localIpFor(address) {
  const ips = Object.values(os.networkInterfaces()).flat().filter((i) => i.family === "IPv4" && !i.internal).map((i) => i.address);
  const prefix = address.split(".").slice(0, 3).join(".");
  return ips.find((ip) => ip.startsWith(prefix + ".")) || ips[0];
}

// --- FFmpeg: lo usa el puente para capturar y codificar la pantalla ---------------------------

function findFfmpeg() {
  const paths = platformPaths();
  if (app.isPackaged) return paths.ffmpeg;
  const pkgs = paths.winget;
  if (pkgs && fs.existsSync(pkgs)) {
    for (const dir of fs.readdirSync(pkgs).filter((d) => d.startsWith("Gyan.FFmpeg"))) {
      for (const build of fs.readdirSync(path.join(pkgs, dir))) {
        const exe = path.join(pkgs, dir, build, "bin", "ffmpeg.exe");
        if (fs.existsSync(exe)) return exe;
      }
    }
  }
  return "ffmpeg"; // en el PATH
}

// --- IPC con la interfaz ---------------------------------------------------------------------

ipcMain.handle("scan", () => call("scan", { timeout: 5 }));
ipcMain.handle("pair-begin", (_e, device) => call("pair_begin", { device }));
ipcMain.handle("pair-pin", (_e, device, pin) => call("pair_pin", { device, pin }));
ipcMain.handle("mirror-code", (_e, code) => call("mirror_code", { code }));
ipcMain.handle("stop", () => call("stop"));

ipcMain.handle("send-video", async (_e, device) => {
  const pick = await dialog.showOpenDialog(win, { title: "Elige un vídeo", filters: [{ name: "Vídeo", extensions: ["mp4", "m4v", "mov"] }], properties: ["openFile"] });
  if (pick.canceled) return null;
  const token = crypto.randomBytes(12).toString("hex");
  const file = pick.filePaths[0];
  shared.set(token, file);
  const url = `http://${localIpFor(device.address)}:${mediaPort}/file/${token}/${encodeURIComponent(path.basename(file))}`;
  log(`Enviando ${path.basename(file)} → ${device.name}\n`);
  return call("play_url", { device: device.id, url });
});

ipcMain.handle("send-audio", async (_e, device) => {
  const pick = await dialog.showOpenDialog(win, { title: "Elige un audio", filters: [{ name: "Audio", extensions: ["mp3", "wav", "flac", "ogg", "m4a"] }], properties: ["openFile"] });
  if (pick.canceled) return null;
  log(`Enviando ${path.basename(pick.filePaths[0])} → ${device.name}\n`);
  return call("stream_audio", { device: device.id, path: pick.filePaths[0] });
});

// Duplicado AirPlay 2 (como PigeonCast): el puente captura con FFmpeg y envía H.264 cifrado.
ipcMain.handle("share-screen", async (_e, device, fps, resolution) => {
  log(`Iniciando duplicado de pantalla en ${device.name} (${resolution}, ${fps} fps)…\n`);
  return call("mirror", { device: device.id, ffmpeg: findFfmpeg(), fps, resolution });
});

app.whenReady().then(() => {
  startBridge();
  listenMedia();
  win = new BrowserWindow({
    width: 620, height: 680, backgroundColor: "#070b14", autoHideMenuBar: true,
    webPreferences: { preload: path.join(__dirname, "preload.js"), contextIsolation: true, nodeIntegration: false },
  });
  win.loadFile("index.html");
});

app.on("before-quit", () => bridge?.kill());
app.on("window-all-closed", () => app.quit());
