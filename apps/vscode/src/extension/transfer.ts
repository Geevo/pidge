import * as os from "node:os";

import type { ImportOutcome, SavedRequest } from "@api-client/ui";
import * as vscode from "vscode";

import type { Store } from "./store";

/**
 * `AppSession.MaxImportBytes`. The sidecar refuses anything bigger whatever
 * this says; checking first only saves reading it and sending it over.
 */
const MAX_IMPORT_BYTES = 20 * 1024 * 1024;

/**
 * Exports saved requests to a file: the desktop app's export dialog, asked as
 * VS Code asks things. `only` is the one request it was started from, if any;
 * otherwise every saved request is offered, all ticked.
 */
export async function exportSavedRequests(store: Store, only?: SavedRequest): Promise<void> {
  await store.load();
  let chosen: readonly SavedRequest[] = only ? [only] : store.savedRequests;
  if (chosen.length === 0) {
    void vscode.window.showInformationMessage("pidge: nothing is saved yet.");
    return;
  }

  if (!only) {
    const picked = await vscode.window.showQuickPick(
      chosen.map((saved) => ({
        label: saved.name,
        description: saved.request.method,
        picked: true,
        saved,
      })),
      { title: "Export saved requests", canPickMany: true, placeHolder: "Requests to export" },
    );
    if (!picked || picked.length === 0) return;
    chosen = picked.map((item) => item.saved);
  }

  const format = await vscode.window.showQuickPick(
    [
      {
        label: "JSON",
        detail: "Everything, exactly as saved. Best for keeping or moving between machines.",
        value: "json" as const,
      },
      {
        label: ".http",
        detail:
          "Readable, and runs in VS Code REST Client and JetBrains. Digest, NTLM and OAuth are left out.",
        value: "http" as const,
      },
    ],
    { title: "Export saved requests", placeHolder: "Format" },
  );
  if (!format) return;

  const secrets = await vscode.window.showQuickPick(
    [
      {
        label: "Passwords and tokens as {{placeholders}}",
        detail: "The file is safe to share. Check URLs and bodies yourself.",
        include: false,
      },
      {
        label: "Include passwords and tokens",
        detail: "Written as plain text. Treat the file like the passwords in it.",
        include: true,
      },
    ],
    { title: "Export saved requests", placeHolder: "Passwords and tokens" },
  );
  if (!secrets) return;

  const fileName = `${chosen.length === 1 ? slug(chosen[0]!.name) : "saved-requests"}.${format.value}`;
  const target = await vscode.window.showSaveDialog({
    title: "Export saved requests",
    defaultUri: vscode.Uri.joinPath(startFolder(), fileName),
    filters: format.value === "http" ? { "HTTP requests": ["http"] } : { JSON: ["json"] },
  });
  if (!target) return;

  const contents = await store.exportSavedRequests(
    chosen.map((saved) => saved.id),
    format.value,
    secrets.include,
  );
  await vscode.workspace.fs.writeFile(target, new TextEncoder().encode(contents));

  const what = chosen.length === 1 ? "1 request" : `${chosen.length} requests`;
  void vscode.window.showInformationMessage(
    secrets.include
      ? `Exported ${what} to ${target.fsPath}, with passwords and tokens in plain text.`
      : `Exported ${what} to ${target.fsPath}.`,
  );
}

/** Adds the saved requests in a JSON export or a `.http` file. */
export async function importSavedRequests(store: Store): Promise<void> {
  const chosen = await vscode.window.showOpenDialog({
    title: "Import saved requests",
    canSelectMany: false,
    openLabel: "Import",
    defaultUri: startFolder(),
    filters: { "Saved requests": ["json", "http", "rest"] },
  });
  const source = chosen?.[0];
  if (!source) return;

  const { size } = await vscode.workspace.fs.stat(source);
  if (size > MAX_IMPORT_BYTES) {
    throw new Error("The file is too large to be a file of saved requests.");
  }
  const contents = new TextDecoder().decode(await vscode.workspace.fs.readFile(source));
  await store.load();
  const outcome = await store.importSavedRequests(contents);

  const notice = importNotice(outcome);
  if (outcome.plainSecrets) void vscode.window.showWarningMessage(notice);
  else void vscode.window.showInformationMessage(notice);
}

/** Mirrors `importNotice` in the UI, so both say the same. */
function importNotice(outcome: ImportOutcome): string {
  const parts = [
    outcome.imported === 1 ? "Imported 1 request." : `Imported ${outcome.imported} requests.`,
  ];
  const names = outcome.undefinedVariables.map((name) => `{{${name}}}`);
  if (names.length > 0) {
    const list =
      names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;
    parts.push(
      `${outcome.imported === 1 ? "It uses" : "They use"} ${list}, which no environment defines yet.`,
    );
  }
  parts.push(...outcome.skipped);
  if (outcome.plainSecrets) {
    parts.push("The file holds passwords or tokens in plain text; it is worth deleting.");
  }
  return parts.join(" ");
}

function startFolder(): vscode.Uri {
  return vscode.workspace.workspaceFolders?.[0]?.uri ?? vscode.Uri.file(os.homedir());
}

/** Mirrors `slug` in the export dialog. */
function slug(name: string): string {
  const slugged = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return slugged || "saved-request";
}
