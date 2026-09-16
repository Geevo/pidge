import { render, screen, waitFor, within } from "@testing-library/react";
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
    expect(screen.getByText(/"ok": true/)).toBeInTheDocument();
  });

  it("shows invalid JSON verbatim rather than an error", async () => {
    const { bridge, user } = setup();
    await ready();
    bridge.queue(ok({ body: btoa("{ not json"), sizeBytes: 10 }));

    await user.type(screen.getByRole("textbox", { name: "URL" }), "localhost:3000");
    await user.click(screen.getByRole("button", { name: "Send" }));

    await screen.findByText("200 OK");
    expect(screen.getByText("{ not json")).toBeInTheDocument();
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
