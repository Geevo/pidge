import type { StreamParser, StringStream } from "@codemirror/language";

/**
 * C#, tokenized here rather than taken from `@codemirror/legacy-modes`.
 *
 * That mode colours the keywords and the strings and stops. It has a list of
 * the types the language ships with, so `int` and `TimeSpan` come out as types
 * and `HttpClient` does not; everything else — a class, a method, a property, a
 * local — is one token name the app's highlight style does not paint. Most of a
 * generated snippet is those, so most of it was the colour of plain text.
 *
 * There is no type checker here and there does not need to be one. C# is
 * written to a naming convention that says most of what colouring wants to
 * know: a name in PascalCase is a type, a method or a property, and a name in
 * camelCase is a local or a parameter. What the convention leaves ambiguous,
 * two characters of context settle — whether `new` came first, whether a
 * bracket follows, whether the name is being assigned to.
 */

/** Everything that can end a value, and so makes the next `.` a member access. */
const VALUE = new Set([
  "variable",
  "type",
  "property",
  "method",
  "string",
  "number",
  "constant",
  "close",
]);

/**
 * What each name is painted as, written as the tag paths CodeMirror resolves
 * against `@lezer/highlight`.
 *
 * By a path rather than through a `tokenTable`, because that table is only
 * consulted for names CodeMirror does not already know, and its own mapping for
 * a name like `variable` silently wins. See `lib/powershell`, where that cost
 * an afternoon.
 *
 * The colours follow VS Code's dark theme: a type reads as a type, a call reads
 * as a call, and a local, a parameter and a property are one colour, being
 * names of things rather than things being done.
 */
const TAGS: Record<string, string | null> = {
  comment: "comment",
  string: "string",
  number: "number",
  keyword: "keyword",
  constant: "bool",
  operator: "operator",
  punctuation: "punctuation",
  close: "punctuation",
  type: "typeName",
  namespace: "typeName",
  method: "variableName.function",
  variable: "propertyName",
  property: "propertyName",
  word: null,
};

const KEYWORDS =
  /^(?:abstract|as|async|await|base|break|case|catch|checked|class|const|continue|default|delegate|do|else|enum|event|explicit|extern|finally|fixed|for|foreach|goto|if|implicit|in|interface|internal|is|lock|namespace|new|operator|out|override|params|private|protected|public|readonly|record|ref|return|sealed|sizeof|stackalloc|static|struct|switch|this|throw|try|typeof|unchecked|unsafe|using|var|virtual|volatile|where|while|yield)\b/;

/** The literals, which are keywords the highlight style paints as constants. */
const CONSTANTS = /^(?:true|false|null)\b/;

/** The types the language ships with, which are spelled as keywords. */
const BUILTIN_TYPES =
  /^(?:bool|byte|char|decimal|double|dynamic|float|int|long|nint|nuint|object|sbyte|short|string|uint|ulong|ushort|void)\b/;

interface State {
  /** Set while a verbatim string or a block comment is open at a line's end. */
  tokenize: ((stream: StringStream, state: State) => string) | null;
  /** Whether what came last can have a member taken from it. */
  afterValue: boolean;
  /** Set between a `.` and the name after it. */
  afterAccessor: boolean;
  /** Set between `new` and the type it is constructing. */
  afterNew: boolean;
  /** Set between `using` and the namespace it imports. */
  afterUsing: boolean;
}

export const csharp: StreamParser<State> = {
  name: "csharp",

  startState: () => ({
    tokenize: null,
    afterValue: false,
    afterAccessor: false,
    afterNew: false,
    afterUsing: false,
  }),

  token(stream, state) {
    if (state.tokenize) return state.tokenize(stream, state);
    if (stream.eatSpace()) return null;

    const context = {
      accessor: state.afterAccessor,
      constructing: state.afterNew,
      importing: state.afterUsing,
    };
    state.afterAccessor = false;
    state.afterNew = false;
    state.afterUsing = false;

    const token = read(stream, state, context);
    state.afterValue = VALUE.has(token);
    return TAGS[token] ?? null;
  },

  languageData: {
    commentTokens: { line: "//", block: { open: "/*", close: "*/" } },
    closeBrackets: { brackets: ["(", "[", "{", "'", '"'] },
  },
};

interface Context {
  /** The name after a `.`, which is a member rather than anything of its own. */
  accessor: boolean;
  /** The name after `new`, which is a type however it is spelled. */
  constructing: boolean;
  /** The name after `using`, which is a namespace rather than a type. */
  importing: boolean;
}

function read(stream: StringStream, state: State, context: Context): string {
  if (stream.match("//")) {
    stream.skipToEnd();
    return "comment";
  }
  if (stream.match("/*")) {
    state.tokenize = blockComment;
    return blockComment(stream, state);
  }

  // `@"..."` keeps its newlines, so it is the one string that carries state.
  if (stream.match(/^\$?@"/)) {
    state.tokenize = verbatimString;
    return verbatimString(stream, state);
  }
  if (stream.match(/^\$?"/)) return quoted(stream, '"');
  if (stream.eat("'")) return quoted(stream, "'");

  if (stream.match(/^0[xXbB][\da-fA-F_]+[uUlL]*/)) return "number";
  if (stream.match(/^\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][-+]?\d+)?[uUlLfFdDmM]*/)) return "number";

  /*
   * A member, which is the one place the convention says nothing: `.Headers`
   * and `.Parse` are both PascalCase, and only the bracket tells them apart.
   */
  if (context.accessor && stream.match(/^[A-Za-z_]\w*/)) {
    return stream.peek() === "(" ? "method" : "property";
  }

  /*
   * The keywords come before the two branches below so that `using var x = ...`
   * is read as the statement it is: `var` is a keyword, not the first segment
   * of a namespace, and matching it here is what tells that line from a
   * `using System.Net.Http;` directive.
   */
  if (CONSTANTS.test(stream.string.slice(stream.pos))) {
    stream.match(CONSTANTS);
    return "constant";
  }
  if (BUILTIN_TYPES.test(stream.string.slice(stream.pos))) {
    stream.match(BUILTIN_TYPES);
    return "type";
  }
  if (KEYWORDS.test(stream.string.slice(stream.pos))) {
    const keyword = stream.match(KEYWORDS) as RegExpMatchArray;
    if (keyword[0] === "new") state.afterNew = true;
    if (keyword[0] === "using") state.afterUsing = true;
    return "keyword";
  }

  // `using System.Net.Http;` — a namespace, taken whole.
  if (context.importing && stream.match(/^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*/)) return "namespace";

  // `new HttpClient()` — a type, though a bracket follows it.
  if (context.constructing && stream.match(/^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*/)) return "type";

  const name = stream.match(/^[A-Za-z_]\w*/) as RegExpMatchArray | null;
  if (name) {
    const pascal = /^[A-Z]/.test(name[0]);
    if (stream.peek() === "(") return "method";
    /*
     * `AllowAutoRedirect = false` inside an object initializer. Without this a
     * PascalCase name on its own would read as a type, which is what it looks
     * like until the `=` arrives. `==` is a comparison and not this.
     */
    if (pascal && stream.match(/^\s*=[^=]/, false)) return "property";
    return pascal ? "type" : "variable";
  }

  if (state.afterValue && stream.eat(".")) {
    state.afterAccessor = true;
    return "punctuation";
  }

  if (stream.match(/^[)\]}]/)) return "close";
  if (stream.match(/^[[({,;.:]/)) return "punctuation";
  if (
    stream.match(
      /^(?:=>|\?\?=|\?\?|\?\.|<<=|>>=|[-+*/%&|^!<>=]=|&&|\|\||\+\+|--|[-+*/%&|^!<>=~?:])/,
    )
  )
    return "operator";

  stream.next();
  return "word";
}

/**
 * A string that ends with its line. C# lets neither a quoted string nor a
 * character literal run over one, so an unterminated string stops here rather
 * than colouring the rest of the snippet.
 */
function quoted(stream: StringStream, quote: string): string {
  while (!stream.eol()) {
    if (stream.eat("\\")) {
      stream.next();
      continue;
    }
    if (stream.eat(quote)) break;
    stream.next();
  }
  return "string";
}

/**
 * `@"..."`, which keeps its newlines and in which the only escape is a doubled
 * quote. Every JSON body this app writes is one of these.
 */
function verbatimString(stream: StringStream, state: State): string {
  while (!stream.eol()) {
    if (stream.eat('"')) {
      if (!stream.eat('"')) {
        state.tokenize = null;
        return "string";
      }
      continue;
    }
    stream.next();
  }
  return "string";
}

function blockComment(stream: StringStream, state: State): string {
  while (!stream.eol()) {
    if (stream.match("*/")) {
      state.tokenize = null;
      return "comment";
    }
    stream.next();
  }
  return "comment";
}
