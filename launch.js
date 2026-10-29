// Lanza Electron sin ELECTRON_RUN_AS_NODE, que VS Code y otras apps Electron dejan en el
// entorno y haría que Electron arranque como Node (sin ipcMain, BrowserWindow…).
const { spawn } = require("node:child_process");
const electron = require("electron"); // desde Node, devuelve la ruta al ejecutable

const env = { ...process.env };
delete env.ELECTRON_RUN_AS_NODE;
spawn(electron, [__dirname], { stdio: "inherit", env }).on("exit", (code) => process.exit(code ?? 0));
