import type { StreamParser, StringStream } from "@codemirror/language";

/**
 * Python, tokenized here rather than taken from `@codemirror/legacy-modes`.
 *
 * That mode colours the keywords, the strings and whatever follows a dot, and
 * leaves everything else: the names being assigned to, the calls, and all of
 * the punctuation. In a snippet that is mostly one long call with keyword
 * arguments, that is most of the snippet.
 *
 * As in `lib/csharp`, the naming convention does the work a type checker would:
 * PEP 8 puts classes in CapWords and everything else in lower case, so
 * `HTTPDigestAuth` reads as a class and `requests` reads as a name, without
 * anything here having to know what either of them is.
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
 * What each name is painted as, as the tag paths CodeMirror resolves against
 * `@lezer/highlight`. See `lib/powershell` for why not a `tokenTable`.
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
  method: "variableName.function",
  variable: "propertyName",
  property: "propertyName",
  word: null,
};

const KEYWORDS =
  /^(?:and|as|assert|async|await|break|class|continue|def|del|elif|else|except|finally|for|from|global|if|import|in|is|lambda|match|nonlocal|not|or|pass|raise|return|try|while|with|yield)\b/;

const CONSTANTS = /^(?:True|False|None)\b/;

/** The prefixes a string literal can carry: raw, bytes, formatted, unicode. */
const TRIPLE_QUOTE = /^(?:[rRbBuUfF]|[rR][bBfF]|[bBfF][rR])?("""|''')/;
const QUOTE = /^(?:[rRbBuUfF]|[rR][bBfF]|[bBfF][rR])?("|')/;

/** CapWords is a class; ALL_CAPS is a constant. PEP 8, and near enough always. */
const CAP_WORDS = /^[A-Z][A-Za-z0-9_]*[a-z]/;
const ALL_CAPS = /^[A-Z][A-Z0-9_]*$/;

interface State {
  /** Set while a triple-quoted string is still open at the end of a line. */
  tokenize: ((stream: StringStream, state: State) => string) | null;
  /** The delimiter that will close the string being read. */
  stringEnd: string | null;
  /** Whether what came last can have a member taken from it. */
  afterValue: boolean;
  /** Set between a `.` and the name after it. */
  afterAccessor: boolean;
}

export const python: StreamParser<State> = {
  name: "python",

  startState: () => ({
    tokenize: null,
    stringEnd: null,
    afterValue: false,
    afterAccessor: false,
  }),

  token(stream, state) {
    if (state.tokenize) return state.tokenize(stream, state);
    if (stream.eatSpace()) return null;

    const accessor = state.afterAccessor;
    state.afterAccessor = false;
    const token = read(stream, state, accessor);
    state.afterValue = VALUE.has(token);
    return TAGS[token] ?? null;
  },

  languageData: { commentTokens: { line: "#" } },
};

function read(stream: StringStream, state: State, accessor: boolean): string {
  if (stream.eat("#")) {
    stream.skipToEnd();
    return "comment";
  }

  // Triple quotes before single ones, or `"""` would read as an empty string.
  const triple = stream.match(TRIPLE_QUOTE) as RegExpMatchArray | null;
  if (triple) {
    state.stringEnd = triple[1]!;
    state.tokenize = tripleQuoted;
    return tripleQuoted(stream, state);
  }
  const quote = stream.match(QUOTE) as RegExpMatchArray | null;
  if (quote) return quoted(stream, quote[1]!);

  if (stream.match(/^0[xXoObB][\da-fA-F_]+/)) return "number";
  if (stream.match(/^\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][-+]?\d+)?[jJ]?/)) return "number";

  /*
   * A member. As in C#, only the bracket after it says whether `.post` is
   * something being read or something being called.
   */
  if (accessor && stream.match(/^[A-Za-z_]\w*/)) {
    return stream.peek() === "(" ? "method" : "property";
  }

  if (CONSTANTS.test(stream.string.slice(stream.pos))) {
    stream.match(CONSTANTS);
    return "constant";
  }
  if (KEYWORDS.test(stream.string.slice(stream.pos))) {
    stream.match(KEYWORDS);
    return "keyword";
  }

  const name = stream.match(/^[A-Za-z_]\w*/) as RegExpMatchArray | null;
  if (name) {
    if (CAP_WORDS.test(name[0])) return "type";
    if (ALL_CAPS.test(name[0])) return "constant";
    return stream.peek() === "(" ? "method" : "variable";
  }

  if (state.afterValue && stream.eat(".")) {
    state.afterAccessor = true;
    return "punctuation";
  }

  if (stream.match(/^[)\]}]/)) return "close";
  if (stream.match(/^[[({,;:.]/)) return "punctuation";
  if (stream.match(/^(?:\*\*=|\/\/=|[-+*/%@&|^]=|==|!=|<=|>=|<<|>>|\*\*|\/\/|->|[-+*/%@&|^~<>=])/))
    return "operator";

  stream.next();
  return "word";
}

/** A string that ends with its line, which is every quoted string but one. */
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
 * `"""..."""`, which keeps its newlines. Every JSON body this app writes is one
 * of these, so the state has to carry or the body is coloured as code.
 */
function tripleQuoted(stream: StringStream, state: State): string {
  while (!stream.eol()) {
    if (stream.eat("\\")) {
      stream.next();
      continue;
    }
    if (state.stringEnd && stream.match(state.stringEnd)) {
      state.tokenize = null;
      state.stringEnd = null;
      return "string";
    }
    stream.next();
  }
  return "string";
}
