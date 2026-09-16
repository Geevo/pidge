import { describe, expect, it } from "vitest";

import { defaultState, historyEntry, response } from "../test/fakeBridge";
import { blankRequest, cloneRequest, isUntouched } from "./factories";
import type { UiState } from "./reducer";
import { activeTab, needsCloseConfirmation, reducer, runtimeFor } from "./reducer";

function hydrated(): UiState {
  const empty: UiState = {
    app: defaultState(),
    runtime: {},
    drawer: null,
    storagePath: "",
    notice: null,
    loaded: false,
  };
  return reducer(empty, {
    type: "hydrate",
    app: defaultState(),
    storagePath: "/tmp/state.json",
    notice: null,
  });
}

describe("tabs", () => {
  it("opens with exactly one blank request", () => {
    const state = hydrated();
    expect(state.app.tabs).toHaveLength(1);
    expect(isUntouched(activeTab(state).request)).toBe(true);
  });

  it("adds and selects a new tab", () => {
    const state = reducer(hydrated(), { type: "newTab" });
    expect(state.app.tabs).toHaveLength(2);
    expect(state.app.activeTabId).toBe(state.app.tabs[1]!.id);
  });

  it("always leaves one tab open", () => {
    const start = hydrated();
    const state = reducer(start, { type: "closeTab", tabId: start.app.tabs[0]!.id });
    expect(state.app.tabs).toHaveLength(1);
    expect(state.app.activeTabId).toBe(state.app.tabs[0]!.id);
  });

  it("selects the neighbour when the active tab closes", () => {
    let state = reducer(hydrated(), { type: "newTab" });
    state = reducer(state, { type: "newTab" });
    const middle = state.app.tabs[1]!.id;
    state = reducer(state, { type: "selectTab", tabId: middle });
    state = reducer(state, { type: "closeTab", tabId: middle });

    expect(state.app.tabs).toHaveLength(2);
    expect(state.app.activeTabId).toBe(state.app.tabs[1]!.id);
  });

  it("only asks before discarding a tab with real content", () => {
    const state = hydrated();
    const blank = state.app.tabs[0]!;
    expect(needsCloseConfirmation(blank)).toBe(false);

    const edited = reducer(state, {
      type: "setRequest",
      tabId: blank.id,
      request: { ...blank.request, url: "https://example.com" },
    });
    expect(needsCloseConfirmation(edited.app.tabs[0]!)).toBe(true);
  });

  it("does not ask about a tab that is dirty but still empty", () => {
    const state = hydrated();
    const blank = state.app.tabs[0]!;
    const touched = reducer(state, {
      type: "setRequest",
      tabId: blank.id,
      request: { ...blank.request, url: "   " },
    });
    expect(needsCloseConfirmation(touched.app.tabs[0]!)).toBe(false);
  });

  it("gives a copied request fresh ids so two tabs do not share a send", () => {
    const original = {
      ...blankRequest(),
      url: "https://example.com",
      headers: [{ id: "row-1", enabled: true, name: "X", value: "1" }],
    };
    const copy = cloneRequest(original);

    expect(copy.id).not.toBe(original.id);
    expect(copy.headers[0]!.id).not.toBe("row-1");
    expect(copy.headers[0]!.name).toBe("X");
  });
});

describe("send lifecycle", () => {
  it("moves a tab through sending and done", () => {
    const state = hydrated();
    const tabId = state.app.tabs[0]!.id;

    const sending = reducer(state, { type: "sendStarted", tabId });
    expect(runtimeFor(sending, tabId).status.state).toBe("sending");

    const value = response();
    const done = reducer(sending, {
      type: "sendSucceeded",
      tabId,
      response: value,
      historyEntry: historyEntry(value),
    });

    const status = runtimeFor(done, tabId).status;
    expect(status.state).toBe("done");
    expect(done.app.history).toHaveLength(1);
  });

  it("records failures in history too", () => {
    const state = hydrated();
    const tabId = state.app.tabs[0]!.id;
    const value = response({ status: 500 });

    const failed = reducer(state, {
      type: "sendFailed",
      tabId,
      error: { kind: "dns", message: "no such host", detail: null },
      historyEntry: historyEntry(value),
    });

    expect(runtimeFor(failed, tabId).status.state).toBe("failed");
    expect(failed.app.history).toHaveLength(1);
  });

  it("does not record a cancelled send", () => {
    const state = hydrated();
    const tabId = state.app.tabs[0]!.id;

    const cancelled = reducer(reducer(state, { type: "sendStarted", tabId }), {
      type: "sendCancelled",
      tabId,
    });

    expect(runtimeFor(cancelled, tabId).status.state).toBe("idle");
    expect(cancelled.app.history).toHaveLength(0);
  });

  it("trims history to the configured cap", () => {
    let state = hydrated();
    state = { ...state, app: { ...state.app, settings: { ...state.app.settings, maxHistory: 2 } } };
    const tabId = state.app.tabs[0]!.id;

    for (let index = 0; index < 5; index += 1) {
      const value = response({ status: 200 + index });
      state = reducer(state, {
        type: "sendSucceeded",
        tabId,
        response: value,
        historyEntry: historyEntry(value),
      });
    }

    expect(state.app.history).toHaveLength(2);
    expect(state.app.history[0]!.status).toBe(204);
  });
});

describe("panels and environments", () => {
  it("toggles the drawer", () => {
    const state = reducer(hydrated(), { type: "setDrawer", drawer: "history" });
    expect(state.drawer).toBe("history");
    expect(reducer(state, { type: "setDrawer", drawer: null }).drawer).toBeNull();
  });

  it("keeps the active environment id", () => {
    const state = reducer(hydrated(), { type: "setActiveEnvironment", environmentId: "env-1" });
    expect(state.app.activeEnvironmentId).toBe("env-1");
  });

  it("repairs a state whose active tab does not exist", () => {
    const broken = { ...defaultState(), activeTabId: "gone" };
    const state = reducer(hydrated(), {
      type: "hydrate",
      app: broken,
      storagePath: "",
      notice: null,
    });
    expect(state.app.activeTabId).toBe(state.app.tabs[0]!.id);
  });
});
