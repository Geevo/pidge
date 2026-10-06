import * as vscode from "vscode";

/**
 * The page a webview loads: one of the bundles under `media/`.
 *
 * Strict CSP: scripts only from this extension and only with the nonce, no
 * remote anything. CodeMirror injects its own stylesheet at runtime, which is
 * why inline styles are allowed and inline scripts are not.
 */
export function webviewHtml(
  webview: vscode.Webview,
  extensionUri: vscode.Uri,
  script: "webview.js" | "sidebar.js",
): string {
  const nonce = makeNonce();
  const asset = (file: string): string =>
    webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", file)).toString();

  const csp = [
    `default-src 'none'`,
    `img-src ${webview.cspSource} data:`,
    `font-src ${webview.cspSource}`,
    `style-src ${webview.cspSource} 'unsafe-inline'`,
    `script-src 'nonce-${nonce}' ${webview.cspSource}`,
  ].join("; ");

  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="${csp}" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <link rel="stylesheet" href="${asset("webview.css")}" />
    <title>pidge</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" nonce="${nonce}" src="${asset(script)}"></script>
  </body>
</html>`;
}

/** Roots a webview may load from: the built bundles and nothing else. */
export function webviewOptions(extensionUri: vscode.Uri): vscode.WebviewOptions {
  return {
    enableScripts: true,
    localResourceRoots: [vscode.Uri.joinPath(extensionUri, "media")],
  };
}

function makeNonce(): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let nonce = "";
  for (let index = 0; index < 32; index += 1) {
    nonce += alphabet[Math.floor(Math.random() * alphabet.length)];
  }
  return nonce;
}
