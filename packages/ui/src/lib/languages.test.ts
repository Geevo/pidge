import { StringStream } from "@codemirror/language";
import type { StreamParser } from "@codemirror/language";
import { describe, expect, it } from "vitest";

import type { CurlyState } from "./curly";
import { go, java, php, rust, zig } from "./languages";

const NAME = "propertyName";
const CALL = "variableName.function";
const TYPE = "typeName";

/** Runs a tokenizer the way CodeMirror does, one line at a time. */
function tokens(mode: StreamParser<CurlyState>, source: string): [string, string][] {
  const state = mode.startState!(2);
  const out: [string, string][] = [];

  for (const line of source.split("\n")) {
    const stream = new StringStream(line, 2, 2);
    if (line === "") {
      mode.blankLine?.(state, 2);
      continue;
    }
    let guard = 0;
    while (!stream.eol()) {
      const start = stream.pos;
      const token = mode.token(stream, state);
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

function tokenFor(
  mode: StreamParser<CurlyState>,
  source: string,
  text: string,
): string | undefined {
  return tokens(mode, source).find(([value]) => value === text)?.[1];
}

describe("Rust", () => {
  it("colours a call chain, its types and its names", () => {
    const source = 'let response = client.post(url).header("X-Trace", value).send()?;';

    expect(tokenFor(rust, source, "let")).toBe("keyword");
    expect(tokenFor(rust, source, "response")).toBe(NAME);
    expect(tokenFor(rust, source, "client")).toBe(NAME);
    expect(tokenFor(rust, source, "post")).toBe(CALL);
    expect(tokenFor(rust, source, "header")).toBe(CALL);
    expect(tokenFor(rust, source, '"X-Trace"')).toBe("string");
  });

  /*
   * Every JSON body this app writes in Rust is a raw string, and the fence can
   * be longer than one hash when the body carries one of its own.
   */
  it("carries a raw string across lines, whatever its fence", () => {
    const source = ['let payload = r#"{', '  "name": "Ada"', '}"#;', "let after = 1;"].join("\n");

    const strings = tokens(rust, source)
      .filter(([, token]) => token === "string")
      .map(([text]) => text);
    expect(strings).toEqual(['r#"{', '  "name": "Ada"', '}"#']);
    expect(tokenFor(rust, source, "after")).toBe(NAME);

    const longer = ['let payload = r##"a "# b"##;', "let after = 2;"].join("\n");
    expect(tokenFor(rust, longer, "after")).toBe(NAME);
  });

  /** A lifetime is not the start of a character literal. */
  it("tells a lifetime from a character", () => {
    expect(tokenFor(rust, "fn f<'a>(x: &'a str) {}", "'a")).toBe("keyword");
    expect(tokenFor(rust, "let c = 'x';", "'x'")).toBe("string");
  });

  it("knows a macro and an attribute", () => {
    expect(tokenFor(rust, 'println!("{}", x);', "println!")).toBe("meta");
    expect(tokenFor(rust, "#[tokio::main]", "#[tokio::main]")).toBe("meta");
  });

  it("knows the types the language ships with", () => {
    expect(tokenFor(rust, "let n: u32 = 1;", "u32")).toBe(TYPE);
    expect(tokenFor(rust, "let r: Result<String> = x;", "String")).toBe(TYPE);
    expect(tokenFor(rust, "let v = Some(1);", "Some")).toBe("bool");
  });
});

describe("Go", () => {
  it("colours a declaration, its call and its types", () => {
    const source = 'req, err := http.NewRequest("POST", url, payload)';

    expect(tokenFor(go, source, "req")).toBe(NAME);
    expect(tokenFor(go, source, "http")).toBe(NAME);
    expect(tokenFor(go, source, "NewRequest")).toBe(CALL);
    expect(tokenFor(go, source, '"POST"')).toBe("string");
    expect(tokenFor(go, source, ":=")).toBe("operator");
  });

  /** A backtick string keeps its newlines and has no escapes at all. */
  it("carries a backtick string across lines", () => {
    const source = ["payload := `{", '  "name": "Ada"', "}`", "after := 1"].join("\n");

    const strings = tokens(go, source)
      .filter(([, token]) => token === "string")
      .map(([text]) => text);
    expect(strings).toEqual(["`{", '  "name": "Ada"', "}`"]);
    expect(tokenFor(go, source, "after")).toBe(NAME);
  });

  it("knows its keywords and its types", () => {
    expect(tokenFor(go, "func main() {", "func")).toBe("keyword");
    expect(tokenFor(go, "if err != nil {", "nil")).toBe("bool");
    expect(tokenFor(go, "var s string", "string")).toBe(TYPE);
  });
});

describe("Java", () => {
  it("colours a builder chain", () => {
    const source = "HttpClient client = HttpClient.newBuilder().build();";

    expect(tokenFor(java, source, "HttpClient")).toBe(TYPE);
    expect(tokenFor(java, source, "client")).toBe(NAME);
    expect(tokenFor(java, source, "newBuilder")).toBe(CALL);
    expect(tokenFor(java, source, "build")).toBe(CALL);
  });

  /*
   * A text block is the one Java string that runs over lines, and it has to be
   * matched before the plain quote or `"""` reads as two empty strings.
   */
  it("carries a text block across lines", () => {
    const source = ['String payload = """', "{", '  "name": "Ada"', '}""";', "int after = 1;"].join(
      "\n",
    );

    expect(tokenFor(java, source, "after")).toBe(NAME);
    expect(tokens(java, source).filter(([, token]) => token === "string").length).toBeGreaterThan(
      2,
    );
  });

  it("knows an annotation, a keyword and a primitive", () => {
    expect(tokenFor(java, "@Override", "@Override")).toBe("meta");
    expect(tokenFor(java, "public class Main {", "public")).toBe("keyword");
    expect(tokenFor(java, "int n = 1;", "int")).toBe(TYPE);
    expect(tokenFor(java, "String s = null;", "null")).toBe("bool");
  });
});

describe("Zig", () => {
  it("colours a builtin, a call and a name", () => {
    const source = 'const std = @import("std");';

    expect(tokenFor(zig, source, "const")).toBe("keyword");
    expect(tokenFor(zig, source, "std")).toBe(NAME);
    expect(tokenFor(zig, source, "@import")).toBe(CALL);
    expect(tokenFor(zig, source, '"std"')).toBe("string");
  });

  /** Every line of a multiline string opens with its own `\\`. */
  it("reads a multiline string line by line", () => {
    const source = [
      "const payload =",
      "    \\\\{",
      '    \\\\  "name": "Ada"',
      "    \\\\}",
      ";",
    ].join("\n");

    const strings = tokens(zig, source).filter(([, token]) => token === "string");
    expect(strings.length).toBe(3);
    expect(strings[1]![0]).toBe('\\\\  "name": "Ada"');
  });

  /** `.method = .POST` is a field being set and an enum value being read. */
  it("knows a field being set from an enum literal", () => {
    expect(tokenFor(zig, ".method = .POST,", ".method")).toBe(NAME);
    expect(tokenFor(zig, ".method = .POST,", ".POST")).toBe("bool");
    expect(tokenFor(zig, "var client = std.http.Client{};", "http")).toBe(NAME);
  });

  it("knows its keywords and its types", () => {
    expect(tokenFor(zig, "pub fn main() !void {", "pub")).toBe("keyword");
    expect(tokenFor(zig, "var n: u32 = 1;", "u32")).toBe(TYPE);
    expect(tokenFor(zig, "const x = true;", "true")).toBe("bool");
  });
});

describe("PHP", () => {
  it("colours the tag, the variables and the calls", () => {
    const source = "<?php\n$curl = curl_init();";

    expect(tokenFor(php, source, "<?php")).toBe("keyword");
    expect(tokenFor(php, source, "$curl")).toBe(NAME);
    expect(tokenFor(php, source, "curl_init")).toBe(CALL);
  });

  /*
   * A single-quoted string runs over lines and has two escapes, which is what
   * every body this app writes in PHP relies on.
   */
  it("carries a single-quoted string across lines", () => {
    const source = ["$payload = '{", '  "name": "Ada\\\'s cat"', "}';", "$after = 1;"].join("\n");

    expect(tokenFor(php, source, "$after")).toBe(NAME);
    expect(tokens(php, source).filter(([, token]) => token === "string").length).toBe(3);
  });

  it("reads an object operator as reaching for a member", () => {
    const source = "echo $response->getStatusCode(), PHP_EOL;";

    expect(tokenFor(php, source, "$response")).toBe(NAME);
    expect(tokenFor(php, source, "->")).toBe("punctuation");
    expect(tokenFor(php, source, "getStatusCode")).toBe(CALL);
    // A name in capitals is a constant, which is how ext-curl names everything.
    expect(tokenFor(php, source, "PHP_EOL")).toBe("bool");
  });

  it("reads both kinds of comment", () => {
    expect(tokenFor(php, "# a note", "# a note")).toBe("comment");
    expect(tokenFor(php, "// a note", "// a note")).toBe("comment");
  });
});

describe("all of them", () => {
  const modes: [string, StreamParser<CurlyState>][] = [
    ["rust", rust],
    ["go", go],
    ["java", java],
    ["php", php],
    ["zig", zig],
  ];

  /*
   * The snippets carry whatever the user typed, so every mode has to survive
   * anything rather than hang on it. A mode that fails to advance freezes the
   * editor, which `tokens` fails on.
   */
  it.each(modes)("%s survives what it cannot make sense of", (_name, mode) => {
    expect(() => tokens(mode, '"never closed')).not.toThrow();
    expect(() => tokens(mode, "/* never closed")).not.toThrow();
    expect(() => tokens(mode, "}{)(][;,|&!<>%*/+-=~`@#$^?")).not.toThrow();
    expect(() => tokens(mode, "a.")).not.toThrow();
    expect(() => tokens(mode, "")).not.toThrow();
  });

  it.each(modes)("%s colours its comments and numbers", (_name, mode) => {
    expect(tokenFor(mode, "// a note", "// a note")).toBe("comment");
    expect(tokenFor(mode, "x = 30;", "30")).toBe("number");
  });
});
