#!/usr/bin/env node
/**
 * Builds the request engine and copies it where the VS Code extension expects
 * it: `apps/vscode/bin/<platform>-<arch>/`.
 *
 * The extension ships the binaries it was packaged with. It never downloads
 * anything at runtime.
 */
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const release = !process.argv.includes("--debug");
const profile = release ? "release" : "debug";

/** Cross-compilation needs the target installed; see docs/packaging.md. */
const target = readFlag("--target");

const cargoArgs = ["build", "-p", "api-client-sidecar"];
if (release) cargoArgs.push("--release");
if (target) cargoArgs.push("--target", target);

console.log(`cargo ${cargoArgs.join(" ")}`);
execFileSync("cargo", cargoArgs, { cwd: root, stdio: "inherit" });

const exe = platformOf(target) === "win32" ? "api-client-sidecar.exe" : "api-client-sidecar";
const built = target
  ? join(root, "target", target, profile, exe)
  : join(root, "target", profile, exe);

const destinationDir = join(
  root,
  "apps",
  "vscode",
  "bin",
  `${platformOf(target)}-${archOf(target)}`,
);
mkdirSync(destinationDir, { recursive: true });

const destination = join(destinationDir, exe);
copyFileSync(built, destination);
console.log(`copied ${built} -> ${destination}`);

function readFlag(name) {
  const index = process.argv.indexOf(name);
  return index === -1 ? null : process.argv[index + 1];
}

/** Maps a Rust target triple onto the Node platform/arch names VS Code uses. */
function platformOf(triple) {
  if (!triple) return process.platform;
  if (triple.includes("windows")) return "win32";
  if (triple.includes("apple") || triple.includes("darwin")) return "darwin";
  return "linux";
}

function archOf(triple) {
  if (!triple) return process.arch;
  if (triple.startsWith("aarch64")) return "arm64";
  if (triple.startsWith("x86_64")) return "x64";
  return process.arch;
}
