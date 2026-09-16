import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { FakeBridge, defaultState, failure, historyEntry, ok, response } from "../test/fakeBridge";
import { App } from "./App";

function setup() {
  const bridge = new FakeBridge();
  const user = userEvent.setup();
  render(<App bridge={bridge} />);
  return { bridge, user };
}

/** The app hydrates asynchronously; wait for that before asserting. */
async function ready() {
  await screen.findByRole("textbox", { name: "URL" });
}

/**
 * The response body renders in CodeMirror, which splits the text across
 * highlight spans, so it is read as a whole rather than matched span by span.
 */
function responseBodyText(): string {
  const editor = document.querySelector('[aria-label="Response body"]');
  if (editor) return editor.textContent ?? "";
  return document.querySelector(".ac-response-body")?.textContent ?? "";
}

beforeEach(() => {
  vi.spyOn(window, "confirm").mockReturnValue(true);
  vi.spyOn(window, "prompt").mockReturnValue("Saved name");
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("opening the app", () => {
  it("lands directly on a blank request", async () => {
    setup();
    await ready();

    expect(screen.getByRole("textbox", { name: "URL" })).toHaveValue("");
    expect(screen.getByRole("combobox", { name: "Method" })).toHaveValue("GET");
    expect(screen.getByText("Type a URL and press Send.")).toBeInTheDocument();
    // No welcome screen, no login, no workspace picker.
    expect(screen.queryByText(/sign in|workspace|collection/i)).not.toBeInTheDocument();
  });

  it("shows a recovery notice when the host reports one", async () => {
    const bridge = new FakeBridge();
    bridge.recovery = "Your saved data could not be loaded. Starting fresh.";
    render(<App bridge={bridge} />);

    expect(await screen.findByText(/could not be loaded/)).toBeInTheDocument();
  });
});

describe("sending", () => {
  it("sends what was typed and shows status, duration and size", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await user.click(screen.getByRole("button", { name: "Send" }));

    expect(await screen.findByText("200 OK")).toBeInTheDocument();
    expect(screen.getByText("42 ms")).toBeInTheDocument();
    expect(screen.getByText("11 B")).toBeInTheDocument();
    expect(bridge.sent).toHaveLength(1);
    expect(bridge.sent[0]!.url).toBe("localhost:3000/api/test");
  });

  it("pretty-prints a JSON response", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok({ body: btoa('{"ok":true,"n":1}') }));

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));

    await screen.findByText("200 OK");
    await waitFor(() => expect(responseBodyText()).toContain('"ok": true'));
  });

  it("shows invalid JSON verbatim rather than an error", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok({ body: btoa("{ not json"), sizeBytes: 10 }));

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));

    await screen.findByText("200 OK");
    await waitFor(() => expect(responseBodyText()).toContain("{ not json"));
  });

  it("falls back to a size for a binary body", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({
        body: btoa(String.fromCharCode(0, 159, 146, 150)),
        mimeType: "application/octet-stream",
        sizeBytes: 43_315,
      }),
    );

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/file");
    await user.click(screen.getByRole("button", { name: "Send" }));

    // The size appears in the status line too, so assert on the fallback itself.
    expect(await screen.findByText(/^Binary response — 42\.3 KB$/)).toBeInTheDocument();
    expect(screen.getByText("application/octet-stream")).toBeInTheDocument();
  });

  it("shows errors in the response area, not just a toast", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      failure({
        kind: "connectionRefused",
        message: "`localhost` refused the connection. Is the server running?",
        detail: "tcp connect error",
      }),
    );

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));

    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText("Connection refused")).toBeInTheDocument();
    expect(within(alert).getByText(/refused the connection/)).toBeInTheDocument();
    expect(within(alert).getByText("Details")).toBeInTheDocument();
  });

  it("shows a warning when an explicit Authorization header wins", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({
        warnings: ["An explicit Authorization header is set, so the Auth tab was ignored."],
      }),
    );

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));

    expect(await screen.findByText(/Auth tab was ignored/)).toBeInTheDocument();
  });

  it("offers Cancel while a request is in flight, and cancels it", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.holdNextSend();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/slow");
    await user.click(screen.getByRole("button", { name: "Send" }));

    const cancel = await screen.findByRole("button", { name: "Cancel" });
    expect(screen.queryByRole("button", { name: "Send" })).not.toBeInTheDocument();

    await user.click(cancel);

    expect(bridge.cancelled).toHaveLength(1);
    expect(await screen.findByRole("button", { name: "Send" })).toBeInTheDocument();
  });

  it("does not start a second send while one is running", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.holdNextSend();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByRole("button", { name: "Cancel" });

    await user.keyboard("{Control>}{Enter}{/Control}");
    expect(bridge.sent).toHaveLength(1);
  });
});

describe("keyboard shortcuts", () => {
  it("sends on Ctrl+Enter from anywhere", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.keyboard("{Control>}{Enter}{/Control}");

    expect(await screen.findByText("200 OK")).toBeInTheDocument();
  });

  it("sends on Enter inside the URL field", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000{Enter}");

    expect(await screen.findByText("200 OK")).toBeInTheDocument();
  });

  it("focuses the URL on Ctrl+L", async () => {
    const { user } = setup();
    await ready();

    await user.click(screen.getByRole("button", { name: "History" }));
    await user.keyboard("{Control>}l{/Control}");

    expect(screen.getByRole("textbox", { name: "URL" })).toHaveFocus();
  });

  it("opens a tab on Ctrl+N and closes one on Ctrl+W", async () => {
    const { user } = setup();
    await ready();

    await user.keyboard("{Control>}n{/Control}");
    expect(screen.getAllByRole("tab", { name: /New request/ })).toHaveLength(2);

    await user.keyboard("{Control>}w{/Control}");
    await waitFor(() => {
      expect(screen.getAllByRole("tab", { name: /New request/ })).toHaveLength(1);
    });
  });

  it("saves on Ctrl+S", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/save-me");
    await user.keyboard("{Control>}s{/Control}");

    await waitFor(() => {
      expect(bridge.state.savedRequests).toHaveLength(1);
    });
    expect(bridge.state.savedRequests[0]!.name).toBe("Saved name");
  });
});

describe("request configuration", () => {
  it("keeps the URL and the params table in sync both ways", async () => {
    const { user } = setup();
    await ready();

    const url = screen.getByRole("textbox", { name: "URL" });
    await user.type(url, "localhost:3000/search?q=cats");

    await user.click(screen.getByRole("tab", { name: /Params/ }));
    expect(screen.getByRole("textbox", { name: "Name 1" })).toHaveValue("q");
    expect(screen.getByRole("textbox", { name: "Value 1" })).toHaveValue("cats");

    await user.clear(screen.getByRole("textbox", { name: "Value 1" }));
    await user.type(screen.getByRole("textbox", { name: "Value 1" }), "dogs");

    expect(url).toHaveValue("localhost:3000/search?q=dogs");
  });

  it("sends headers that are enabled and skips the ones that are not", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("tab", { name: /Headers/ }));

    await user.type(screen.getByRole("textbox", { name: "Name 1" }), "X-Token");
    await user.type(screen.getByRole("textbox", { name: "Value 1" }), "abc");
    await user.type(screen.getByRole("textbox", { name: "Name 2" }), "X-Off");
    await user.click(screen.getByRole("checkbox", { name: /Enable X-Off/ }));

    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    const headers = bridge.sent[0]!.headers;
    expect(headers.find((header) => header.name === "X-Token")?.enabled).toBe(true);
    expect(headers.find((header) => header.name === "X-Off")?.enabled).toBe(false);
  });

  it("switches to POST with a JSON body and a bearer token", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/users");
    await user.selectOptions(screen.getByRole("combobox", { name: "Method" }), "POST");

    await user.click(screen.getByRole("tab", { name: /Body/ }));
    await user.selectOptions(screen.getByLabelText("Body"), "json");

    await user.click(screen.getByRole("tab", { name: /Auth/ }));
    await user.selectOptions(screen.getByLabelText("Auth"), "bearer");
    await user.type(screen.getByLabelText("Token"), "secret");

    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    const sent = bridge.sent[0]!;
    expect(sent.method).toBe("POST");
    expect(sent.body.type).toBe("json");
    expect(sent.auth).toEqual({ type: "bearer", token: "secret" });
  });

  it("warns when a typed Authorization header will beat the Auth tab", async () => {
    const { user } = setup();
    await ready();

    await user.click(screen.getByRole("tab", { name: /Auth/ }));
    await user.selectOptions(screen.getByLabelText("Auth"), "bearer");

    await user.click(screen.getByRole("tab", { name: /Headers/ }));
    await user.type(screen.getByRole("textbox", { name: "Name 1" }), "Authorization");
    await user.type(screen.getByRole("textbox", { name: "Value 1" }), "Token x");

    expect(screen.getByText(/Auth tab will be ignored/)).toBeInTheDocument();
  });
});

describe("response tabs", () => {
  it("shows response headers on the Headers tab", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    const responsePane = screen.getByRole("region", { name: "Response" });
    await user.click(within(responsePane).getByRole("tab", { name: /Headers/ }));

    expect(within(responsePane).getByText("content-type")).toBeInTheDocument();
    expect(within(responsePane).getByText("application/json")).toBeInTheDocument();
  });
});

describe("history and saved requests", () => {
  it("records a send in history and reopens it as a new scratch tab", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    await user.click(screen.getByRole("button", { name: "History" }));
    const drawer = await screen.findByRole("complementary");
    const entry = within(drawer).getByText(/localhost:3000\/api\/test/);

    // The method is its own colour-coded badge rather than part of the label.
    expect(within(drawer).getAllByText("GET").length).toBeGreaterThan(0);

    await user.click(entry);
    expect(screen.getAllByRole("tab", { name: /localhost:3000/ }).length).toBeGreaterThan(0);
  });

  it("clears history after confirming", async () => {
    const bridge = new FakeBridge();
    bridge.state = { ...defaultState(), history: [historyEntry(response())] };
    const user = userEvent.setup();
    render(<App bridge={bridge} />);
    await ready();

    await user.click(screen.getByRole("button", { name: "History" }));
    await user.click(await screen.findByRole("button", { name: "Clear" }));

    expect(await screen.findByText("Nothing sent yet.")).toBeInTheDocument();
  });

  it("saves a request and opens it from the Saved panel", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/users");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(bridge.state.savedRequests).toHaveLength(1));

    await user.click(screen.getByRole("button", { name: "Saved requests" }));
    const panel = await screen.findByRole("complementary");
    await user.click(within(panel).getByText("Saved name"));

    expect(screen.getAllByRole("tab", { name: /Saved name/ }).length).toBeGreaterThan(0);
  });
});

describe("persistence", () => {
  it("writes state back to the host after an edit", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");

    await waitFor(
      () => {
        expect(bridge.saved.length).toBeGreaterThan(0);
      },
      { timeout: 2000 },
    );
    expect(bridge.saved.at(-1)!.tabs[0]!.request.url).toBe("localhost:3000");
  });
});

describe("settings", () => {
  it("saves certificate settings back to the host", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const dialog = await screen.findByRole("dialog", { name: "Settings" });

    await user.click(within(dialog).getByRole("button", { name: "Add CA file" }));
    await user.type(
      within(dialog).getByRole("textbox", { name: "CA file 1" }),
      "/etc/ssl/internal-ca.pem",
    );

    await user.type(within(dialog).getByLabelText("File"), "/home/me/client.p12");
    await user.type(within(dialog).getByLabelText("Password"), "hunter2");

    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    // Saves are debounced and hydration already caused one, so wait for the
    // content rather than for any save at all.
    await waitFor(() => {
      expect(bridge.saved.at(-1)?.settings.tls.extraCaFiles).toEqual(["/etc/ssl/internal-ca.pem"]);
    });

    const tls = bridge.saved.at(-1)!.settings.tls;
    expect(tls.clientIdentity).toEqual({ path: "/home/me/client.p12", password: "hunter2" });
    // The system store stays on: an internal CA is added to it, not swapped in.
    expect(tls.useSystemRoots).toBe(true);
  });

  it("clears the client certificate when the path is emptied", async () => {
    const bridge = new FakeBridge();
    bridge.state = {
      ...defaultState(),
      settings: {
        ...defaultState().settings,
        tls: {
          useSystemRoots: true,
          extraCaFiles: [],
          clientIdentity: { path: "/old/client.pem", password: null },
          acceptInvalidCerts: false,
        },
      },
    };
    const user = userEvent.setup();
    render(<App bridge={bridge} />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const dialog = await screen.findByRole("dialog", { name: "Settings" });

    await user.clear(within(dialog).getByLabelText("File"));
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(bridge.saved.at(-1)?.settings.tls.clientIdentity).toBeNull();
    });
  });

  it("does not offer a password field until a certificate is chosen", async () => {
    const { user } = setup();
    await ready();

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const dialog = await screen.findByRole("dialog", { name: "Settings" });

    expect(within(dialog).queryByLabelText("Password")).not.toBeInTheDocument();
    await user.type(within(dialog).getByLabelText("File"), "/home/me/client.p12");
    expect(within(dialog).getByLabelText("Password")).toBeInTheDocument();
  });
});

describe("pane layout", () => {
  it("flips between stacked and side-by-side and remembers the choice", async () => {
    const { bridge, user } = setup();
    await ready();

    const split = document.querySelector(".ac-split");
    expect(split).toHaveClass("ac-split--rows");

    await user.click(screen.getByRole("button", { name: "Toggle pane layout" }));

    expect(document.querySelector(".ac-split")).toHaveClass("ac-split--columns");
    await waitFor(() => {
      expect(bridge.saved.at(-1)?.settings.paneLayout).toBe("columns");
    });
  });

  it("gives the divider the right orientation for the layout", async () => {
    const { user } = setup();
    await ready();

    const divider = screen.getByRole("separator");
    expect(divider).toHaveAttribute("aria-orientation", "horizontal");

    await user.click(screen.getByRole("button", { name: "Toggle pane layout" }));
    expect(screen.getByRole("separator")).toHaveAttribute("aria-orientation", "vertical");
  });

  it("resizes with the keyboard and persists the result", async () => {
    const { bridge, user } = setup();
    await ready();

    const divider = screen.getByRole("separator");
    expect(divider).toHaveAttribute("aria-valuenow", "42");

    divider.focus();
    await user.keyboard("{ArrowDown}{ArrowDown}");

    expect(screen.getByRole("separator")).toHaveAttribute("aria-valuenow", "46");
    await waitFor(() => {
      expect(bridge.saved.at(-1)?.settings.splitPercent).toBe(46);
    });
  });

  it("will not let a pane be dragged shut", async () => {
    const { user } = setup();
    await ready();

    const divider = screen.getByRole("separator");
    divider.focus();
    await user.keyboard("{Home}");
    expect(screen.getByRole("separator")).toHaveAttribute("aria-valuenow", "15");

    await user.keyboard("{ArrowUp}{ArrowUp}{ArrowUp}");
    expect(screen.getByRole("separator")).toHaveAttribute("aria-valuenow", "15");

    await user.keyboard("{End}");
    expect(screen.getByRole("separator")).toHaveAttribute("aria-valuenow", "85");
  });
});

describe("json responses", () => {
  it("offers collapse and expand for valid JSON", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok({ body: btoa('{"a":{"b":[1,2,3]}}') }));

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    expect(screen.getByRole("button", { name: "Collapse all" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Expand all" })).toBeInTheDocument();
  });

  it("does not offer folding for a body that is not valid JSON", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok({ body: btoa("{ not json") }));

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    expect(screen.queryByRole("button", { name: "Collapse all" })).not.toBeInTheDocument();
    await waitFor(() => expect(responseBodyText()).toContain("{ not json"));
  });

  it("falls back to plain text for a body too large to highlight", async () => {
    const { bridge, user } = setup();
    await ready();
    const big = `{"pad":"${"x".repeat(2 * 1024 * 1024)}"}`;
    bridge.queue(ok({ body: btoa(big), sizeBytes: big.length }));

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    expect(await screen.findByText(/too large to highlight/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Collapse all" })).not.toBeInTheDocument();
  });
});

describe("dragging the divider", () => {
  it("suppresses text selection for the duration of the drag", async () => {
    setup();
    await ready();

    const divider = screen.getByRole("separator");

    fireEvent.pointerDown(divider, { pointerId: 1, clientX: 0, clientY: 100 });
    // Applied straight to the body, so the browser never starts a selection.
    expect(document.body).toHaveClass("ac-dragging");
    expect(document.body).toHaveClass("ac-dragging--rows");

    fireEvent.pointerUp(divider, { pointerId: 1, clientX: 0, clientY: 200 });
    expect(document.body).not.toHaveClass("ac-dragging");
  });

  it("releases the page if the gesture is cancelled", async () => {
    setup();
    await ready();

    const divider = screen.getByRole("separator");
    fireEvent.pointerDown(divider, { pointerId: 1, clientX: 0, clientY: 100 });
    expect(document.body).toHaveClass("ac-dragging");

    fireEvent.lostPointerCapture(divider, { pointerId: 1 });
    expect(document.body).not.toHaveClass("ac-dragging");
  });
});

describe("window chrome", () => {
  it("draws none when the host has its own window frame", async () => {
    setup();
    await ready();

    // The fake bridge exposes no window controls, as VS Code does not.
    expect(screen.queryByRole("button", { name: "Close window" })).not.toBeInTheDocument();
    expect(document.querySelectorAll(".ac-resize-edge")).toHaveLength(0);
  });

  it("draws a title bar and resize edges when the host asks it to", async () => {
    const bridge = new FakeBridge();
    const calls: string[] = [];
    const controls = {
      minimize: () => {
        calls.push("minimize");
        return Promise.resolve();
      },
      toggleMaximize: () => {
        calls.push("toggleMaximize");
        return Promise.resolve();
      },
      close: () => {
        calls.push("close");
        return Promise.resolve();
      },
      isMaximized: () => Promise.resolve(false),
      startDragging: () => Promise.resolve(),
      startResizing: (edge: string) => {
        calls.push(`resize:${edge}`);
        return Promise.resolve();
      },
    };
    bridge.window = controls;
    const user = userEvent.setup();
    render(<App bridge={bridge} />);
    await ready();

    expect(screen.getByRole("button", { name: "Minimise" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Maximise" })).toBeInTheDocument();
    // All eight edges and corners, or the undecorated window cannot be resized.
    expect(document.querySelectorAll(".ac-resize-edge")).toHaveLength(8);

    await user.click(screen.getByRole("button", { name: "Minimise" }));
    expect(calls).toContain("minimize");
  });

  it("survives a host whose window controls fail", async () => {
    const bridge = new FakeBridge();
    const controls = {
      minimize: () => Promise.reject(new Error("no window")),
      toggleMaximize: () => Promise.reject(new Error("no window")),
      close: () => Promise.reject(new Error("no window")),
      // Rejecting here used to unmount the whole app from inside an effect.
      isMaximized: () => Promise.reject(new Error("no window")),
      startDragging: () => Promise.reject(new Error("no window")),
      startResizing: () => Promise.reject(new Error("no window")),
    };
    bridge.window = controls;
    const user = userEvent.setup();
    render(<App bridge={bridge} />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Minimise" }));

    // The app is still there; chrome failing is not the app failing.
    expect(screen.getByRole("textbox", { name: "URL" })).toBeInTheDocument();
  });
});
