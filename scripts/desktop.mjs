#!/usr/bin/env node
/**
 * Runs and packages the desktop app.
 *
 *   node scripts/desktop.mjs dev
 *     Starts Vite and the app pointed at it, so UI changes reload in place.
 *
 *   node scripts/desktop.mjs publish [--rid linux-x64]
 *     Builds the UI, then the app as one native executable with the UI
 *     embedded, into artifacts/desktop/<rid>/.
 */
import { execFileSync, spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { hostRid, readFlag } from "./rid.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const desktop = join(root, "apps", "desktop");
const project = join(root, "src", "Pidge.Desktop", "Pidge.Desktop.csproj");
const devServer = "http://localhost:5173";
// Run directly rather than through pnpm, which may only be reachable via corepack.
const viteBin = join(root, "node_modules", "vite", "bin", "vite.js");

const command = process.argv[2];
if (command === "dev") {
  dev();
} else if (command === "publish") {
  publish();
} else {
  console.error("usage: desktop.mjs dev | publish [--rid <rid>]");
  process.exit(2);
}

function dev() {
  const vite = spawn(process.execPath, [viteBin], { cwd: desktop, stdio: "inherit" });
  const app = spawn("dotnet", ["run", "--project", project], {
    cwd: root,
    stdio: "inherit",
    env: { ...process.env, PIDGE_DEV_SERVER: devServer },
  });

  // Closing the window ends the session; so does Ctrl+C.
  const stop = (code) => {
    vite.kill();
    app.kill();
    process.exit(code ?? 0);
  };
  app.on("exit", stop);
  vite.on("exit", (code) => {
    if (code) stop(code);
  });
  process.on("SIGINT", () => stop(130));
}

function publish() {
  const rid = readFlag("--rid") ?? hostRid();
  run(process.execPath, [viteBin, "build"], { cwd: desktop });
  run("dotnet", [
    "publish",
    project,
    "--configuration",
    "Release",
    "--runtime",
    rid,
    // Native compilation reports what it cannot see through as warnings;
    // each one is something that may break only in the published build.
    "-warnaserror",
    "--output",
    join(root, "artifacts", "desktop", rid),
  ]);
}

function run(file, args, options = {}) {
  console.log(`${file} ${args.join(" ")}`);
  execFileSync(file, args, { cwd: root, stdio: "inherit", ...options });
}
