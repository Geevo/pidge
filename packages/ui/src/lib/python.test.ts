import { StringStream } from "@codemirror/language";
import { describe, expect, it } from "vitest";

import { python } from "./python";

const NAME = "propertyName";
const CALL = "variableName.function";
const TYPE = "typeName";

/** Runs the tokenizer the way CodeMirror does, one line at a time. */
function tokens(source: string): [string, string][] {
  const state = python.startState!(2);
  const out: [string, string][] = [];

  for (const line of source.split("\n")) {
    const stream = new StringStream(line, 2, 2);
    if (line === "") {
      python.blankLine?.(state, 2);
      continue;
    }
    let guard = 0;
    while (!stream.eol()) {
      const start = stream.pos;
      const token = python.token(stream, state);
      if (stream.pos === start) {
        expect.fail(`no progress at ${JSON.stringify(line.slice(start))}`);
      }
      if (token) out.push([line.slice(start, stream.pos), token]);
      if (++guard > 500) expect.fail("too many tokens for one line");
    }
    stream.start = stream.pos;
  }

  return out;
}

function tokenFor(source: string, text: string): string | undefined {
  return tokens(source).find(([value]) => value === text)?.[1];
}

describe("the Python tokenizer", () => {
  /*
   * The line a generated snippet is really about, and the one `legacy-modes`
   * left almost entirely the colour of plain text.
   */
  it("colours the names, the calls and the keyword arguments of a request", () => {
    const source = "response = requests.post(url, headers=headers, timeout=30)";

    expect(tokenFor(source, "response")).toBe(NAME);
    expect(tokenFor(source, "requests")).toBe(NAME);
    expect(tokenFor(source, "post")).toBe(CALL);
    expect(tokenFor(source, "url")).toBe(NAME);
    expect(tokenFor(source, "headers")).toBe(NAME);
    expect(tokenFor(source, "timeout")).toBe(NAME);
    expect(tokenFor(source, "30")).toBe("number");
    expect(tokenFor(source, "=")).toBe("operator");
    expect(tokenFor(source, "(")).toBe("punctuation");
    expect(tokenFor(source, ",")).toBe("punctuation");
  });

  it("colours a builtin call and what it is reading", () => {
    const source = "print(response.status_code)";

    expect(tokenFor(source, "print")).toBe(CALL);
    expect(tokenFor(source, "response")).toBe(NAME);
    expect(tokenFor(source, "status_code")).toBe(NAME);
  });

  /** CapWords is a class, which is the convention the language is written to. */
  it("reads a class as a class", () => {
    const source = 'auth = HTTPDigestAuth("ada", "lovelace")';
    expect(tokenFor(source, "HTTPDigestAuth")).toBe(TYPE);
    expect(tokenFor(source, "auth")).toBe(NAME);

    // ALL_CAPS is a constant rather than a class.
    expect(tokenFor("TIMEOUT = 30", "TIMEOUT")).toBe("bool");
  });

  it("colours the imports", () => {
    const source = ["import requests", "from requests.auth import HTTPDigestAuth"].join("\n");

    expect(tokenFor(source, "import")).toBe("keyword");
    expect(tokenFor(source, "from")).toBe("keyword");
    expect(tokenFor(source, "requests")).toBe(NAME);
    expect(tokenFor(source, "auth")).toBe(NAME);
    expect(tokenFor(source, "HTTPDigestAuth")).toBe(TYPE);
  });

  /*
   * Every JSON body this app writes is a triple-quoted string. If the state
   * does not carry, the body is coloured as code and the closing quotes open a
   * string that runs to the end of the snippet.
   */
  it("carries a triple-quoted string across lines", () => {
    const source = ['payload = """{', '  "name": "Ada"', '}"""', "after = 1"].join("\n");

    const strings = tokens(source)
      .filter(([, token]) => token === "string")
      .map(([text]) => text);
    expect(strings).toEqual(['"""{', '  "name": "Ada"', '}"""']);

    expect(tokenFor(source, "after")).toBe(NAME);
    expect(tokenFor(source, "1")).toBe("number");
  });

  it("reads both kinds of triple quote and the string prefixes", () => {
    expect(tokenFor("a = '''x'''", "'''x'''")).toBe("string");
    expect(tokenFor('a = r"\\d+"', 'r"\\d+"')).toBe("string");
    expect(tokenFor('a = b"bytes"', 'b"bytes"')).toBe("string");
  });

  /** An ordinary string ends with its line rather than running on. */
  it("keeps an ordinary string to its line", () => {
    const source = ['a = "unterminated', "b = 2"].join("\n");
    expect(tokenFor(source, "b")).toBe(NAME);
    expect(tokenFor(source, "2")).toBe("number");
  });

  it("colours a list of pairs, as a form body is written", () => {
    const source = ["payload = [", '    ("user", "ada"),', "]"].join("\n");

    expect(tokenFor(source, "payload")).toBe(NAME);
    expect(tokenFor(source, "[")).toBe("punctuation");
    expect(tokenFor(source, '"user"')).toBe("string");
    expect(tokenFor(source, "]")).toBe("punctuation");
  });

  it("colours a file part, handle and all", () => {
    const source = 'files = [("photo", ("cat.png", open("/a/cat.png", "rb"), "image/png"))]';
    expect(tokenFor(source, "files")).toBe(NAME);
    expect(tokenFor(source, "open")).toBe(CALL);
  });

  it("finds the constants and the comments", () => {
    expect(tokenFor("a = None", "None")).toBe("bool");
    expect(tokenFor("a = True", "True")).toBe("bool");
    expect(tokenFor("verify = False", "False")).toBe("bool");
    expect(tokenFor("# a note", "# a note")).toBe("comment");
  });

  it("survives what it cannot make sense of", () => {
    expect(() => tokens('a = """never closed')).not.toThrow();
    expect(() => tokens("a = 'never closed")).not.toThrow();
    expect(() => tokens("}{)(][;,|&!<>%*/+-=~`@#$^?")).not.toThrow();
    expect(() => tokens("a.")).not.toThrow();
  });
});
