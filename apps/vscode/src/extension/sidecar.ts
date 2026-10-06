import { type ChildProcessWithoutNullStreams, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import * as path from "node:path";

import * as vscode from "vscode";

import {
  type ClientEnvelope,
  type ClientMessage,
  PROTOCOL_VERSION,
  type ServerEnvelope,
  type ServerMessage,
  decodeLine,
  encodeLine,
} from "./protocol";

/**
 * One long-running engine process per window.
 *
 * All HTTP goes through it, so the webview never has network access of its own
 * and both frontends share exactly one request engine.
 */
export class Sidecar implements vscode.Disposable {
  private child: ChildProcessWithoutNullStreams | null = null;
  private buffer = "";
  private nextId = 0;
  private readonly pending = new Map<
    string,
    { resolve: (message: ServerMessage) => void; reject: (error: Error) => void }
  >();
  private starting: Promise<void> | null = null;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly output: vscode.OutputChannel,
  ) {}

  /** Starts the process and completes the version handshake. */
  async start(): Promise<void> {
    this.starting ??= this.doStart().catch((error: unknown) => {
      this.starting = null;
      throw error;
    });
    return this.starting;
  }

  private async doStart(): Promise<void> {
    const binary = this.resolveBinary();
    this.output.appendLine(`starting request engine: ${binary}`);

    const child = spawn(binary, ["--state-dir", this.context.globalStorageUri.fsPath], {
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.child = child;

    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => this.onStdout(chunk));
    // The sidecar logs to stderr and protocol messages to stdout, never both.
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => this.output.append(chunk));

    child.on("exit", (code, signal) => {
      this.output.appendLine(
        `request engine exited (code ${code ?? "none"}, signal ${signal ?? "none"})`,
      );
      this.failAllPending(
        new Error("The request engine stopped. Run “pidge: Restart Request Engine”."),
      );
      this.child = null;
      this.starting = null;
    });

    child.on("error", (error) => {
      this.output.appendLine(`request engine error: ${error.message}`);
      this.failAllPending(error);
    });

    const reply = await this.call({
      type: "handshake",
      clientName: "vscode",
      clientVersion: this.extensionVersion(),
    });

    if (reply.type === "handshakeError") {
      const message = reply.message;
      this.dispose();
      throw new Error(message);
    }
    if (reply.type !== "handshakeOk") {
      this.dispose();
      throw new Error(`Unexpected handshake reply: ${reply.type}`);
    }

    this.output.appendLine(
      `request engine ready: ${reply.serverName} ${reply.serverVersion}, protocol ${reply.protocolVersion}`,
    );
  }

  /** Sends a message and waits for the reply with the same correlation id. */
  async call(message: ClientMessage, correlationId?: string): Promise<ServerMessage> {
    await this.ensureRunning();

    const id = correlationId ?? `m${++this.nextId}`;
    const envelope: ClientEnvelope = { v: PROTOCOL_VERSION, id, msg: message };

    return new Promise<ServerMessage>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      const child = this.child;
      if (!child) {
        this.pending.delete(id);
        reject(new Error("The request engine is not running."));
        return;
      }
      this.trace(`-> ${message.type} (${id})`);
      child.stdin.write(encodeLine(envelope), (error) => {
        if (error) {
          this.pending.delete(id);
          reject(error);
        }
      });
    });
  }

  /** Fire and forget: used for cancel, where the reply is not interesting. */
  notify(message: ClientMessage, correlationId?: string): void {
    const child = this.child;
    if (!child) return;
    const envelope: ClientEnvelope = {
      v: PROTOCOL_VERSION,
      id: correlationId ?? null,
      msg: message,
    };
    this.trace(`-> ${message.type} (notify)`);
    child.stdin.write(encodeLine(envelope));
  }

  async restart(): Promise<void> {
    this.dispose();
    await this.start();
  }

  dispose(): void {
    const child = this.child;
    this.child = null;
    this.starting = null;
    this.failAllPending(new Error("The request engine was stopped."));
    if (!child) return;
    try {
      child.stdin.write(encodeLine({ v: PROTOCOL_VERSION, id: null, msg: { type: "shutdown" } }));
    } catch {
      // The process may already be gone; killing it below is enough.
    }
    child.kill();
  }

  private async ensureRunning(): Promise<void> {
    if (!this.child) await this.start();
  }

  /** stdout is newline-delimited JSON; a chunk can split a line anywhere. */
  private onStdout(chunk: string): void {
    this.buffer += chunk;
    let newline = this.buffer.indexOf("\n");
    while (newline !== -1) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      if (line !== "") this.onLine(line);
      newline = this.buffer.indexOf("\n");
    }
  }

  private onLine(line: string): void {
    let envelope: ServerEnvelope;
    try {
      envelope = decodeLine(line);
    } catch {
      this.output.appendLine(
        `could not parse a line from the request engine: ${line.slice(0, 200)}`,
      );
      return;
    }

    if (envelope.v !== PROTOCOL_VERSION) {
      this.output.appendLine(
        `protocol mismatch: the engine sent version ${envelope.v}, this extension speaks ${PROTOCOL_VERSION}`,
      );
    }

    this.trace(`<- ${envelope.msg.type} (${envelope.id ?? "-"})`);

    const id = envelope.id;
    if (id === null) return;

    const waiting = this.pending.get(id);
    if (!waiting) return;
    this.pending.delete(id);
    waiting.resolve(envelope.msg);
  }

  private failAllPending(error: Error): void {
    for (const [, waiting] of this.pending) waiting.reject(error);
    this.pending.clear();
  }

  private extensionVersion(): string {
    const manifest = this.context.extension.packageJSON as { version?: string };
    return manifest.version ?? "0.0.0";
  }

  /**
   * Finds the binary: an explicit setting, then the one bundled for this
   * platform, then a local `dotnet build` so the repo is usable from source.
   */
  private resolveBinary(): string {
    const configured = vscode.workspace
      .getConfiguration("pidge")
      .get<string>("sidecarPath", "")
      .trim();
    if (configured !== "") return configured;

    const name = process.platform === "win32" ? "api-client-sidecar.exe" : "api-client-sidecar";
    const bundled = path.join(
      this.context.extensionPath,
      "bin",
      `${process.platform}-${process.arch}`,
      name,
    );
    if (existsSync(bundled)) return bundled;

    const artifacts = path.join(this.context.extensionPath, "..", "..", "artifacts");
    for (const configuration of ["debug", "release"]) {
      const local = path.join(artifacts, "bin", "Pidge.Sidecar", configuration, name);
      if (existsSync(local)) return local;
    }

    throw new Error(
      `No request engine found for ${process.platform}-${process.arch}. ` +
        `Run "pnpm build:sidecar", or set pidge.sidecarPath.`,
    );
  }

  private trace(message: string): void {
    if (vscode.workspace.getConfiguration("pidge").get<boolean>("trace", false)) {
      this.output.appendLine(message);
    }
  }
}
