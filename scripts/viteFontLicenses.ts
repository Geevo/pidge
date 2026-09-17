import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import type { Plugin } from "vite";

/**
 * Ships the bundled fonts' licences with the bundled fonts.
 *
 * IBM Plex Sans and IBM Plex Mono are both SIL Open Font License 1.1, which requires
 * the licence to travel with the font. The fonts are emitted by Vite as build
 * assets, so their licences are emitted the same way rather than left behind in
 * the source tree for a packager to remember.
 *
 * One source of truth: the files next to the woff2 in `packages/ui/src/fonts`.
 * The desktop bundle lists those same paths in `tauri.conf.json`, so nothing is
 * copied anywhere for it to find.
 */

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const fontDir = join(repoRoot, "packages", "ui", "src", "fonts");

const LICENCE_FILES = ["IBMPlexSans-LICENSE.txt", "IBMPlexMono-LICENSE.txt"] as const;
const NOTICE_FILE = "THIRD-PARTY-LICENSES.md";

export interface FontLicenseOptions {
  /** Directory inside the build output. */
  outDir?: string;
}

export function fontLicenses({ outDir = "licenses" }: FontLicenseOptions = {}): Plugin {
  const sources = [
    ...LICENCE_FILES.map((name) => ({ name, path: join(fontDir, name) })),
    { name: NOTICE_FILE, path: join(repoRoot, NOTICE_FILE) },
  ];

  return {
    name: "api-client:font-licenses",
    apply: "build",

    generateBundle() {
      for (const source of sources) {
        this.emitFile({
          type: "asset",
          fileName: `${outDir}/${source.name}`,
          source: readFileSync(source.path, "utf8"),
        });
      }
    },
  };
}
