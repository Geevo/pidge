/**
 * .NET runtime identifiers, and the Node platform/arch names VS Code uses for
 * the same machines.
 */

/** The runtime identifier for this machine, which is what is built by default. */
export function hostRid() {
  const os = { win32: "win", darwin: "osx", linux: "linux" }[process.platform];
  const arch = { x64: "x64", arm64: "arm64" }[process.arch];
  if (!os || !arch) {
    throw new Error(`No .NET runtime identifier for ${process.platform}-${process.arch}.`);
  }
  return `${os}-${arch}`;
}

/** Maps a runtime identifier such as `linux-arm64` onto `linux-arm64` as Node names it. */
export function nodeTargetOf(rid) {
  const [os, arch] = rid.split("-");
  const platform = { win: "win32", osx: "darwin", linux: "linux" }[os];
  if (!platform || !arch) {
    throw new Error(`Unrecognised runtime identifier: ${rid}`);
  }
  return { platform, arch };
}

export function readFlag(name) {
  const index = process.argv.indexOf(name);
  return index === -1 ? null : process.argv[index + 1];
}
