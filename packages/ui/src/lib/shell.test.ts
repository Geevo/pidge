import { StringStream } from "@codemirror/language";
import { describe, expect, it } from "vitest";

import { shell } from "./shell";

const VALUE = "string";
const FLAG = "attributeName";
const COMMAND = "variableName.function";

/** Runs the tokenizer the way CodeMirror does, one line at a time. */
function tokens(source: string): [string, string][] {
  const state = shell.startState!(2);
  const out: [string, string][] = [];

  for (const line of source.split("\n")) {
    const stream = new StringStream(line, 2, 2);
    if (line === "") {
      shell.blankLine?.(state, 2);
      continue;
    }
    let guard = 0;
    while (!stream.eol()) {
      const start = stream.pos;
      const token = shell.token(stream, state);
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

describe("the shell tokenizer", () => {
  it("colours the command, its flags and their values", () => {
    const source = "curl --request POST --max-time 30";

    expect(tokenFor(source, "curl")).toBe(COMMAND);
    expect(tokenFor(source, "--request")).toBe(FLAG);
    expect(tokenFor(source, "POST")).toBe(VALUE);
    expect(tokenFor(source, "--max-time")).toBe(FLAG);
    expect(tokenFor(source, "30")).toBe("number");
  });

  /*
   * The backslashes are what hold a curl command together. The one at the end
   * of a line is not an escape, and the next line is not a new command.
   */
  it("follows a command across its line continuations", () => {
    const source = [
      "curl --request POST \\",
      "  --url 'https://example.com/' \\",
      "  --location",
    ].join("\n");

    expect(tokenFor(source, "\\")).toBe("operator");
    expect(tokenFor(source, "--url")).toBe(FLAG);
    expect(tokenFor(source, "--location")).toBe(FLAG);
    // Only the first word is the command; `--url` on line two is not one.
    expect(tokens(source).filter(([, token]) => token === COMMAND)).toEqual([["curl", COMMAND]]);
  });

  /** A line that was not continued starts a command of its own. */
  it("starts a new command on a new line", () => {
    const source = ["curl --url 'https://example.com/'", "echo done"].join("\n");
    expect(tokenFor(source, "echo")).toBe(COMMAND);
  });

  /*
   * `'{ ... }'` over several lines is every JSON body this app writes. A single
   * quoted string has no escapes at all and ends at the very next quote.
   */
  it("carries a single-quoted string across lines", () => {
    const source = ["curl --data-raw '{", '  "name": "Ada"', "}' \\", "  --location"].join("\n");

    const strings = tokens(source)
      .filter(([, token]) => token === "string")
      .map(([text]) => text);
    expect(strings).toContain("'{");
    expect(strings).toContain('  "name": "Ada"');
    expect(strings).toContain("}'");

    // The command is code again afterwards.
    expect(tokenFor(source, "--location")).toBe(FLAG);
  });

  /*
   * `'\''` is how a quote gets inside a quoted string: close, escape, reopen.
   * Three tokens, one colour, because it is one string to anybody reading it.
   */
  it("paints an escaped quote as part of the string around it", () => {
    const source = "curl --data-raw 'Ada'\\''s cat'";
    const coloured = tokens(source).filter(([text]) => text !== "curl" && text !== "--data-raw");

    expect(coloured.every(([, token]) => token === "string")).toBe(true);
    expect(coloured.map(([text]) => text).join("")).toBe("'Ada'\\''s cat'");
  });

  it("reads a double-quoted string and its escapes", () => {
    expect(tokenFor('echo "a \\" b"', '"a \\" b"')).toBe("string");
    expect(tokenFor('echo "$HOME"', '"$HOME"')).toBe("string");
  });

  it("finds a variable and a comment", () => {
    expect(tokenFor("curl --url $BASE_URL", "$BASE_URL")).toBe("propertyName");
    expect(tokenFor("# a note", "# a note")).toBe("comment");
  });

  /** Anything that ends a command means the next word starts another. */
  it("knows where one command ends and the next begins", () => {
    const source = "curl --url 'https://example.com/' | jq .items";
    expect(tokenFor(source, "curl")).toBe(COMMAND);
    expect(tokenFor(source, "|")).toBe("operator");
    expect(tokenFor(source, "jq")).toBe(COMMAND);
  });

  /** A form part is one word, dividers and all. */
  it("keeps a form argument whole", () => {
    const source = "curl --form 'photo=@/home/ada/cat.png;type=image/png'";
    expect(tokenFor(source, "'photo=@/home/ada/cat.png;type=image/png'")).toBe("string");
  });

  it("survives what it cannot make sense of", () => {
    expect(() => tokens("curl --data-raw 'never closed")).not.toThrow();
    expect(() => tokens('curl --data-raw "never closed')).not.toThrow();
    expect(() => tokens("}{)(][;,|&!<>%*/+-=~`@#$^?")).not.toThrow();
    expect(() => tokens("\\")).not.toThrow();
  });
});
