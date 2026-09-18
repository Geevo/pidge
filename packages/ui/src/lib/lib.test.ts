import { describe, expect, it } from "vitest";

import { decodeBase64, decodeText, looksBinary } from "./base64";
import { formatBytes, formatDuration, requestLabel, statusClass } from "./format";
import { baseMimeType, isJsonMime, isTextMime, prettyJson, syntaxForMime } from "./mime";
import { paramsChanged, parseQueryParams, urlChanged } from "./url";
import { shortenUrl } from "./format";
import { isSelectAll, matchEditingCommand } from "./shortcuts";
import { createHistory, record, redo, undo } from "./textHistory";
import { installFieldHistory } from "./fieldHistory";

describe("formatting", () => {
  it("formats sizes the way the status line shows them", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1331)).toBe("1.3 KB");
    expect(formatBytes(2.4 * 1024 * 1024)).toBe("2.4 MB");
    expect(formatBytes(43_315)).toBe("42.3 KB");
    expect(formatBytes(2048)).toBe("2 KB");
    expect(formatBytes(-1)).toBe("—");
  });

  it("formats durations", () => {
    expect(formatDuration(42)).toBe("42 ms");
    expect(formatDuration(1200)).toBe("1.20 s");
  });

  it("bands status codes", () => {
    expect(statusClass(204)).toBe("ok");
    expect(statusClass(302)).toBe("redirect");
    expect(statusClass(404)).toBe("client");
    expect(statusClass(503)).toBe("server");
    expect(statusClass(0)).toBe("other");
  });

  it("labels requests by host and path", () => {
    expect(requestLabel("https://api.example.com/users", "GET")).toBe("api.example.com/users");
    expect(requestLabel("localhost:3000/x", "GET")).toBe("localhost:3000/x");
    expect(requestLabel("", "POST")).toBe("New request");
  });
});

describe("response bodies", () => {
  it("decodes base64 to text", () => {
    expect(decodeText(decodeBase64(btoa("hello")))).toBe("hello");
  });

  it("treats a NUL byte as binary", () => {
    expect(looksBinary(new Uint8Array([104, 0, 105]))).toBe(true);
    expect(looksBinary(new Uint8Array([104, 105]))).toBe(false);
    expect(looksBinary(new Uint8Array())).toBe(false);
  });
});

describe("the url field's own undo history", () => {
  const at = (value: string) => ({ value, caret: value.length });

  it("folds a run of typing into one step", () => {
    let history = createHistory(at(""));
    history = record(history, at("e"), 1000);
    history = record(history, at("ex"), 1050);
    history = record(history, at("exa"), 1100);

    expect(history.entries).toHaveLength(2);
    expect(undo(history)?.snapshot.value).toBe("");
  });

  it("starts a new step after a pause", () => {
    let history = createHistory(at(""));
    history = record(history, at("one"), 1000);
    history = record(history, at("one two"), 5000);

    expect(undo(history)?.snapshot.value).toBe("one");
  });

  /* A URL reads in parts, so its punctuation is where a step should end. */
  it("starts a new step at the punctuation between parts of a url", () => {
    let history = createHistory(at(""));
    history = record(history, at("example.com"), 1000);
    history = record(history, at("example.com/"), 1010);
    history = record(history, at("example.com/users"), 1020);

    const first = undo(history);
    expect(first?.snapshot.value).toBe("example.com");
  });

  it("treats a run of backspaces as one step", () => {
    let history = createHistory(at(""));
    history = record(history, at("hello"), 1000);
    history = record(history, at("hell"), 2000);
    history = record(history, at("hel"), 2050);
    history = record(history, at("he"), 2100);

    expect(undo(history)?.snapshot.value).toBe("hello");
  });

  it("walks back and forward again", () => {
    let history = createHistory(at(""));
    history = record(history, at("one"), 1000);
    history = record(history, at("one/two"), 5000);

    const back = undo(history)!;
    expect(back.snapshot.value).toBe("one");
    expect(redo(back.history)?.snapshot.value).toBe("one/two");
  });

  it("drops the redo future once something else is typed", () => {
    let history = createHistory(at(""));
    history = record(history, at("one"), 1000);
    history = record(history, at("one/two"), 5000);

    const back = undo(history)!;
    const next = record(back.history, at("one/three"), 9000);

    expect(redo(next)).toBeNull();
    expect(undo(next)?.snapshot.value).toBe("one");
  });

  it("has nothing to undo at the beginning, or to redo at the end", () => {
    const history = createHistory(at("start"));
    expect(undo(history)).toBeNull();
    expect(redo(history)).toBeNull();
  });
});

describe("undo wired to every field", () => {
  const typeInto = (node: HTMLInputElement, text: string) => {
    node.value = text;
    node.dispatchEvent(new Event("input", { bubbles: true }));
  };

  const pressUndo = (node: HTMLElement, shiftKey = false) =>
    node.dispatchEvent(
      new KeyboardEvent("keydown", { key: "z", ctrlKey: true, shiftKey, bubbles: true }),
    );

  it("steps back through what was typed in an ordinary input", () => {
    const field = document.createElement("input");
    document.body.append(field);
    const stop = installFieldHistory(document);

    field.dispatchEvent(new Event("focusin", { bubbles: true }));
    typeInto(field, "alpha");
    typeInto(field, "alpha/beta");

    pressUndo(field);
    expect(field.value).toBe("alpha");

    pressUndo(field, true);
    expect(field.value).toBe("alpha/beta");

    stop();
    field.remove();
  });

  /* CodeMirror binds these keys itself and keeps a better history than ours. */
  it("keeps its hands off a CodeMirror editor", () => {
    const editor = document.createElement("div");
    editor.className = "cm-editor";
    const field = document.createElement("textarea");
    editor.append(field);
    document.body.append(editor);
    const stop = installFieldHistory(document);

    field.dispatchEvent(new Event("focusin", { bubbles: true }));
    field.value = "one";
    field.dispatchEvent(new Event("input", { bubbles: true }));
    field.value = "one/two";
    field.dispatchEvent(new Event("input", { bubbles: true }));

    field.dispatchEvent(new KeyboardEvent("keydown", { key: "z", ctrlKey: true, bubbles: true }));
    expect(field.value).toBe("one/two");

    stop();
    editor.remove();
  });

  it("does nothing when the text was replaced by something other than typing", () => {
    const field = document.createElement("input");
    document.body.append(field);
    const stop = installFieldHistory(document);

    field.dispatchEvent(new Event("focusin", { bubbles: true }));
    typeInto(field, "typed");

    // A tab switch, say: the value changes with no input event behind it.
    field.value = "from somewhere else";
    pressUndo(field);
    expect(field.value).toBe("from somewhere else");

    stop();
    field.remove();
  });
});

describe("undo and redo keys", () => {
  const press = (key: string, extra: Partial<Record<"shiftKey" | "altKey", boolean>> = {}) => ({
    key,
    ctrlKey: true,
    metaKey: false,
    altKey: false,
    shiftKey: false,
    ...extra,
  });

  it("names the command behind each combination", () => {
    expect(matchEditingCommand(press("z"))).toBe("undo");
    expect(matchEditingCommand(press("Z", { shiftKey: true }))).toBe("redo");
    expect(matchEditingCommand(press("y"))).toBe("redo");
  });

  it("ignores everything else", () => {
    expect(matchEditingCommand(press("z", { altKey: true }))).toBeNull();
    expect(matchEditingCommand(press("a"))).toBeNull();
    expect(
      matchEditingCommand({
        key: "z",
        ctrlKey: false,
        metaKey: false,
        altKey: false,
        shiftKey: false,
      }),
    ).toBeNull();
  });
});

describe("the select all key", () => {
  const press = (key: string, extra: Partial<Record<"shiftKey" | "altKey", boolean>> = {}) => ({
    key,
    ctrlKey: true,
    metaKey: false,
    altKey: false,
    shiftKey: false,
    ...extra,
  });

  it("recognises the combination a read-only pane has to answer", () => {
    expect(isSelectAll(press("a"))).toBe(true);
    expect(isSelectAll(press("A"))).toBe(true);
  });

  /* Ctrl+Shift+A and Ctrl+Alt+A belong to whoever else wants them. */
  it("ignores everything else", () => {
    expect(isSelectAll(press("a", { shiftKey: true }))).toBe(false);
    expect(isSelectAll(press("a", { altKey: true }))).toBe(false);
    expect(isSelectAll(press("s"))).toBe(false);
    expect(
      isSelectAll({ key: "a", ctrlKey: false, metaKey: false, altKey: false, shiftKey: false }),
    ).toBe(false);
  });
});

describe("shortening a url for the status line", () => {
  it("leaves a url that fits alone", () => {
    expect(shortenUrl("https://example.com/v1/users?a=1")).toBe("https://example.com/v1/users?a=1");
  });

  /* The query is the half worth keeping, so the cut favours the tail. */
  it("cuts the middle and keeps the query", () => {
    const long = `https://api.example.com/very/long/path/${"segment/".repeat(12)}?search=luke&format=wookiee`;
    const short = shortenUrl(long);

    expect(short.length).toBeLessThanOrEqual(88);
    expect(short.startsWith("https://api.example.com")).toBe(true);
    expect(short.endsWith("?search=luke&format=wookiee")).toBe(true);
    expect(short).toContain("…");
  });

  it("keeps enough of the front to tell one host from another", () => {
    expect(shortenUrl("https://example.com/" + "x".repeat(400), 30).startsWith("https://exa")).toBe(
      true,
    );
  });
});

describe("url encoding of params", () => {
  const row = (name: string, value: string) => ({ id: name, enabled: true, name, value });

  it("percent-encodes by default", () => {
    expect(paramsChanged("https://x/lookup", [row("postcode", "SW1A 1AA")]).url).toBe(
      "https://x/lookup?postcode=SW1A%201AA",
    );
  });

  /*
   * Off is for a value that is already encoded, or that holds a `/` or `:` the
   * server wants to see. What goes in the URL is then exactly what was typed.
   */
  it("writes the text as typed when encoding is off", () => {
    expect(paramsChanged("https://x/s", [row("path", "/v1/a:b")], false).url).toBe(
      "https://x/s?path=/v1/a:b",
    );
    expect(paramsChanged("https://x/s", [row("pre", "%2F")], false).url).toBe(
      "https://x/s?pre=%2F",
    );
  });

  it("would otherwise encode an already-encoded value twice", () => {
    expect(paramsChanged("https://x/s", [row("pre", "%2F")]).url).toBe("https://x/s?pre=%252F");
  });

  /*
   * Pasting an encoded URL with the switch off and then turning it on is the
   * case that would double-encode if the table held raw text. It does not: the
   * URL is decoded on the way into the table, so what goes back out is the same
   * escape rather than an escaped escape.
   */
  it("survives pasting an encoded URL and then switching encoding on", () => {
    const pasted = urlChanged("https://x/lookup?postcode=SW1A%201AA", []);
    expect(pasted.queryParams[0]?.value).toBe("SW1A 1AA");
    expect(paramsChanged(pasted.url, pasted.queryParams, true).url).toBe(
      "https://x/lookup?postcode=SW1A%201AA",
    );
  });

  it("escapes only what was left unescaped in a half-encoded URL", () => {
    const pasted = urlChanged("https://x/s?a=x%20y&b=p q", []);
    expect(paramsChanged(pasted.url, pasted.queryParams, true).url).toBe(
      "https://x/s?a=x%20y&b=p%20q",
    );
  });

  it("returns to where it started when the switch is flipped twice", () => {
    const pasted = urlChanged("https://x/lookup?postcode=SW1A%201AA", []);
    const off = paramsChanged(pasted.url, pasted.queryParams, false);
    expect(off.url).toBe("https://x/lookup?postcode=SW1A 1AA");
    expect(paramsChanged(off.url, urlChanged(off.url, off.queryParams).queryParams, true).url).toBe(
      "https://x/lookup?postcode=SW1A%201AA",
    );
  });
});

describe("syntax from the content type", () => {
  it("names the language the server said it sent", () => {
    expect(syntaxForMime("application/json")).toBe("json");
    expect(syntaxForMime("application/vnd.api+json")).toBe("json");
    expect(syntaxForMime("text/html; charset=utf-8")).toBe("html");
    expect(syntaxForMime("application/xml")).toBe("xml");
    expect(syntaxForMime("text/xml")).toBe("xml");
    expect(syntaxForMime("application/soap+xml")).toBe("xml");
    expect(syntaxForMime("image/svg+xml")).toBe("xml");
    expect(syntaxForMime("text/css")).toBe("css");
    expect(syntaxForMime("application/javascript")).toBe("javascript");
    expect(syntaxForMime("text/javascript")).toBe("javascript");
    expect(syntaxForMime("application/yaml")).toBe("yaml");
    expect(syntaxForMime("application/vnd.oai.openapi+yaml")).toBe("yaml");
  });

  /** XHTML matches both rules; it is markup people read as HTML. */
  it("reads XHTML as HTML rather than as XML", () => {
    expect(syntaxForMime("application/xhtml+xml")).toBe("html");
  });

  it("highlights nothing it cannot name", () => {
    expect(syntaxForMime("text/plain")).toBe("text");
    expect(syntaxForMime("text/csv")).toBe("text");
    expect(syntaxForMime(null)).toBe("text");
  });

  /** These used to be rendered as "binary response" and never reached a viewer. */
  it("counts YAML and ndjson as text", () => {
    expect(isTextMime("application/yaml")).toBe(true);
    expect(isTextMime("application/x-yaml")).toBe(true);
    expect(isTextMime("application/x-ndjson")).toBe(true);
  });
});

describe("mime handling", () => {
  it("strips content type parameters", () => {
    expect(baseMimeType("application/json; charset=utf-8")).toBe("application/json");
    expect(baseMimeType(null)).toBe("");
  });

  it("recognises json, including suffixed types", () => {
    expect(isJsonMime("application/json")).toBe(true);
    expect(isJsonMime("application/vnd.api+json")).toBe(true);
    expect(isJsonMime("text/html")).toBe(false);
  });

  it("treats html and xml as text", () => {
    expect(isTextMime("text/html")).toBe(true);
    expect(isTextMime("application/xml")).toBe(true);
    expect(isTextMime("image/png")).toBe(false);
  });

  it("pretty-prints valid json and leaves invalid json alone", () => {
    expect(prettyJson('{"a":1}')).toBe('{\n  "a": 1\n}');
    expect(prettyJson("{ not json")).toBeNull();
    expect(prettyJson("")).toBeNull();
  });
});

describe("url and params stay in sync", () => {
  it("reads params out of a url", () => {
    const params = parseQueryParams("https://example.com/x?a=1&b=hello+world&c");
    expect(params.map((entry) => [entry.name, entry.value])).toEqual([
      ["a", "1"],
      ["b", "hello world"],
      ["c", ""],
    ]);
  });

  it("returns nothing for a url with no query", () => {
    expect(parseQueryParams("https://example.com/x")).toEqual([]);
  });

  it("keeps disabled rows when the url changes", () => {
    const existing = [
      { id: "1", enabled: true, name: "a", value: "1" },
      { id: "2", enabled: false, name: "old", value: "x" },
    ];
    const result = urlChanged("https://example.com?a=2", existing);

    expect(result.queryParams.map((entry) => [entry.name, entry.value, entry.enabled])).toEqual([
      ["a", "2", true],
      ["old", "x", false],
    ]);
  });

  it("reuses row ids so typing does not steal focus", () => {
    const existing = [{ id: "keep-me", enabled: true, name: "a", value: "1" }];
    const result = urlChanged("https://example.com?a=12", existing);
    expect(result.queryParams[0]!.id).toBe("keep-me");
  });

  it("rewrites only the query string when params change", () => {
    const result = paramsChanged("https://example.com/path?old=1#section", [
      { id: "1", enabled: true, name: "a", value: "hello world" },
      { id: "2", enabled: false, name: "skipped", value: "x" },
      { id: "3", enabled: true, name: "  ", value: "no name" },
    ]);

    expect(result.url).toBe("https://example.com/path?a=hello%20world#section");
  });

  it("removes the question mark when the last param goes", () => {
    expect(paramsChanged("https://example.com/path?a=1", []).url).toBe("https://example.com/path");
  });

  it("leaves a half-typed url alone", () => {
    expect(paramsChanged("local", []).url).toBe("local");
    expect(urlChanged("localhost:30", []).url).toBe("localhost:30");
  });
});
