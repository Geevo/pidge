import type {
  ExportInput,
  FilePickRequest,
  LoadedState,
  PlatformBridge,
  SaveRequestInput,
  SendOutcome,
  WindowControls,
} from "../bridge";
import type {
  AppState,
  CodeTarget,
  HistoryEntry,
  HttpRequest,
  HttpResponse,
  ImportOutcome,
  RequestError,
  SavedRequest,
} from "../types";
import { blankTab } from "../state/factories";
import { newId } from "../lib/ids";

/**
 * A stand-in for the host.
 *
 * It records what it was asked to do and answers with whatever the test
 * queued, so the UI tests exercise real component behaviour without a process.
 */
export class FakeBridge implements PlatformBridge {
  readonly platform = "test";

  state: AppState = defaultState();
  recovery: string | null = null;
  /** Set by tests that need the app to draw its own title bar. */
  window?: WindowControls;
  /** Set by tests that need a host with a file chooser. */
  pickFile?: (request: FilePickRequest) => Promise<string | null>;
  /** Set by tests that need a host that can save files. */
  exportSavedRequests?: (input: ExportInput) => Promise<string | null>;
  /** Set by tests that need a host that can open files. */
  importSavedRequests?: () => Promise<ImportOutcome | null>;

  readonly sent: HttpRequest[] = [];
  readonly generated: { request: HttpRequest; target: CodeTarget }[] = [];
  /** What the next generate answers with. A string is code; an Error rejects. */
  code: string | Error = "curl --url 'https://example.com/'";
  readonly cancelled: string[] = [];
  readonly saved: AppState[] = [];

  /** Queued answers, taken in order; the last one repeats. */
  private outcomes: SendOutcome[] = [];
  private pendingResolve: ((outcome: SendOutcome) => void) | null = null;

  queue(outcome: SendOutcome): void {
    this.outcomes.push(outcome);
  }

  /** Makes the next send hang until `resolvePending` is called. */
  holdNextSend(): void {
    this.outcomes.push(HOLD);
  }

  resolvePending(outcome: SendOutcome): void {
    const resolve = this.pendingResolve;
    this.pendingResolve = null;
    resolve?.(outcome);
  }

  sendRequest(request: HttpRequest): Promise<SendOutcome> {
    this.sent.push(request);
    const next = this.outcomes.length > 1 ? this.outcomes.shift()! : (this.outcomes[0] ?? ok());

    if (next === HOLD) {
      return new Promise<SendOutcome>((resolve) => {
        this.pendingResolve = resolve;
      });
    }
    return Promise.resolve(next);
  }

  generateCode(request: HttpRequest, target: CodeTarget): Promise<string> {
    this.generated.push({ request, target });
    return this.code instanceof Error ? Promise.reject(this.code) : Promise.resolve(this.code);
  }

  cancelRequest(requestId: string): Promise<void> {
    this.cancelled.push(requestId);
    return Promise.resolve();
  }

  loadState(): Promise<LoadedState> {
    return Promise.resolve({
      state: this.state,
      recovery: this.recovery,
      storagePath: "/tmp/state.json",
      version: "0.1.0",
    });
  }

  saveState(state: AppState): Promise<AppState> {
    this.saved.push(state);
    // History is owned by the host, exactly as in the real one.
    this.state = { ...state, history: this.state.history };
    return Promise.resolve(this.state);
  }

  saveRequest(input: SaveRequestInput): Promise<AppState> {
    const saved: SavedRequest = {
      id: input.savedRequestId ?? newId(),
      name: input.name,
      request: input.request,
      createdAt: 0,
      updatedAt: 0,
    };
    const savedRequests = this.state.savedRequests.some((entry) => entry.id === saved.id)
      ? this.state.savedRequests.map((entry) => (entry.id === saved.id ? saved : entry))
      : [...this.state.savedRequests, saved];
    this.state = { ...this.state, savedRequests };
    return Promise.resolve(this.state);
  }

  deleteSavedRequest(savedRequestId: string): Promise<AppState> {
    this.state = {
      ...this.state,
      savedRequests: this.state.savedRequests.filter((entry) => entry.id !== savedRequestId),
    };
    return Promise.resolve(this.state);
  }

  clearHistory(): Promise<AppState> {
    this.state = { ...this.state, history: [] };
    return Promise.resolve(this.state);
  }
}

const HOLD = { response: null, error: null, historyEntry: null } as SendOutcome;

export function response(overrides: Partial<HttpResponse> = {}): HttpResponse {
  return {
    status: 200,
    statusText: "OK",
    headers: [{ id: "h1", enabled: true, name: "content-type", value: "application/json" }],
    body: btoa('{"ok":true}'),
    mimeType: "application/json",
    durationMs: 42,
    sizeBytes: 11,
    truncated: false,
    finalUrl: "http://localhost:3000/api/test",
    warnings: [],
    tls: null,
    ...overrides,
  };
}

export function ok(overrides: Partial<HttpResponse> = {}): SendOutcome {
  const value = response(overrides);
  return { response: value, error: null, historyEntry: historyEntry(value) };
}

export function failure(error: Partial<RequestError> = {}): SendOutcome {
  return {
    response: null,
    error: { kind: "connectionRefused", message: "nope", detail: null, ...error },
    historyEntry: null,
  };
}

export function historyEntry(value: HttpResponse): HistoryEntry {
  return {
    id: newId(),
    timestamp: 1_700_000_000_000,
    request: {
      id: newId(),
      method: "GET",
      url: value.finalUrl,
      queryParams: [],
      headers: [],
      auth: { type: "none" },
      body: { type: "none" },
      timeoutMs: null,
      encodeQuery: true,
    },
    status: value.status,
    statusText: value.statusText,
    durationMs: value.durationMs,
    sizeBytes: value.sizeBytes,
    error: null,
  };
}

export function defaultState(): AppState {
  const tab = blankTab();
  return {
    version: 1,
    settings: {
      theme: "system",
      syntaxTheme: "app",
      timeoutMs: 30000,
      followRedirects: true,
      maxHistory: 500,
      maxResponseBytes: 50 * 1024 * 1024,
      restoreTabs: true,
      wrapResponseLines: false,
      fontScale: 100,
      paneLayout: "rows",
      splitPercent: 42,
      tls: {
        useSystemRoots: true,
        extraCaFiles: [],
        clientIdentity: null,
        acceptInvalidCerts: false,
      },
    },
    savedRequests: [],
    history: [],
    tabs: [tab],
    activeTabId: tab.id,
    environments: [],
    activeEnvironmentId: null,
  };
}
