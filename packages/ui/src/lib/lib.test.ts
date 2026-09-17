import { describe, expect, it } from "vitest";

import { decodeBase64, decodeText, looksBinary } from "./base64";
import { formatBytes, formatDuration, requestLabel, statusClass } from "./format";
import { baseMimeType, isJsonMime, isTextMime, prettyJson, syntaxForMime } from "./mime";
import { paramsChanged, parseQueryParams, urlChanged } from "./url";

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
