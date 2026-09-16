import { build, context } from "esbuild";

/** The extension host runs in Node inside VS Code, so `vscode` stays external. */
const options = {
  entryPoints: ["src/extension/extension.ts"],
  outfile: "dist/extension.cjs",
  bundle: true,
  format: "cjs",
  platform: "node",
  target: "node20",
  external: ["vscode"],
  sourcemap: true,
  logLevel: "info",
  minify: process.argv.includes("--minify"),
};

if (process.argv.includes("--watch")) {
  const ctx = await context(options);
  await ctx.watch();
  console.log("watching the extension host…");
} else {
  await build(options);
}
