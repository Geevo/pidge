import { StringStream } from "@codemirror/language";
import { describe, expect, it } from "vitest";

import { powershell } from "./powershell";

/*
 * The tokenizer returns tag paths rather than names of its own, so these are
 * what the assertions read as. Several tokens share one deliberately: a
 * variable, a key, a parameter and a member are all names of things.
 */
const NAME = "propertyName";
const CALL = "variableName.function";
const PARAMETER = "attributeName";
const TYPE = "typeName";

/**
 * Runs the tokenizer over a snippet the way CodeMirror does: one line at a
 * time, carrying the state, so that a string left open at the end of a line is
 * still open at the start of the next.
 */
function tokens(source: string): [string, string][] {
  const state = powershell.startState!(2);
  const out: [string, string][] = [];

  for (const line of source.split("\n")) {
    const stream = new StringStream(line, 2, 2);
    if (line === "") {
      powershell.blankLine?.(state, 2);
      continue;
    }
    let guard = 0;
    while (!stream.eol()) {
      const start = stream.pos;
      const token = powershell.token(stream, state);
      if (stream.pos === start) {
        // The tokenizer must always move; a mode that does not hangs the editor.
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

describe("the PowerShell tokenizer", () => {
  /*
   * The whole reason for writing this. `legacy-modes` has no name for a cmdlet,
   * so `Invoke-RestMethod` came back the colour of plain text.
   */
  it("knows a cmdlet from a parameter and a parameter from an operator", () => {
    const source = "$password = ConvertTo-SecureString 'pw' -AsPlainText -Force";

    expect(tokenFor(source, "ConvertTo-SecureString")).toBe(CALL);
    expect(tokenFor(source, "-AsPlainText")).toBe(PARAMETER);
    expect(tokenFor(source, "-Force")).toBe(PARAMETER);
    expect(tokenFor(source, "$password")).toBe(NAME);
    expect(tokenFor(source, "'pw'")).toBe("string");
  });

  /** `-eq` looks exactly like a parameter and is not one. */
  it("does not read a comparison as a parameter", () => {
    expect(tokenFor("$a -eq $b", "-eq")).toBe("operator");
    expect(tokenFor("$a -notmatch $b", "-notmatch")).toBe("operator");
    expect(tokenFor("$a -Uri $b", "-Uri")).toBe(PARAMETER);
  });

  it("colours the keys of a hashtable", () => {
    const source = ["$parameters = @{", "    Uri        = 'https://example.com/'", "}"].join("\n");

    expect(tokenFor(source, "Uri")).toBe(NAME);
    expect(tokenFor(source, "@{")).toBe("punctuation");
    expect(tokenFor(source, "'https://example.com/'")).toBe("string");
  });

  it("reads a splatted hashtable as the variable it is", () => {
    expect(tokenFor("Invoke-RestMethod @parameters", "@parameters")).toBe(NAME);
    expect(tokenFor("Invoke-RestMethod @parameters", "Invoke-RestMethod")).toBe(CALL);
  });

  /*
   * A type literal and an index are both square brackets. The difference is
   * whether a value came first, which is the one piece of context the tokenizer
   * carries for it.
   */
  it("tells a type literal from an index", () => {
    const type = "$pair = [Text.Encoding]::UTF8.GetBytes('ada:lovelace')";
    expect(tokenFor(type, "[Text.Encoding]")).toBe(TYPE);
    expect(tokenFor(type, "UTF8")).toBe(NAME);
    expect(tokenFor(type, "GetBytes")).toBe(CALL);

    const index = "$headers['Authorization'] = 'Basic abc'";
    expect(tokenFor(index, "[")).toBe("punctuation");
    expect(tokenFor(index, "'Authorization'")).toBe("string");
    expect(tokens(index).some(([, token]) => token === TYPE)).toBe(false);
  });

  /*
   * `New-Object System.Net.WebClient` writes a type without its brackets. The
   * last segment is the type's own name, not a call, whatever follows it.
   */
  it("reads a dotted name after a cmdlet as one type", () => {
    const source = "$credential = New-Object System.Management.Automation.PSCredential('ada', $p)";

    expect(tokenFor(source, "New-Object")).toBe(CALL);
    expect(tokenFor(source, "System.Management.Automation.PSCredential")).toBe(TYPE);
    // The last segment is the type's name, not a call, though a bracket follows.
    expect(tokens(source).filter(([, token]) => token === CALL)).toEqual([["New-Object", CALL]]);
  });

  it("reads the long form of a type literal", () => {
    const source =
      "$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new('/a.p12', 'pw')";

    expect(
      tokenFor(source, "[System.Security.Cryptography.X509Certificates.X509Certificate2]"),
    ).toBe(TYPE);
    expect(tokenFor(source, "new")).toBe(CALL);
  });

  /*
   * Every JSON body this app writes is a single-quoted string over several
   * lines. If the state does not carry, the body is coloured as code and the
   * closing quote reopens a string that runs to the end of the snippet.
   */
  it("carries a single-quoted string across lines", () => {
    const source = ["$body = '{", '  "name": "Ada"', "}'", "$after = 1"].join("\n");
    const all = tokens(source);

    const strings = all.filter(([, token]) => token === "string").map(([text]) => text);
    expect(strings).toEqual(["'{", '  "name": "Ada"', "}'"]);

    // The code after the string is code again.
    expect(tokenFor(source, "$after")).toBe(NAME);
    expect(tokenFor(source, "1")).toBe("number");
  });

  /** A doubled quote is an escaped one, not the end of the string. */
  it("keeps going past an escaped quote", () => {
    const source = "$body = 'Ada''s cat'";
    expect(tokens(source)).toEqual([
      ["$body", NAME],
      ["=", "operator"],
      ["'Ada''s cat'", "string"],
    ]);
  });

  it("carries a double-quoted string across lines and honours the backtick", () => {
    const source = ['$a = "one', 'two"', "$b = 2"].join("\n");
    expect(tokens(source).filter(([, token]) => token === "string").length).toBe(2);
    expect(tokenFor(source, "$b")).toBe(NAME);

    expect(tokenFor('$a = "a `" b"', '"a `" b"')).toBe("string");
  });

  it("reads a here-string to the line that closes it", () => {
    const source = ["$body = @'", "  'quoted' and $notavariable", "'@", "$after = 1"].join("\n");

    expect(tokenFor(source, "  'quoted' and $notavariable")).toBe("string");
    expect(tokenFor(source, "$after")).toBe(NAME);
  });

  it("reads both kinds of comment", () => {
    expect(tokenFor("# a note", "# a note")).toBe("comment");

    const block = ["<# a note", "over two lines #>", "$after = 1"].join("\n");
    expect(tokenFor(block, "$after")).toBe(NAME);
    expect(tokens(block).filter(([, token]) => token === "comment").length).toBe(2);
  });

  it("separates the constants from the variables", () => {
    expect(tokenFor("$a = $true", "$true")).toBe("bool");
    expect(tokenFor("$a = $null", "$null")).toBe("bool");
    expect(tokenFor("$a = $truename", "$truename")).toBe(NAME);
    expect(tokenFor("$a = ${odd name}", "${odd name}")).toBe(NAME);
    expect(tokenFor("$a = $env:HOME", "$env:HOME")).toBe(NAME);
  });

  it("finds the numbers", () => {
    expect(tokenFor("$a = 30", "30")).toBe("number");
    expect(tokenFor("$a = 1.5", "1.5")).toBe("number");
    expect(tokenFor("$a = 0xFF", "0xFF")).toBe("number");
  });

  /*
   * The generated snippets carry whatever the user typed, so the tokenizer has
   * to survive a string that never closes rather than hanging on it.
   */
  it("survives an unterminated string", () => {
    expect(() => tokens("$body = 'never closed")).not.toThrow();
    expect(() => tokens('$body = "never closed')).not.toThrow();
    expect(() => tokens("$body = @'\nstill open")).not.toThrow();
    expect(() => tokens("<# still open")).not.toThrow();
  });

  it("survives punctuation soup", () => {
    expect(() => tokens("}{)(][;,|&!<>%*/+-=~`@#$^?")).not.toThrow();
  });
});
