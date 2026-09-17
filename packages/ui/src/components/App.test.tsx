import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { FilePickRequest } from "../bridge";
import { FakeBridge, defaultState, failure, historyEntry, ok, response } from "../test/fakeBridge";
import { App } from "./App";

function setup() {
  const bridge = new FakeBridge();
  const user = userEvent.setup();
  render(<App bridge={bridge} />);
  return { bridge, user };
}

/**
 * The dropdowns are drawn in the app rather than by the platform, so a choice
 * is two clicks and the option is matched by its label, not its value.
 */
async function choose(
  user: ReturnType<typeof userEvent.setup>,
  combobox: HTMLElement,
  option: string,
) {
  await user.click(combobox);
  await user.click(await screen.findByRole("option", { name: option }));
}

/**
 * jsdom answers every media query with `matches: false`, which would make the
 * system theme look light whatever it is asked. This says what the desktop
 * prefers, and can change its mind.
 */
function systemPrefersDark(dark: boolean) {
  const listeners = new Set<() => void>();
  let matches = dark;

  vi.spyOn(window, "matchMedia").mockImplementation(
    (query: string) =>
      ({
        media: query,
        get matches() {
          return matches;
        },
        addEventListener: (_event: string, listener: () => void) => listeners.add(listener),
        removeEventListener: (_event: string, listener: () => void) => listeners.delete(listener),
        addListener: () => undefined,
        removeListener: () => undefined,
        onchange: null,
        dispatchEvent: () => false,
      }) as unknown as MediaQueryList,
  );

  return {
    change(next: boolean) {
      matches = next;
      for (const listener of listeners) listener();
    },
  };
}

/** Settings is tabbed; the certificate fields live behind the Certs tab. */
async function openSettings(
  user: ReturnType<typeof userEvent.setup>,
  section?: "General" | "Certs" | "About",
) {
  await user.click(screen.getByRole("button", { name: "Settings" }));
  const dialog = await screen.findByRole("dialog", { name: "Settings" });
  if (section) await user.click(within(dialog).getByRole("tab", { name: section }));
  return dialog;
}

/** Saving asks for a name in a dialog of the app's own. */
async function saveAs(user: ReturnType<typeof userEvent.setup>, name: string) {
  const dialog = await screen.findByRole("dialog", { name: "Save request" });
  const field = within(dialog).getByLabelText("Name");
  await user.clear(field);
  await user.type(field, name);
  await user.click(within(dialog).getByRole("button", { name: "Save" }));
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

beforeEach(() => {});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("opening the app", () => {
  it("lands directly on a blank request", async () => {
    setup();
    await ready();

    expect(screen.getByRole("textbox", { name: "URL" })).toHaveValue("");
    expect(screen.getByRole("combobox", { name: "Method" })).toHaveTextContent("GET");
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
    await saveAs(user, "Saved name");

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
    await choose(user, screen.getByRole("combobox", { name: "Method" }), "POST");

    await user.click(screen.getByRole("tab", { name: /Body/ }));
    await choose(user, screen.getByLabelText("Body"), "JSON");

    await user.click(screen.getByRole("tab", { name: /Auth/ }));
    await choose(user, screen.getByLabelText("Auth"), "Bearer token");
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
    await choose(user, screen.getByLabelText("Auth"), "Bearer token");

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

    const dialog = await screen.findByRole("dialog", { name: "Clear history" });
    await user.click(within(dialog).getByRole("button", { name: "Clear" }));

    expect(await screen.findByText("Nothing sent yet.")).toBeInTheDocument();
  });

  it("keeps history when the clear is cancelled", async () => {
    const bridge = new FakeBridge();
    bridge.state = { ...defaultState(), history: [historyEntry(response())] };
    const user = userEvent.setup();
    render(<App bridge={bridge} />);
    await ready();

    await user.click(screen.getByRole("button", { name: "History" }));
    await user.click(await screen.findByRole("button", { name: "Clear" }));
    const dialog = await screen.findByRole("dialog", { name: "Clear history" });
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByText("Nothing sent yet.")).not.toBeInTheDocument();
    expect(bridge.state.history).toHaveLength(1);
  });

  it("saves a request and opens it from the Saved panel", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/users");
    await user.click(screen.getByRole("button", { name: "Save" }));
    await saveAs(user, "Saved name");

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

    const dialog = await openSettings(user, "Certs");

    await user.click(within(dialog).getByRole("button", { name: "Add" }));
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

    const dialog = await openSettings(user, "Certs");

    await user.clear(within(dialog).getByLabelText("File"));
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(bridge.saved.at(-1)?.settings.tls.clientIdentity).toBeNull();
    });
  });

  it("does not offer a password field until a certificate is chosen", async () => {
    const { user } = setup();
    await ready();

    const dialog = await openSettings(user, "Certs");

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

describe("url encoding", () => {
  const urlField = () => screen.getByRole<HTMLInputElement>("textbox", { name: "URL" });

  /*
   * The switch rewrites the URL as it is flipped, so what will be sent is on
   * screen. A value with a space is the case that sent a 400 for a while.
   */
  it("shows the difference in the URL bar as it is flipped", async () => {
    const { user } = setup();
    await ready();

    await user.type(urlField(), "https://api.example.com/lookup?postcode=SW1A 1AA");
    const encode = screen.getByRole("switch", { name: /URL-encode/ });
    expect(encode).toBeChecked();

    await user.click(encode);
    expect(urlField().value).toBe("https://api.example.com/lookup?postcode=SW1A 1AA");

    await user.click(encode);
    expect(urlField().value).toBe("https://api.example.com/lookup?postcode=SW1A%201AA");
  });
});

describe("response bodies", () => {
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

  it("highlights and folds markup, not only JSON", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({
        body: btoa("<html><body><p>hello</p></body></html>"),
        mimeType: "text/html; charset=utf-8",
      }),
    );

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    // Folding is the visible half; the language is what makes it possible.
    expect(screen.getByRole("button", { name: "Collapse all" })).toBeInTheDocument();
    await waitFor(() => expect(responseBodyText()).toContain("hello"));
  });

  /*
   * Plenty of APIs answer `text/plain` with one long line of JSON. The content
   * type is not evidence, but a successful parse is, so the body can be offered
   * formatted without the app pretending to know what it is.
   */
  it("offers to format JSON that arrived as one line of text", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({ body: btoa('{"a":{"b":[1,2,3]},"c":"d"}'), mimeType: "text/plain; charset=utf-8" }),
    );

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    // Raw to begin with: the server did not call it JSON.
    const pretty = screen.getByRole("checkbox", { name: "Pretty print" });
    expect(pretty).not.toBeChecked();
    await waitFor(() => expect(responseBodyText()).toContain('{"a":{"b":[1,2,3]},"c":"d"}'));

    await user.click(pretty);
    expect(pretty).toBeChecked();
    // Formatted, and folded as JSON once it is.
    await waitFor(() => expect(responseBodyText()).toContain('"b": ['));
    expect(screen.getByRole("button", { name: "Collapse all" })).toBeInTheDocument();
  });

  /** A JSON content type still arrives formatted, and can be put back. */
  it("can show a JSON body as it actually arrived", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok({ body: btoa('{"a":1,"b":2}') }));

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    const pretty = screen.getByRole("checkbox", { name: "Pretty print" });
    expect(pretty).toBeChecked();

    await user.click(pretty);
    await waitFor(() => expect(responseBodyText()).toContain('{"a":1,"b":2}'));
  });

  /*
   * Content type says JSON, body is not: SWAPI's `?format=wookiee` answers
   * `application/json` with unquoted barewords. Plain stays plain, and the box
   * says why it cannot help rather than disappearing.
   */
  it("cannot pretty-print a body that is not JSON, and says so", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({ body: btoa('{"oaoohuwhao":1,"whwokao":whhuanan}'), mimeType: "application/json" }),
    );

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    const pretty = screen.getByRole("checkbox", { name: "Pretty print" });
    expect(pretty).toBeDisabled();
    expect(pretty).not.toBeChecked();
    await waitFor(() => expect(responseBodyText()).toContain('"whwokao":whhuanan'));
  });

  /** YAML used to be refused as binary before it reached the viewer at all. */
  it("shows a YAML body rather than calling it binary", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({ body: btoa("openapi: 3.1.0\ninfo:\n  title: Test\n"), mimeType: "application/yaml" }),
    );

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    await waitFor(() => expect(responseBodyText()).toContain("openapi"));
    expect(screen.queryByText(/Binary response/)).not.toBeInTheDocument();
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
      buttons: "kde" as const,
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
      onResized: () => Promise.resolve(() => undefined),
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
    // The host said KDE, so it gets Breeze's buttons rather than Windows'.
    expect(document.querySelector(".ac-window-buttons--kde")).toBeInTheDocument();
    expect(document.querySelector(".ac-window-buttons--windows")).not.toBeInTheDocument();
    // All eight edges and corners, or the undecorated window cannot be resized.
    expect(document.querySelectorAll(".ac-resize-edge")).toHaveLength(8);

    await user.click(screen.getByRole("button", { name: "Minimise" }));
    expect(calls).toContain("minimize");
  });

  /*
   * A compositor maximises when it is ready, so asking straight after the click
   * returns the old answer and the glyph ends up a step behind. It follows the
   * window's own resize instead — which is also how it keeps up with a double
   * click on the title bar or a keyboard shortcut.
   */
  it("follows the window rather than its own click", async () => {
    const bridge = new FakeBridge();
    let maximized = false;
    let resized: (() => void) | undefined;
    bridge.window = {
      buttons: "kde" as const,
      minimize: () => Promise.resolve(),
      toggleMaximize: () => Promise.resolve(),
      close: () => Promise.resolve(),
      isMaximized: () => Promise.resolve(maximized),
      onResized: (listener: () => void) => {
        resized = listener;
        return Promise.resolve(() => undefined);
      },
      startResizing: () => Promise.resolve(),
    };
    render(<App bridge={bridge} />);
    await ready();

    expect(screen.getByRole("button", { name: "Maximise" })).toBeInTheDocument();

    // The window maximises without the app being told directly.
    maximized = true;
    act(() => resized?.());
    expect(await screen.findByRole("button", { name: "Restore" })).toBeInTheDocument();

    maximized = false;
    act(() => resized?.());
    expect(await screen.findByRole("button", { name: "Maximise" })).toBeInTheDocument();
  });

  it("survives a host whose window controls fail", async () => {
    const bridge = new FakeBridge();
    const controls = {
      buttons: "windows" as const,
      minimize: () => Promise.reject(new Error("no window")),
      toggleMaximize: () => Promise.reject(new Error("no window")),
      close: () => Promise.reject(new Error("no window")),
      // Rejecting here used to unmount the whole app from inside an effect.
      isMaximized: () => Promise.reject(new Error("no window")),
      onResized: () => Promise.reject(new Error("no window")),
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

describe("split position per tab", () => {
  const divider = () => screen.getByRole("separator");
  const position = () => divider().getAttribute("aria-valuenow");

  it("keeps each tab's divider where that tab left it", async () => {
    const { user } = setup();
    await ready();

    // Move the first tab's divider.
    divider().focus();
    await user.keyboard("{ArrowDown}{ArrowDown}");
    expect(position()).toBe("46");

    await user.keyboard("{Control>}n{/Control}");
    const tabs = screen.getAllByRole("tab", { name: /New request/ });
    expect(tabs).toHaveLength(2);

    // The new tab starts from the default, then moves independently.
    divider().focus();
    await user.keyboard("{ArrowUp}{ArrowUp}{ArrowUp}");
    expect(position()).toBe("40");

    // Back to the first tab: still where it was, not 40.
    await user.click(tabs[0]!);
    expect(position()).toBe("46");

    await user.click(screen.getAllByRole("tab", { name: /New request/ })[1]!);
    expect(position()).toBe("40");
  });

  it("opens a new tab at the position last used rather than always the default", async () => {
    const { user } = setup();
    await ready();

    divider().focus();
    await user.keyboard("{PageDown}");
    expect(position()).toBe("52");

    await user.keyboard("{Control>}n{/Control}");
    expect(position()).toBe("52");
  });

  it("persists each tab's position", async () => {
    const { bridge, user } = setup();
    await ready();

    divider().focus();
    await user.keyboard("{ArrowDown}");

    await waitFor(() => {
      expect(bridge.saved.at(-1)?.tabs[0]?.splitPercent).toBe(44);
    });
  });
});

describe("themes", () => {
  afterEach(() => {
    document.documentElement.removeAttribute("data-theme");
  });

  it("offers the warm palettes alongside the built-in ones", async () => {
    const { user } = setup();
    await ready();

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const dialog = await screen.findByRole("dialog", { name: "Settings" });

    // The list is drawn into the body, so it is read from the screen.
    await user.click(within(dialog).getByLabelText("Theme"));
    const options = screen.getAllByRole("option").map((option) => option.textContent);

    expect(options).toEqual(["Follow the system", "Light", "Dark", "Warm dark", "Warm light"]);
  });

  it("previews the palette as it is picked, before anything is saved", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const dialog = await screen.findByRole("dialog", { name: "Settings" });
    await choose(user, within(dialog).getByLabelText("Theme"), "Warm dark");

    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "warmDark");
    });
    expect(bridge.saved.at(-1)?.settings.theme ?? "system").toBe("system");
  });

  it("puts the saved palette back when the dialog is cancelled", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const dialog = await screen.findByRole("dialog", { name: "Settings" });
    await choose(user, within(dialog).getByLabelText("Theme"), "Warm dark");
    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "warmDark");
    });

    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "light");
    });
    expect(bridge.saved.at(-1)?.settings.theme ?? "system").toBe("system");

    // The draft goes with it: reopening starts from the saved theme again.
    await user.click(screen.getByRole("button", { name: "Settings" }));
    const again = await screen.findByRole("dialog", { name: "Settings" });
    expect(within(again).getByLabelText("Theme")).toHaveTextContent("Follow the system");
  });

  it("applies the chosen palette to the document", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const dialog = await screen.findByRole("dialog", { name: "Settings" });

    await choose(user, within(dialog).getByLabelText("Theme"), "Warm dark");
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "warmDark");
    });
    await waitFor(() => {
      expect(bridge.saved.at(-1)?.settings.theme).toBe("warmDark");
    });
  });

  it("resolves the system theme to what the desktop actually prefers", async () => {
    systemPrefersDark(true);
    const { user } = setup();
    await ready();

    // The saved theme is "system", and this desktop is dark.
    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "dark");
    });

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const dialog = await screen.findByRole("dialog", { name: "Settings" });
    await choose(user, within(dialog).getByLabelText("Theme"), "Light");
    await user.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "light");
    });

    await user.click(screen.getByRole("button", { name: "Settings" }));
    const again = await screen.findByRole("dialog", { name: "Settings" });
    await choose(user, within(again).getByLabelText("Theme"), "Follow the system");
    await user.click(within(again).getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "dark");
    });
  });

  it("follows the desktop changing its mind while the app is open", async () => {
    const system = systemPrefersDark(false);
    setup();
    await ready();

    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "light");
    });

    act(() => system.change(true));

    await waitFor(() => {
      expect(document.documentElement).toHaveAttribute("data-theme", "dark");
    });
  });
});

describe("saving a request", () => {
  it("suggests the URL as the name, in a field wide enough to read it", async () => {
    const { user } = setup();
    await ready();

    const url = "localhost:3000/v1/organisations/42/members?include=roles";
    await user.type(screen.getByRole("textbox", { name: "URL" }), url);
    await user.click(screen.getByRole("button", { name: "Save" }));

    const dialog = await screen.findByRole("dialog", { name: "Save request" });
    expect(within(dialog).getByLabelText("Name")).toHaveValue(url);
  });

  it("saves nothing when the dialog is cancelled", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/users");
    await user.click(screen.getByRole("button", { name: "Save" }));
    const dialog = await screen.findByRole("dialog", { name: "Save request" });
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog", { name: "Save request" })).not.toBeInTheDocument();
    expect(bridge.state.savedRequests).toHaveLength(0);
  });

  it("closes on Escape", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByRole("dialog", { name: "Save request" });
    await user.keyboard("{Escape}");

    await waitFor(() => {
      expect(screen.queryByRole("dialog", { name: "Save request" })).not.toBeInTheDocument();
    });
    expect(bridge.state.savedRequests).toHaveLength(0);
  });

  it("will not save an empty name", async () => {
    const { user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/users");
    await user.click(screen.getByRole("button", { name: "Save" }));
    const dialog = await screen.findByRole("dialog", { name: "Save request" });
    await user.clear(within(dialog).getByLabelText("Name"));

    expect(within(dialog).getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("saves on Enter, without reaching for the button", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/users");
    await user.keyboard("{Control>}s{/Control}");
    const dialog = await screen.findByRole("dialog", { name: "Save request" });
    await user.type(within(dialog).getByLabelText("Name"), "{Enter}");

    await waitFor(() => expect(bridge.state.savedRequests).toHaveLength(1));
    expect(bridge.state.savedRequests[0]!.name).toBe("localhost:3000/users");
  });
});

describe("confirming destructive things", () => {
  it("asks before closing a tab with work in it, and keeps it on cancel", async () => {
    const { user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/nearly-done");
    await user.keyboard("{Control>}w{/Control}");

    const dialog = await screen.findByRole("dialog", { name: "Discard changes" });
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.getByRole("textbox", { name: "URL" })).toHaveValue("localhost:3000/nearly-done");
  });

  it("closes the tab once the discard is confirmed", async () => {
    const { user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/nearly-done");
    await user.keyboard("{Control>}w{/Control}");

    const dialog = await screen.findByRole("dialog", { name: "Discard changes" });
    await user.click(within(dialog).getByRole("button", { name: "Discard" }));

    await waitFor(() => {
      expect(screen.getByRole("textbox", { name: "URL" })).toHaveValue("");
    });
  });

  it("closes an untouched tab without asking", async () => {
    const { user } = setup();
    await ready();

    // Scoped to the tab strip: the request pane's Params/Body/… are tabs too.
    const strip = () =>
      within(screen.getByRole("tablist", { name: "Open requests" })).getAllByRole("tab");

    await user.keyboard("{Control>}n{/Control}");
    await waitFor(() => expect(strip()).toHaveLength(2));

    await user.keyboard("{Control>}w{/Control}");

    await waitFor(() => expect(strip()).toHaveLength(1));
    expect(screen.queryByRole("dialog", { name: "Discard changes" })).not.toBeInTheDocument();
  });

  it("asks before deleting a saved request", async () => {
    const { bridge, user } = setup();
    await ready();

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/users");
    await user.click(screen.getByRole("button", { name: "Save" }));
    await saveAs(user, "Users");
    await waitFor(() => expect(bridge.state.savedRequests).toHaveLength(1));

    await user.click(screen.getByRole("button", { name: "Saved requests" }));
    const panel = await screen.findByRole("complementary");
    await user.click(within(panel).getByRole("button", { name: "Delete Users" }));

    const dialog = await screen.findByRole("dialog", { name: "Delete saved request" });
    await user.click(within(dialog).getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(bridge.state.savedRequests).toHaveLength(0));
  });
});

describe("the padlock", () => {
  const certificate = {
    subject: "CN=api.example.com",
    issuer: "CN=Example CA, O=Example",
    subjectAltNames: ["api.example.com", "www.example.com"],
    notBefore: "2026-01-01T00:00:00Z",
    notAfter: "2027-01-01T00:00:00Z",
    serial: "2E:BF:82:C4",
    signatureAlgorithm: "ecdsa-with-SHA256",
    sha256Fingerprint: "AB:CD:EF:01",
    expired: false,
    selfSigned: false,
  };

  it("is absent for a plain HTTP response", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    expect(screen.queryByRole("button", { name: "View certificate" })).not.toBeInTheDocument();
  });

  it("shows the certificate the server presented", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({
        finalUrl: "https://api.example.com/v1/users",
        tls: { protocol: "TLS 1.3", certificate },
      }),
    );

    await user.type(
      screen.getByRole("textbox", { name: "URL" }),
      "https://api.example.com/v1/users",
    );
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    await user.click(screen.getByRole("button", { name: "View certificate" }));
    const dialog = await screen.findByRole("dialog", { name: "Certificate" });

    // The host, not the path: that is what the certificate was issued for.
    expect(within(dialog).getByText("api.example.com")).toBeInTheDocument();
    expect(within(dialog).getByText("CN=Example CA, O=Example")).toBeInTheDocument();
    expect(within(dialog).getByText("api.example.com, www.example.com")).toBeInTheDocument();
    expect(within(dialog).getByText("AB:CD:EF:01")).toBeInTheDocument();
    expect(within(dialog).getByText("TLS 1.3")).toBeInTheDocument();
  });

  it("says so when the certificate has expired", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({
        finalUrl: "https://api.example.com/v1/users",
        tls: { protocol: "TLS 1.2", certificate: { ...certificate, expired: true } },
      }),
    );

    await user.type(
      screen.getByRole("textbox", { name: "URL" }),
      "https://api.example.com/v1/users",
    );
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    await user.click(screen.getByRole("button", { name: "View certificate" }));
    const dialog = await screen.findByRole("dialog", { name: "Certificate" });

    expect(within(dialog).getByText(/expired on/)).toBeInTheDocument();
  });

  it("copes with a connection whose certificate could not be read", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(
      ok({
        finalUrl: "https://api.example.com/v1/users",
        tls: { protocol: "TLS 1.3", certificate: null },
      }),
    );

    await user.type(
      screen.getByRole("textbox", { name: "URL" }),
      "https://api.example.com/v1/users",
    );
    await user.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByText("200 OK");

    await user.click(screen.getByRole("button", { name: "View certificate" }));
    const dialog = await screen.findByRole("dialog", { name: "Certificate" });

    expect(within(dialog).getByText(/could not be read/)).toBeInTheDocument();
  });
});

describe("the settings sections", () => {
  it("opens on General, with the certificates out of the way", async () => {
    const { user } = setup();
    const dialog = await openSettings(user);

    expect(within(dialog).getByLabelText("Theme")).toBeInTheDocument();
    expect(within(dialog).queryByLabelText("File")).not.toBeInTheDocument();
    expect(within(dialog).getByRole("tab", { name: "General" })).toHaveAttribute(
      "aria-selected",
      "true",
    );
  });

  it("shows the certificates under Certs, and the general fields under General", async () => {
    const { user } = setup();
    const dialog = await openSettings(user, "Certs");

    expect(within(dialog).getByLabelText("File")).toBeInTheDocument();
    expect(within(dialog).queryByLabelText("Theme")).not.toBeInTheDocument();

    await user.click(within(dialog).getByRole("tab", { name: "General" }));
    expect(within(dialog).getByLabelText("Theme")).toBeInTheDocument();
  });

  it("shows the host's version and where the state is kept under About", async () => {
    const { user } = setup();
    const dialog = await openSettings(user, "About");

    expect(within(dialog).getByText(/0\.1\.0/)).toBeInTheDocument();
    expect(within(dialog).getByText(/\/tmp\/state\.json/)).toBeInTheDocument();
  });

  it("keeps an edit made in one section when another is saved", async () => {
    const { bridge, user } = setup();
    const dialog = await openSettings(user, "Certs");

    await user.click(within(dialog).getByRole("button", { name: "Add" }));
    await user.type(within(dialog).getByRole("textbox", { name: "CA file 1" }), "/ca.pem");

    // The draft is one object behind all three sections, not one per tab.
    await user.click(within(dialog).getByRole("tab", { name: "General" }));
    await user.click(within(dialog).getByLabelText("Follow redirects"));
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => {
      const saved = bridge.saved.at(-1)?.settings;
      expect(saved?.tls.extraCaFiles).toEqual(["/ca.pem"]);
      expect(saved?.followRedirects).toBe(false);
    });
  });
});

describe("browsing for a certificate", () => {
  it("puts the chosen path in the field and saves it", async () => {
    const bridge = new FakeBridge();
    const asked: FilePickRequest[] = [];
    bridge.pickFile = (request: FilePickRequest) => {
      asked.push(request);
      return Promise.resolve("/home/me/client.p12");
    };
    const user = userEvent.setup();
    render(<App bridge={bridge} />);
    await ready();

    const dialog = await openSettings(user, "Certs");
    await user.click(within(dialog).getAllByRole("button", { name: "Browse…" })[0]!);

    await waitFor(() => {
      expect(within(dialog).getByLabelText("File")).toHaveValue("/home/me/client.p12");
    });

    // The chooser is told what it is for and which extensions to offer.
    expect(asked[0]?.title).toBe("Client certificate");
    expect(asked[0]?.filters).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ extensions: expect.arrayContaining(["p12", "pfx"]) }),
      ]),
    );

    await user.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(() => {
      expect(bridge.saved.at(-1)?.settings.tls.clientIdentity?.path).toBe("/home/me/client.p12");
    });
  });

  it("fills a CA row from the chooser", async () => {
    const bridge = new FakeBridge();
    bridge.pickFile = () => Promise.resolve("/etc/ssl/internal-ca.pem");
    const user = userEvent.setup();
    render(<App bridge={bridge} />);
    await ready();

    const dialog = await openSettings(user, "Certs");
    await user.click(within(dialog).getByRole("button", { name: "Add" }));
    await user.click(within(dialog).getAllByRole("button", { name: "Browse…" })[0]!);

    await waitFor(() => {
      expect(within(dialog).getByRole("textbox", { name: "CA file 1" })).toHaveValue(
        "/etc/ssl/internal-ca.pem",
      );
    });
  });

  it("leaves the field alone when the chooser is dismissed", async () => {
    const bridge = new FakeBridge();
    bridge.pickFile = () => Promise.resolve(null);
    const user = userEvent.setup();
    render(<App bridge={bridge} />);
    await ready();

    const dialog = await openSettings(user, "Certs");
    await user.type(within(dialog).getByLabelText("File"), "/typed/by/hand.pem");
    await user.click(within(dialog).getAllByRole("button", { name: "Browse…" })[0]!);

    expect(within(dialog).getByLabelText("File")).toHaveValue("/typed/by/hand.pem");
  });

  it("offers no Browse button on a host without a file chooser", async () => {
    const { user } = setup();
    await ready();

    // The fake bridge has no pickFile, as a webview without a host would not.
    const dialog = await openSettings(user, "Certs");
    expect(within(dialog).queryByRole("button", { name: "Browse…" })).not.toBeInTheDocument();
    expect(within(dialog).getByLabelText("File")).toBeInTheDocument();
  });
});

describe("API key auth", () => {
  it("sends the key in the header it names", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "API key");

    await user.type(screen.getByLabelText("Key"), "X-API-Key");
    await user.type(screen.getByLabelText("Value"), "secret-key");
    await user.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(bridge.sent).toHaveLength(1));
    expect(bridge.sent[0]!.auth).toEqual({
      type: "apiKey",
      key: "X-API-Key",
      value: "secret-key",
      placement: "header",
    });
  });

  it("can put it in the query string instead, and says why not to", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "API key");
    await user.type(screen.getByLabelText("Key"), "api_key");
    await choose(user, screen.getByLabelText("Send in"), "Query parameter");

    expect(screen.getByText(/ends up in server logs/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(bridge.sent).toHaveLength(1));
    expect(bridge.sent[0]!.auth).toMatchObject({ type: "apiKey", placement: "query" });
  });

  it("starts each scheme empty rather than carrying the last one over", async () => {
    const { user } = setup();
    await ready();

    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "Bearer token");
    await user.type(screen.getByLabelText("Token"), "a-token");

    await choose(user, screen.getByLabelText("Auth"), "API key");
    expect(screen.getByLabelText("Key")).toHaveValue("");
    expect(screen.getByLabelText("Value")).toHaveValue("");
  });
});

describe("digest auth", () => {
  it("offers it and sends the credentials to the host", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "Digest");

    // It says what it will do, since nothing goes out on the first request.
    expect(screen.getByText(/comes back 401 with a challenge/)).toBeInTheDocument();

    await user.type(screen.getByLabelText("Username"), "ada");
    await user.type(screen.getByLabelText("Password"), "lovelace");
    await user.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(bridge.sent).toHaveLength(1));
    expect(bridge.sent[0]!.auth).toEqual({
      type: "digest",
      username: "ada",
      password: "lovelace",
    });
  });
});

describe("OAuth 2 auth", () => {
  async function pickOAuth(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "OAuth 2");
  }

  it("sends the client credentials settings to the host", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await pickOAuth(user);

    await user.type(screen.getByLabelText("Token URL"), "https://id.example.com/token");
    await user.type(screen.getByLabelText("Client ID"), "test-client");
    await user.type(screen.getByLabelText("Client secret"), "test-secret");
    await user.type(screen.getByLabelText("Scope"), "read:things");
    await user.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(bridge.sent).toHaveLength(1));
    expect(bridge.sent[0]!.auth).toMatchObject({
      type: "oauth2",
      grant: "clientCredentials",
      tokenUrl: "https://id.example.com/token",
      clientId: "test-client",
      clientSecret: "test-secret",
      scope: "read:things",
      clientAuth: "basicHeader",
    });
  });

  it("asks only for the fields the chosen grant needs", async () => {
    const { user } = setup();
    await ready();
    await pickOAuth(user);

    // Client credentials needs neither a user nor a refresh token.
    expect(screen.queryByLabelText("Refresh token")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Username")).not.toBeInTheDocument();

    await choose(user, screen.getByLabelText("Grant"), "Password");
    expect(screen.getByLabelText("Username")).toBeInTheDocument();
    expect(screen.queryByLabelText("Refresh token")).not.toBeInTheDocument();

    await choose(user, screen.getByLabelText("Grant"), "Refresh token");
    expect(screen.getByLabelText("Refresh token")).toBeInTheDocument();
    expect(screen.queryByLabelText("Username")).not.toBeInTheDocument();
  });

  it("says what it does not do", async () => {
    const { user } = setup();
    await ready();
    await pickOAuth(user);

    expect(screen.getByText(/browser flows are not here/)).toBeInTheDocument();
  });
});

describe("OAuth 1 auth", () => {
  it("sends the consumer and token pairs to the host", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "OAuth 1");

    await user.type(screen.getByLabelText("Consumer key"), "consumer");
    await user.type(screen.getByLabelText("Consumer secret"), "consumer-secret");
    await user.type(screen.getByLabelText("Token"), "user-token");
    await user.type(screen.getByLabelText("Token secret"), "user-secret");
    await user.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(bridge.sent).toHaveLength(1));
    expect(bridge.sent[0]!.auth).toMatchObject({
      type: "oauth1",
      consumerKey: "consumer",
      consumerSecret: "consumer-secret",
      token: "user-token",
      tokenSecret: "user-secret",
      signatureMethod: "hmacSha1",
    });
  });

  it("warns about PLAINTEXT when it is chosen", async () => {
    const { user } = setup();
    await ready();

    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "OAuth 1");
    expect(screen.queryByText(/Only over HTTPS/)).not.toBeInTheDocument();

    await choose(user, screen.getByLabelText("Signature"), "PLAINTEXT");
    expect(screen.getByText(/Only over HTTPS/)).toBeInTheDocument();
  });
});

describe("NTLM auth", () => {
  it("sends the account and the machine names to the host", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok());

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000/api/test");
    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "NTLM");

    await user.type(screen.getByLabelText("Username"), "ada");
    await user.type(screen.getByLabelText("Password"), "lovelace");
    await user.type(screen.getByLabelText("Domain"), "LOVELACE-LTD");
    await user.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(bridge.sent).toHaveLength(1));
    expect(bridge.sent[0]!.auth).toEqual({
      type: "ntlm",
      username: "ada",
      password: "lovelace",
      domain: "LOVELACE-LTD",
      workstation: "",
    });
  });

  it("says a domain is optional, because a local account has none", async () => {
    const { user } = setup();
    await ready();

    await user.click(screen.getByRole("tab", { name: "Auth" }));
    await choose(user, screen.getByLabelText("Auth"), "NTLM");

    expect(screen.getByLabelText("Domain")).toHaveAttribute(
      "placeholder",
      expect.stringContaining("Optional"),
    );
    expect(screen.getByText(/same connection/)).toBeInTheDocument();
  });
});
