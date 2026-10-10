import { describe, expect, it } from "vitest";

import type { HttpRequest } from "../types";
import { importCurl, shellWords } from "./curlImport";

function imported(command: string): { request: HttpRequest; notes: readonly string[] } {
  const result = importCurl(command);
  if (!result.ok) throw new Error(result.message);
  return result;
}

const rows = (entries: readonly { name: string; value: string }[]) =>
  entries.map(({ name, value }) => [name, value]);

describe("splitting a command into words", () => {
  it("follows the shell's quoting", () => {
    expect(shellWords(`curl 'a b' "c \\"d\\"" e\\ f`)).toEqual(["curl", "a b", 'c "d"', "e f"]);
  });

  it("joins lines ended with a backslash", () => {
    expect(shellWords("curl \\\n  --url x \\\r\n  -k")).toEqual(["curl", "--url", "x", "-k"]);
  });

  it("puts a quote inside a single-quoted string the usual way", () => {
    expect(shellWords(`'it'\\''s'`)).toEqual(["it's"]);
  });

  it("reads the $'…' strings a browser writes", () => {
    expect(shellWords(`$'{"a":\\n"it\\'s"}'`)).toEqual([`{"a":\n"it's"}`]);
  });

  it("keeps an empty quoted word", () => {
    expect(shellWords(`a '' b`)).toEqual(["a", "", "b"]);
  });
});

describe("importing a curl command", () => {
  it("reads the URL into the params", () => {
    const { request } = imported("curl 'https://api.example.com/users?page=2&q=a%20b'");
    expect(request.method).toBe("GET");
    expect(request.url).toBe("https://api.example.com/users?page=2&q=a%20b");
    expect(rows(request.queryParams)).toEqual([
      ["page", "2"],
      ["q", "a b"],
    ]);
  });

  it("reads this app's own curl snippets back", () => {
    const { request } = imported(
      [
        "curl --request PUT \\",
        "  --url 'https://example.com/items/1' \\",
        "  --header 'X-Trace: abc' \\",
        "  --header 'Content-Type: application/json' \\",
        `  --data-raw '{"name":"it'\\''s"}' \\`,
        "  --location \\",
        "  --max-time 2.5",
      ].join("\n"),
    );
    expect(request.method).toBe("PUT");
    expect(request.url).toBe("https://example.com/items/1");
    expect(rows(request.headers)).toEqual([["X-Trace", "abc"]]);
    expect(request.body).toEqual({ type: "json", text: `{"name":"it's"}` });
    expect(request.timeoutMs).toBe(2500);
  });

  it("works without the word curl", () => {
    expect(imported("-X DELETE https://example.com/x").request.method).toBe("DELETE");
  });

  it("splits short options run together", () => {
    const { request } = imported(`curl -sSL -XPATCH -H'Accept: */*' https://example.com`);
    expect(request.method).toBe("PATCH");
    expect(rows(request.headers)).toEqual([["Accept", "*/*"]]);
  });

  it("makes a request with data a POST", () => {
    expect(imported("curl -d a=1 https://example.com").request.method).toBe("POST");
  });

  it("reads a form body into its rows", () => {
    const { request } = imported("curl https://example.com -d 'a=1' --data-urlencode 'b=x y&z'");
    expect(request.body.type).toBe("urlEncoded");
    if (request.body.type !== "urlEncoded") return;
    expect(rows(request.body.entries)).toEqual([
      ["a", "1"],
      ["b", "x y&z"],
    ]);
  });

  it("takes an unlabelled JSON body as JSON", () => {
    const { request } = imported(`curl https://example.com -d '{"a":1}'`);
    expect(request.body).toEqual({ type: "json", text: '{"a":1}' });
  });

  it("keeps a JSON type with a name of its own", () => {
    const { request } = imported(
      `curl https://example.com -H 'Content-Type: application/vnd.api+json' -d '{"a":1}'`,
    );
    expect(request.body.type).toBe("json");
    expect(rows(request.headers)).toEqual([["Content-Type", "application/vnd.api+json"]]);
  });

  it("moves the content type of any other body onto the body", () => {
    const { request } = imported(
      `curl https://example.com -H 'Content-Type: application/xml' --data-binary '<a/>'`,
    );
    expect(request.body).toEqual({ type: "text", text: "<a/>", contentType: "application/xml" });
    expect(request.headers).toEqual([]);
  });

  it("reads --json as a JSON body that asks for JSON back", () => {
    const { request } = imported(`curl --json '{"a":1}' https://example.com`);
    expect(request.method).toBe("POST");
    expect(request.body).toEqual({ type: "json", text: '{"a":1}' });
    expect(rows(request.headers)).toEqual([["Accept", "application/json"]]);
  });

  it("sends the data in the query string with --get", () => {
    const { request } = imported("curl -G https://example.com/s -d q=cats -d n=2");
    expect(request.method).toBe("GET");
    expect(request.body).toEqual({ type: "none" });
    expect(rows(request.queryParams)).toEqual([
      ["q", "cats"],
      ["n", "2"],
    ]);
  });

  it("reads --form into multipart rows, files included", () => {
    const { request } = imported(
      "curl https://example.com -F 'note=hi' -F 'doc=@/tmp/a.pdf;type=application/pdf;filename=b.pdf'",
    );
    expect(request.method).toBe("POST");
    expect(request.body.type).toBe("multipart");
    if (request.body.type !== "multipart") return;
    expect(request.body.entries.map((entry) => [entry.name, entry.value])).toEqual([
      ["note", { kind: "text", value: "hi" }],
      [
        "doc",
        { kind: "file", path: "/tmp/a.pdf", fileName: "b.pdf", contentType: "application/pdf" },
      ],
    ]);
  });

  it("reads --user as basic auth", () => {
    const { request } = imported("curl -u 'ann:s3:cret' https://example.com");
    expect(request.auth).toEqual({ type: "basic", username: "ann", password: "s3:cret" });
  });

  it("reads --digest and --ntlm", () => {
    expect(imported("curl --digest -u a:b https://e.com").request.auth).toEqual({
      type: "digest",
      username: "a",
      password: "b",
    });
    expect(imported("curl --ntlm -u 'CORP\\ann:pw' https://e.com").request.auth).toEqual({
      type: "ntlm",
      username: "ann",
      password: "pw",
      domain: "CORP",
      workstation: "",
    });
  });

  it("moves an Authorization header into the auth pane", () => {
    const bearer = imported("curl -H 'Authorization: Bearer abc.def' -H 'X: 1' https://e.com");
    expect(bearer.request.auth).toEqual({ type: "bearer", token: "abc.def" });
    expect(rows(bearer.request.headers)).toEqual([["X", "1"]]);

    const basic = imported(`curl -H 'Authorization: Basic ${btoa("ann:pw")}' https://e.com`);
    expect(basic.request.auth).toEqual({ type: "basic", username: "ann", password: "pw" });
    expect(basic.request.headers).toEqual([]);
  });

  it("leaves an Authorization scheme it has no helper for as a header", () => {
    const { request } = imported("curl -H 'Authorization: Token xyz' https://e.com");
    expect(request.auth).toEqual({ type: "none" });
    expect(rows(request.headers)).toEqual([["Authorization", "Token xyz"]]);
  });

  it("turns the shorthand options into headers", () => {
    const { request } = imported("curl -A agent/1 -e https://ref -b 'a=1; b=2' https://e.com");
    expect(rows(request.headers)).toEqual([
      ["User-Agent", "agent/1"],
      ["Referer", "https://ref"],
      ["Cookie", "a=1; b=2"],
    ]);
  });

  it("does not take an option's value for the URL", () => {
    const { request } = imported("curl -o out.json --connect-timeout 5 https://e.com/x");
    expect(request.url).toBe("https://e.com/x");
  });

  it("says what it ignored", () => {
    const { notes } = imported("curl --frobnicate https://e.com");
    expect(notes).toEqual(["Ignored: --frobnicate."]);
  });

  it("uses --head for HEAD", () => {
    expect(imported("curl -I https://e.com").request.method).toBe("HEAD");
  });

  it("fails on a command with no URL", () => {
    expect(importCurl("curl -X POST")).toEqual({
      ok: false,
      message: "There is no URL in this command.",
    });
  });

  it("fails on a quote left open", () => {
    expect(importCurl("curl 'https://e.com").ok).toBe(false);
  });
});
