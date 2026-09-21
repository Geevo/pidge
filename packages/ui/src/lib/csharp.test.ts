import { StringStream } from "@codemirror/language";
import { describe, expect, it } from "vitest";

import { csharp } from "./csharp";

/*
 * The tokenizer returns tag paths rather than names of its own, so these are
 * what the assertions read as. A local, a property and a parameter share one
 * deliberately: they are all names of things rather than things being done.
 */
const NAME = "propertyName";
const CALL = "variableName.function";
const TYPE = "typeName";

/**
 * Runs the tokenizer the way CodeMirror does: one line at a time, carrying the
 * state, so a verbatim string left open at the end of a line is still open at
 * the start of the next.
 */
function tokens(source: string): [string, string][] {
  const state = csharp.startState!(2);
  const out: [string, string][] = [];

  for (const line of source.split("\n")) {
    const stream = new StringStream(line, 2, 2);
    if (line === "") {
      csharp.blankLine?.(state, 2);
      continue;
    }
    let guard = 0;
    while (!stream.eol()) {
      const start = stream.pos;
      const token = csharp.token(stream, state);
      if (stream.pos === start) {
        // A mode that fails to advance hangs the editor.
        expect.fail(`no progress at ${JSON.stringify(line.slice(start))}`);
      }
      if (token) out.push([line.slice(start, stream.pos), token]);
      if (++guard > 500) expect.fail("too many tokens for one line");
    }
    stream.start = stream.pos;
  }

  return out;
}

/** What `text` was called, or undefined if the tokenizer never produced it. */
function tokenFor(source: string, text: string): string | undefined {
  return tokens(source).find(([value]) => value === text)?.[1];
}

describe("the C# tokenizer", () => {
  /*
   * The three things `legacy-modes` left the colour of plain text, which is
   * most of what a generated snippet is made of.
   */
  it("colours the type in a construction, the class in a static call, and the local", () => {
    const source = [
      "using var client = new HttpClient();",
      "client.Timeout = TimeSpan.FromSeconds(30);",
      "Console.WriteLine(response.StatusCode);",
    ].join("\n");

    expect(tokenFor(source, "HttpClient")).toBe(TYPE);
    expect(tokenFor(source, "TimeSpan")).toBe(TYPE);
    expect(tokenFor(source, "Console")).toBe(TYPE);

    expect(tokenFor(source, "client")).toBe(NAME);
    expect(tokenFor(source, "response")).toBe(NAME);

    expect(tokenFor(source, "FromSeconds")).toBe(CALL);
    expect(tokenFor(source, "WriteLine")).toBe(CALL);
    expect(tokenFor(source, "Timeout")).toBe(NAME);
    expect(tokenFor(source, "StatusCode")).toBe(NAME);

    expect(tokenFor(source, "using")).toBe("keyword");
    expect(tokenFor(source, "var")).toBe("keyword");
    expect(tokenFor(source, "new")).toBe("keyword");
  });

  /*
   * `new HttpClient()` and `Console.WriteLine()` differ by one keyword. Without
   * it the bracket would make both of them calls.
   */
  it("reads a constructed type as a type and not as a call", () => {
    const source = 'var content = new StringContent("A photo");';

    expect(tokenFor(source, "StringContent")).toBe(TYPE);
    expect(tokens(source).some(([, token]) => token === CALL)).toBe(false);
  });

  it("reads a fully qualified construction as one type", () => {
    const source = "var handler = new System.Net.Http.HttpClientHandler();";
    expect(tokenFor(source, "System.Net.Http.HttpClientHandler")).toBe(TYPE);
  });

  /*
   * `.Headers` and `.Parse` are both PascalCase. Only the bracket after them
   * says which is a property and which is a call.
   */
  it("tells a property from a method by the bracket after it", () => {
    const source = 'part1.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");';

    expect(tokenFor(source, "part1")).toBe(NAME);
    expect(tokenFor(source, "Headers")).toBe(NAME);
    expect(tokenFor(source, "ContentType")).toBe(NAME);
    expect(tokenFor(source, "MediaTypeHeaderValue")).toBe(TYPE);
    expect(tokenFor(source, "Parse")).toBe(CALL);
  });

  it("follows a chain of members", () => {
    const source = 'Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:lovelace"));';

    expect(tokenFor(source, "Convert")).toBe(TYPE);
    expect(tokenFor(source, "ToBase64String")).toBe(CALL);
    expect(tokenFor(source, "Encoding")).toBe(TYPE);
    expect(tokenFor(source, "UTF8")).toBe(NAME);
    expect(tokenFor(source, "GetBytes")).toBe(CALL);
  });

  /*
   * A PascalCase name being assigned to is a property, not a type. It looks
   * exactly like one until the `=` arrives.
   */
  it("reads the fields of an object initializer as properties", () => {
    const source = [
      "var handler = new HttpClientHandler",
      "{",
      "    AllowAutoRedirect = false,",
      '    Credentials = new NetworkCredential("ada", "lovelace"),',
      "};",
    ].join("\n");

    expect(tokenFor(source, "AllowAutoRedirect")).toBe(NAME);
    expect(tokenFor(source, "Credentials")).toBe(NAME);
    expect(tokenFor(source, "NetworkCredential")).toBe(TYPE);
    expect(tokenFor(source, "false")).toBe("bool");
  });

  /** A comparison is not an assignment, and does not make a property. */
  it("does not read a comparison as an assignment", () => {
    expect(tokenFor("if (Status == Other)", "Status")).toBe(TYPE);
  });

  /*
   * `using System.Net.Http;` and `using var client = ...` start the same way.
   * The keyword after decides, which is why the keywords are matched first.
   */
  it("tells a using directive from a using statement", () => {
    const directive = "using System.Net.Http.Headers;";
    expect(tokenFor(directive, "System.Net.Http.Headers")).toBe(TYPE);

    const statement = "using var response = await client.SendAsync(request);";
    expect(tokenFor(statement, "var")).toBe("keyword");
    expect(tokenFor(statement, "await")).toBe("keyword");
    expect(tokenFor(statement, "response")).toBe(NAME);
    expect(tokenFor(statement, "client")).toBe(NAME);
    expect(tokenFor(statement, "SendAsync")).toBe(CALL);
    expect(tokenFor(statement, "request")).toBe(NAME);
  });

  it("knows the types the language ships with", () => {
    const source = "Console.WriteLine((int)response.StatusCode);";
    expect(tokenFor(source, "int")).toBe(TYPE);
    expect(tokenFor("string name = null;", "string")).toBe(TYPE);
    expect(tokenFor("string name = null;", "null")).toBe("bool");
  });

  /*
   * Every JSON body this app writes is a verbatim string over several lines. If
   * the state does not carry, the body is coloured as code and the closing
   * quote opens a string that runs to the end of the snippet.
   */
  it("carries a verbatim string across lines", () => {
    const source = [
      'request.Content = new StringContent(@"{',
      '  ""name"": ""Ada\'s cat"",',
      '  ""count"": 2.50',
      '}", Encoding.UTF8);',
      "var after = 1;",
    ].join("\n");

    const strings = tokens(source)
      .filter(([, token]) => token === "string")
      .map(([text]) => text);
    expect(strings).toEqual(['@"{', '  ""name"": ""Ada\'s cat"",', '  ""count"": 2.50', '}"']);

    // The code after the string is code again.
    expect(tokenFor(source, "after")).toBe(NAME);
    expect(tokenFor(source, "Encoding")).toBe(TYPE);
    expect(tokenFor(source, "1")).toBe("number");
  });

  /**
   * A quoted string cannot span lines in C#, so an unterminated one stops at
   * the end of its own rather than colouring everything after it.
   */
  it("keeps an ordinary string to its line", () => {
    const source = ['var a = "unterminated', "var b = 2;"].join("\n");
    expect(tokenFor(source, "b")).toBe(NAME);
    expect(tokenFor(source, "2")).toBe("number");
  });

  it("honours a backslash escape", () => {
    expect(tokenFor('var a = "a \\" b";', '"a \\" b"')).toBe("string");
  });

  it("reads both kinds of comment", () => {
    expect(tokenFor("// a note", "// a note")).toBe("comment");

    const block = ["/* a note", "over two lines */", "var after = 1;"].join("\n");
    expect(tokenFor(block, "after")).toBe(NAME);
    expect(tokens(block).filter(([, token]) => token === "comment").length).toBe(2);
  });

  it("finds the numbers", () => {
    expect(tokenFor("var a = 30;", "30")).toBe("number");
    expect(tokenFor("var a = 2.50m;", "2.50m")).toBe("number");
    expect(tokenFor("var a = 0xFF;", "0xFF")).toBe("number");
  });

  /*
   * The snippets carry whatever the user typed, so the tokenizer has to survive
   * anything rather than hang on it.
   */
  it("survives what it cannot make sense of", () => {
    expect(() => tokens('var a = @"never closed')).not.toThrow();
    expect(() => tokens("/* never closed")).not.toThrow();
    expect(() => tokens("}{)(][;,|&!<>%*/+-=~`@#$^?")).not.toThrow();
    expect(() => tokens("using")).not.toThrow();
    expect(() => tokens("new")).not.toThrow();
    expect(() => tokens("a.")).not.toThrow();
  });
});
