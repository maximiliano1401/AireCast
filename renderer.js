// Interfaz de AireCast. Toda la comunicación con el proceso principal pasa por `window.pc`
// (ver preload.js). Aquí solo se gestiona el estado de la vista.

const $ = (id) => document.getElementById(id);

const list = $("list");
const empty = $("empty");
const emptyTitle = $("emptyTitle");
const emptyText = $("emptyText");
const scanBtn = $("scanBtn");
const scanLabel = $("scanLabel");
const deviceCount = $("deviceCount");
const targetLabel = $("targetLabel");
const pairPanel = $("pairPanel");
const pairDevice = $("pairDevice");
const pairForm = $("pairForm");
const pinInput = $("pin");
const pinBtn = $("pinBtn");
const codePanel = $("codePanel");
const codeText = $("codeText");
const codeForm = $("codeForm");
const codeInput = $("codeInput");
const codeBtn = $("codeBtn");
const screenBtn = $("screenBtn");
const videoBtn = $("videoBtn");
const audioBtn = $("audioBtn");
const nowPlaying = $("nowPlaying");
const npTitle = $("npTitle");
const npSub = $("npSub");
const stopBtn = $("stopBtn");
const statusbar = $("statusbar");
const statusText = $("statusText");
const logBody = $("log");
const logCount = $("logCount");
const fpsInputs = document.querySelectorAll('#fpsPicker input[name="fps"]');
const resolutionInputs = document.querySelectorAll('#resolutionPicker input[name="resolution"]');

// Ajustes del duplicado, que se recuerdan en este equipo. Mismos valores que VIDEO_FPS_CHOICES y
// VIDEO_RESOLUTIONS en mirror.py.
const FPS_CHOICES = [15, 24, 30, 60];
const FPS_DEFAULT = 30;
const FPS_KEY = "airecast.fps";
const RESOLUTION_CHOICES = ["720p", "1080p", "1440p", "2160p", "native"];
const RESOLUTION_DEFAULT = "1080p";
const RESOLUTION_KEY = "airecast.resolution";
const RESOLUTION_NAMES = { "720p": "720p", "1080p": "1080p", "1440p": "1440p", "2160p": "4K", native: "la resolución nativa" };

const ICON = {
  tv: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="2.5" y="4" width="19" height="13" rx="2"></rect><path d="M8 21h8"></path><path d="M12 17v4"></path></svg>`,
  speaker: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="2.5" width="14" height="19" rx="3"></rect><circle cx="12" cy="15.5" r="3.5"></circle><path d="M12 7h.01"></path></svg>`,
  check: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"></path></svg>`,
};

const ACTIONS = {
  screen: { button: screenBtn, start: "Compartiendo la pantalla", done: "Compartiendo la pantalla", playing: "Duplicando pantalla", call: (d) => pc.shareScreen(d, fps, resolution) },
  video: { button: videoBtn, start: "Enviando el vídeo", done: "Reproduciendo el vídeo", playing: "Reproduciendo vídeo", call: (d) => pc.sendVideo(d) },
  audio: { button: audioBtn, start: "Enviando el audio", done: "Reproduciendo el audio", playing: "Reproduciendo audio", call: (d) => pc.sendAudio(d) },
};

let devices = [];
let selectedId = null;
let actionInFlight = false;
let pairingInFlight = false;
let pairingTarget = null;
let pairingToken = 0;
let pairingBusy = false;
let codeBusy = false;
let scanning = false;
let linesLogged = 0;
let fps = loadSetting(FPS_KEY, (v) => FPS_CHOICES.includes(Number(v)), FPS_DEFAULT, Number);
let resolution = loadSetting(RESOLUTION_KEY, (v) => RESOLUTION_CHOICES.includes(v), RESOLUTION_DEFAULT, String);
let mirroring = false;

const rows = new Map();
const selected = () => devices.find((d) => d.id === selectedId) || null;

// --- Estado ------------------------------------------------------------------

function setStatus(text, tone = "info") {
  statusbar.hidden = !text;
  statusbar.dataset.tone = tone;
  statusText.textContent = text;
}

function addLog(text) {
  if (!text) return;
  logBody.textContent += text;
  if (logBody.textContent.length > 200000) logBody.textContent = logBody.textContent.slice(-100000);
  linesLogged = logBody.textContent.split("\n").length - 1;
  logCount.textContent = linesLogged > 0 ? `${linesLogged} línea${linesLogged === 1 ? "" : "s"}` : "vacío";
  logBody.scrollTop = logBody.scrollHeight;
}

function showNowPlaying(title, deviceName) {
  npTitle.textContent = title;
  npSub.textContent = deviceName ? `en «${deviceName}»` : "";
  nowPlaying.hidden = false;
}

function hideNowPlaying() {
  nowPlaying.hidden = true;
  mirroring = false;
}

// --- Fotogramas por segundo --------------------------------------------------

function loadSetting(key, isValid, fallback, convert) {
  try {
    const saved = localStorage.getItem(key);
    return saved !== null && isValid(saved) ? convert(saved) : fallback;
  } catch {
    return fallback;
  }
}

function saveSetting(key, value, pendingMessage) {
  try {
    localStorage.setItem(key, String(value));
  } catch {
    // Sin almacenamiento solo se pierde el recuerdo de la elección.
  }
  if (mirroring) setStatus(`${pendingMessage} la próxima vez que compartas la pantalla.`, "info");
}

function chooseFps(value) {
  fps = value;
  saveSetting(FPS_KEY, value, `Se usarán ${value} fps`);
}

function chooseResolution(value) {
  resolution = value;
  saveSetting(RESOLUTION_KEY, value, `Se usará ${RESOLUTION_NAMES[value]}`);
}

// --- Lista de dispositivos ---------------------------------------------------

function makePill(text, variant) {
  const span = document.createElement("span");
  span.className = `pill pill-${variant}`;
  span.textContent = text;
  return span;
}

function renderDevices() {
  list.replaceChildren();
  rows.clear();

  for (const device of devices) {
    const item = document.createElement("li");
    const button = document.createElement("button");
    button.type = "button";
    button.className = "device";
    button.setAttribute("aria-pressed", "false");
    button.title = [device.name, device.model, device.address].filter(Boolean).join(" · ");
    button.innerHTML = `
      <span class="device-icon" aria-hidden="true">${device.audio && !device.video ? ICON.speaker : ICON.tv}</span>
      <span class="device-body">
        <span class="device-title"><span class="device-name"></span></span>
        <span class="device-meta"></span>
      </span>
      <span class="pills"></span>
      <span class="device-check" aria-hidden="true">${ICON.check}</span>`;

    button.querySelector(".device-name").textContent = device.name || "Dispositivo";
    button.querySelector(".device-meta").textContent = [device.model, device.address].filter(Boolean).join(" · ");

    const pills = button.querySelector(".pills");
    if (device.video) pills.append(makePill("Vídeo", "video"));
    if (device.audio) pills.append(makePill("Audio", "audio"));
    if (device.needs_pairing) pills.append(device.paired ? makePill("Emparejado", "ok") : makePill("PIN", "warn"));

    button.addEventListener("click", () => selectDevice(device.id));
    item.append(button);
    list.append(item);
    rows.set(device.id, button);
  }

  empty.hidden = devices.length > 0;
  deviceCount.hidden = devices.length === 0;
  deviceCount.textContent = devices.length === 1 ? "1 encontrado" : `${devices.length} encontrados`;
  updateSelection();
  requestAnimationFrame(updateListFade);
}

// Desvanece el final de la lista cuando queda contenido por debajo.
function updateListFade() {
  const remaining = list.scrollHeight - list.scrollTop - list.clientHeight;
  list.classList.toggle("can-scroll", remaining > 8);
}

function updateSelection() {
  for (const [id, button] of rows) {
    const isSelected = id === selectedId;
    button.classList.toggle("is-selected", isSelected);
    button.setAttribute("aria-pressed", String(isSelected));
  }
}

function refreshDevicePills(device) {
  const button = rows.get(device.id);
  if (!button) return;
  const pills = button.querySelector(".pills");
  pills.replaceChildren();
  if (device.video) pills.append(makePill("Vídeo", "video"));
  if (device.audio) pills.append(makePill("Audio", "audio"));
  if (device.needs_pairing) pills.append(device.paired ? makePill("Emparejado", "ok") : makePill("PIN", "warn"));
}

function selectDevice(id) {
  const device = devices.find((d) => d.id === id);
  if (!device) return;

  selectedId = id;
  updateSelection();
  syncPanels();

  // Al elegir una TV que necesita PIN (o al volver a tocarla) se pide uno nuevo.
  if (device.needs_pairing && !device.paired) beginPairing(device);
}

// --- Sincronización de la vista ---------------------------------------------

function blockedReason(device, kind) {
  if (!device) return "Elige un dispositivo para empezar.";
  if (device.needs_pairing && !device.paired) return "Empareja el dispositivo con su PIN para continuar.";
  return `«${device.name}» no admite ${kind}.`;
}

function syncPanels() {
  const device = selected();

  const needsPin = Boolean(device && device.needs_pairing && !device.paired);
  pairPanel.hidden = !needsPin;
  pairDevice.textContent = device ? `«${device.name}»` : "";

  const ready = Boolean(device && (!device.needs_pairing || device.paired));
  const canVideo = ready && Boolean(device.video);
  const canAudio = ready && Boolean(device.audio);

  screenBtn.setAttribute("aria-disabled", String(!canVideo));
  videoBtn.setAttribute("aria-disabled", String(!canVideo));
  audioBtn.setAttribute("aria-disabled", String(!canAudio));

  targetLabel.textContent = !device ? "Elige un dispositivo" : ready ? device.name : `${device.name} · sin emparejar`;
  targetLabel.title = targetLabel.textContent;
}

// --- Buscar dispositivos -----------------------------------------------------

async function scan() {
  if (scanning) return;
  scanning = true;
  scanBtn.disabled = true;
  scanBtn.setAttribute("aria-busy", "true");
  scanLabel.textContent = "Buscando…";
  emptyTitle.textContent = "Buscando dispositivos…";
  emptyText.textContent = "Asegúrate de que la TV está encendida y en la misma red Wi-Fi.";
  empty.classList.add("is-scanning");
  setStatus("Buscando dispositivos en tu red…", "busy");

  try {
    const found = await pc.scan();
    devices = Array.isArray(found) ? found : [];
    if (selectedId && !devices.some((d) => d.id === selectedId)) selectedId = null;
    renderDevices();
    syncPanels();

    if (devices.length === 0) {
      setStatus("No se encontró ningún dispositivo. Comprueba que la TV está encendida y en la misma red.", "warn");
    } else {
      setStatus(devices.length === 1 ? "1 dispositivo disponible." : `${devices.length} dispositivos disponibles.`, "success");
    }
  } catch (error) {
    devices = [];
    selectedId = null;
    renderDevices();
    syncPanels();
    setStatus(`No se pudo buscar dispositivos: ${error.message}`, "error");
  } finally {
    scanning = false;
    scanBtn.disabled = false;
    scanBtn.removeAttribute("aria-busy");
    scanLabel.textContent = "Buscar";
    empty.classList.remove("is-scanning");
    emptyTitle.textContent = "No hay dispositivos";
    emptyText.textContent = "Enciende la TV, comprueba que está en la misma red Wi-Fi y vuelve a pulsar «Buscar».";
  }
}

// --- Emparejamiento ----------------------------------------------------------

async function beginPairing(device) {
  if (pairingInFlight && pairingTarget === device.id) return;

  const token = ++pairingToken;
  pairingInFlight = true;
  pairingTarget = device.id;
  setStatus(`Pidiendo el PIN a «${device.name}»…`, "busy");

  try {
    await pc.pairBegin(device.id);
    if (token !== pairingToken || selectedId !== device.id) return;
    pinInput.value = "";
    setStatus(`Escribe el PIN de 4 dígitos que aparece en «${device.name}».`, "warn");
    pinInput.focus();
  } catch (error) {
    if (token === pairingToken) setStatus(`No se pudo iniciar el emparejamiento: ${error.message}`, "error");
  } finally {
    if (token === pairingToken) {
      pairingInFlight = false;
      pairingTarget = null;
    }
  }
}

async function submitPin() {
  const device = selected();
  if (!device || pairingBusy) return;

  const pin = pinInput.value.trim();
  if (pin.length !== 4) {
    setStatus("Escribe el PIN de 4 dígitos que aparece en la TV.", "warn");
    pinInput.focus();
    return;
  }

  pairingBusy = true;
  pinBtn.disabled = true;
  setStatus(`Comprobando el PIN en «${device.name}»…`, "busy");

  try {
    await pc.pairPin(device.id, pin);
    device.paired = true;
    pinInput.value = "";
    refreshDevicePills(device);
    syncPanels();
    if (selectedId === device.id) rows.get(device.id)?.focus();
    setStatus(`«${device.name}» emparejado. Ya puedes enviar contenido.`, "success");
  } catch (error) {
    setStatus(`No se pudo emparejar: ${error.message}. Vuelve a tocar la TV para pedir otro PIN.`, "error");
    pinInput.select();
  } finally {
    pairingBusy = false;
    pinBtn.disabled = false;
  }
}

// --- Código en pantalla ------------------------------------------------------

function showCodePrompt(retry) {
  codePanel.hidden = false;
  codeText.textContent = retry
    ? "El código no era correcto. Escribe el nuevo código de 4 dígitos que muestra la TV."
    : "Mira la TV y escribe el código de 4 dígitos que aparece.";
  codeInput.value = "";
  setStatus(retry ? "Código incorrecto. Escribe el nuevo código de la TV." : "Mira la TV y escribe el código que aparece.", "warn");
  codeInput.focus();
  codePanel.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

async function sendCode() {
  if (codeBusy) return;

  const code = codeInput.value.trim();
  if (code.length !== 4) {
    setStatus("Escribe el código de 4 dígitos que aparece en la TV.", "warn");
    codeInput.focus();
    return;
  }

  codeBusy = true;
  codeBtn.disabled = true;
  setStatus("Conectando con la TV…", "busy");

  try {
    await pc.mirrorCode(code);
    codeInput.value = "";
    codePanel.hidden = true;
  } catch (error) {
    setStatus(`No se pudo enviar el código: ${error.message}`, "error");
  } finally {
    codeBusy = false;
    codeBtn.disabled = false;
  }
}

// --- Enviar contenido --------------------------------------------------------

async function run(key) {
  const action = ACTIONS[key];
  const device = selected();

  if (!device) {
    setStatus("Elige un dispositivo para empezar.", "warn");
    return;
  }
  if (device.needs_pairing && !device.paired) {
    setStatus("Empareja el dispositivo con su PIN para continuar.", "warn");
    return;
  }
  if (key === "audio" && !device.audio) {
    setStatus(blockedReason(device, "audio"), "warn");
    return;
  }
  if (key !== "audio" && !device.video) {
    setStatus(blockedReason(device, "vídeo"), "warn");
    return;
  }
  if (actionInFlight) return;

  actionInFlight = true;
  codePanel.hidden = true;
  action.button.classList.add("is-loading");
  setStatus(`${action.start} en «${device.name}»…`, "busy");

  try {
    const result = await action.call(device);
    if (result) {
      mirroring = key === "screen";
      showNowPlaying(action.playing, device.name);
      setStatus(`${action.done} en «${device.name}».`, "success");
    } else {
      setStatus("No se seleccionó ningún archivo.", "info");
    }
  } catch (error) {
    hideNowPlaying();
    codePanel.hidden = true;
    setStatus(`No se pudo iniciar: ${error.message}`, "error");
  } finally {
    actionInFlight = false;
    action.button.classList.remove("is-loading");
  }
}

async function stopPlayback() {
  stopBtn.disabled = true;
  try {
    await pc.stop();
    setStatus("Reproducción detenida.", "info");
  } catch (error) {
    setStatus(`No se pudo detener: ${error.message}`, "error");
  } finally {
    stopBtn.disabled = false;
    hideNowPlaying();
  }
}

// --- Eventos -----------------------------------------------------------------

scanBtn.addEventListener("click", scan);
screenBtn.addEventListener("click", () => run("screen"));
videoBtn.addEventListener("click", () => run("video"));
audioBtn.addEventListener("click", () => run("audio"));
stopBtn.addEventListener("click", stopPlayback);

for (const input of fpsInputs) {
  input.checked = Number(input.value) === fps;
  input.addEventListener("change", () => chooseFps(Number(input.value)));
}

for (const input of resolutionInputs) {
  input.checked = input.value === resolution;
  input.addEventListener("change", () => chooseResolution(input.value));
}

new ResizeObserver(updateListFade).observe(list);
window.addEventListener("resize", updateListFade);
list.addEventListener("scroll", updateListFade, { passive: true });

pairForm.addEventListener("submit", (event) => {
  event.preventDefault();
  submitPin();
});

codeForm.addEventListener("submit", (event) => {
  event.preventDefault();
  sendCode();
});

for (const input of [pinInput, codeInput]) {
  input.addEventListener("input", () => {
    input.value = input.value.replace(/\D/g, "").slice(0, 4);
    if (input.value.length === 4) {
      if (input === pinInput) submitPin();
      else sendCode();
    }
  });
}

pc.onLog(addLog);

pc.onPlayback((message) => {
  if (!message) return;

  switch (message.event) {
    case "ready":
      addLog("Puente AirPlay listo.\n");
      break;
    case "info":
      addLog(`${message.message}\n`);
      break;
    case "code-required":
      showCodePrompt(Boolean(message.retry));
      break;
    case "error":
      hideNowPlaying();
      codePanel.hidden = true;
      setStatus(`Se detuvo la reproducción: ${message.error}`, "error");
      addLog(`Error (${message.kind || "reproducción"}): ${message.error}\n`);
      break;
    case "ended":
      hideNowPlaying();
      codePanel.hidden = true;
      setStatus("La reproducción ha terminado.", "success");
      break;
    case "stopped":
      hideNowPlaying();
      codePanel.hidden = true;
      setStatus("Reproducción detenida.", "info");
      break;
  }
});

scan();
