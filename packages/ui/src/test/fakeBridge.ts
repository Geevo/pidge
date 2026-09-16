import type {
  AppState,
  HistoryEntry,
  HttpRequest,
  HttpResponse,
  LoadedState,
  PlatformBridge,
  RequestError,
  SaveRequestInput,
  SendOutcome,
  SavedRequest,
} from "../index";
import { blankTab, newId } from "../index";

/**
 * A stand-in for the Rust host.
 *
 * It records what it was asked to do and answers with whatever the test
 * queued, so the UI tests exercise real component behaviour without a process.
 */
export class FakeBridge implements PlatformBridge {
  readonly platform = "test";

  state: AppState = defaultState();
  recovery: string | null = null;

  readonly sent: HttpRequest[] = [];
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

  cancelRequest(requestId: string): Promise<void> {
    this.cancelled.push(requestId);
    return Promise.resolve();
  }

  loadState(): Promise<LoadedState> {
    return Promise.resolve({
      state: this.state,
      recovery: this.recovery,
      storagePath: "/tmp/state.json",
    });
  }

  saveState(state: AppState): Promise<AppState> {
    this.saved.push(state);
    // History is owned by the host, exactly as in Rust.
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
      timeoutMs: 30000,
      followRedirects: true,
      maxHistory: 500,
      maxResponseBytes: 50 * 1024 * 1024,
      restoreTabs: true,
      wrapResponseLines: false,
    },
    savedRequests: [],
    history: [],
    tabs: [tab],
    activeTabId: tab.id,
    environments: [],
    activeEnvironmentId: null,
  };
}
