#!/usr/bin/env node
/**
 * Builds the request engine and copies it where the VS Code extension expects
 * it: `apps/vscode/bin/<platform>-<arch>/`.
 *
 * The extension ships the binaries it was packaged with. It never downloads
 * anything at runtime.
 *
 *   node scripts/build-sidecar.mjs [--rid linux-arm64] [--debug]
 *
 * A release build is a native (AOT) executable. Native compilation only
 * targets the operating system it runs on; see docs/packaging.md.
 */
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { hostRid, nodeTargetOf, readFlag } from "./rid.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const release = !process.argv.includes("--debug");
const rid = readFlag("--rid") ?? hostRid();
const { platform, arch } = nodeTargetOf(rid);

const output = join(root, "artifacts", "sidecar", rid);
const args = [
  "publish",
  join(root, "src", "Pidge.Sidecar", "Pidge.Sidecar.csproj"),
  "--configuration",
  release ? "Release" : "Debug",
  "--runtime",
  rid,
  // Native compilation reports what it cannot see through as warnings; each
  // one is something that may break only in the published build.
  "-warnaserror",
  "--output",
  output,
];

console.log(`dotnet ${args.join(" ")}`);
execFileSync("dotnet", args, { cwd: root, stdio: "inherit" });

const exe = platform === "win32" ? "api-client-sidecar.exe" : "api-client-sidecar";
const built = join(output, exe);
const destinationDir = join(root, "apps", "vscode", "bin", `${platform}-${arch}`);
mkdirSync(destinationDir, { recursive: true });

const destination = join(destinationDir, exe);
copyFileSync(built, destination);
console.log(`copied ${built} -> ${destination}`);
