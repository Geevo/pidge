import type { StreamParser, StringStream } from "@codemirror/language";

/**
 * The shared half of a tokenizer for the languages written with braces.
 *
 * Rust, Go, Java, PHP and Zig are lexically much the same thing: `//` and `/*`
 * comments, double-quoted strings with backslash escapes, numbers, a keyword
 * list, and names reached through a dot. What differs between them is a handful
 * of rules each — Rust's raw strings and lifetimes, Go's backtick strings,
 * Java's text blocks, PHP's `$name` and `->`, Zig's `\\` lines and `@builtin` —
 * and those are what a language passes in.
 *
 * Writing five near-identical files would have meant five places to fix the
 * next thing that is wrong with all of them.
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
  escape: "string",
  number: "number",
  keyword: "keyword",
  constant: "bool",
  operator: "operator",
  punctuation: "punctuation",
  close: "punctuation",
  type: "typeName",
  namespace: "typeName",
  macro: "meta",
  attribute: "meta",
  method: "variableName.function",
  variable: "propertyName",
  property: "propertyName",
  word: null,
};

/** What a language hands its own rules, so it can see where it is. */
export interface Context {
  /** The name after a `.`, `->` or `::`, which is a member of something. */
  accessor: boolean;
  /** Whether what came last can have a member taken from it. */
  afterValue: boolean;
}

export interface CurlyState {
  tokenize: ((stream: StringStream, state: CurlyState) => string) | null;
  /** The delimiter that will close whatever is open, for the rules that need it. */
  pending: string | null;
  afterValue: boolean;
  afterAccessor: boolean;
}

export interface CurlyLanguage {
  name: string;
  keywords: RegExp;
  constants: RegExp;
  /** The types the language spells as keywords: `int`, `u32`, `string`. */
  builtinTypes?: RegExp;
  /** `#` starts a comment as well as `//`. */
  hashComments?: boolean;
  /** How a member is reached, beyond the plain `.`. */
  accessors?: RegExp;
  /**
   * The language's own rules, tried before the shared ones. Returning null
   * without moving the stream falls through to them.
   */
  rules?: (stream: StringStream, state: CurlyState, context: Context) => string | null;
}

/** Builds a parser for one language from the rules it does not share. */
export function curly(language: CurlyLanguage): StreamParser<CurlyState> {
  return {
    name: language.name,

    startState: () => ({
      tokenize: null,
      pending: null,
      afterValue: false,
      afterAccessor: false,
    }),

    token(stream, state) {
      if (state.tokenize) return state.tokenize(stream, state);
      if (stream.eatSpace()) return null;

      const context = { accessor: state.afterAccessor, afterValue: state.afterValue };
      state.afterAccessor = false;

      const token = read(stream, state, context, language);
      state.afterValue = VALUE.has(token);
      return TAGS[token] ?? null;
    },

    languageData: {
      commentTokens: { line: "//", block: { open: "/*", close: "*/" } },
    },
  };
}

function read(
  stream: StringStream,
  state: CurlyState,
  context: Context,
  language: CurlyLanguage,
): string {
  const start = stream.pos;
  const own = language.rules?.(stream, state, context);
  if (own !== null && own !== undefined) return own;
  // A rule that moved the stream without naming a token would lose the text.
  if (stream.pos !== start) return "word";

  if (stream.match("//")) {
    stream.skipToEnd();
    return "comment";
  }
  if (language.hashComments && stream.eat("#")) {
    stream.skipToEnd();
    return "comment";
  }
  if (stream.match("/*")) {
    state.tokenize = blockComment;
    return blockComment(stream, state);
  }

  if (stream.eat('"')) {
    state.pending = '"';
    return quoted(stream, state);
  }

  if (stream.match(/^0[xXoObB][\da-fA-F_]+[a-zA-Z0-9_]*/)) return "number";
  if (stream.match(/^\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][-+]?\d+)?[a-zA-Z0-9_]*/)) return "number";

  // A member: only the bracket after it says whether it is read or called.
  if (context.accessor && stream.match(/^[A-Za-z_]\w*/)) {
    return stream.peek() === "(" ? "method" : "property";
  }

  if (language.constants.test(rest(stream))) {
    stream.match(language.constants);
    return "constant";
  }
  if (language.builtinTypes?.test(rest(stream))) {
    stream.match(language.builtinTypes);
    return "type";
  }
  if (language.keywords.test(rest(stream))) {
    stream.match(language.keywords);
    return "keyword";
  }

  const name = stream.match(/^[A-Za-z_]\w*/) as RegExpMatchArray | null;
  if (name) {
    if (stream.peek() === "(") return "method";
    // CapWords is a type, which every one of these languages agrees on.
    if (/^[A-Z]/.test(name[0])) return "type";
    return "variable";
  }

  const accessor = language.accessors ?? /^\./;
  if (state.afterValue && stream.match(accessor)) {
    state.afterAccessor = true;
    return "punctuation";
  }

  if (stream.match(/^[)\]}]/)) return "close";
  /*
   * The operators come before the punctuation because several of them open
   * with a punctuation character: `:=` is Go's declaration and not a colon
   * followed by an equals.
   */
  if (
    stream.match(
      /^(?:<<=|>>=|\.\.=|\.\.\.|\.\.|:=|=>|->|::|&&|\|\||\+\+|--|[-+*/%&|^!<>=]=|[-+*/%&|^!<>=~?@$])/,
    )
  ) {
    return "operator";
  }
  if (stream.match(/^[[({,;:.]/)) return "punctuation";

  stream.next();
  return "word";
}

/** What is left of the line, for the rules that look before they move. */
export function rest(stream: StringStream): string {
  return stream.string.slice(stream.pos);
}

/**
 * A double-quoted string, which none of these languages lets run over a line.
 * An unterminated one stops with its own line rather than colouring the rest.
 */
function quoted(stream: StringStream, state: CurlyState): string {
  const quote = state.pending ?? '"';
  while (!stream.eol()) {
    if (stream.eat("\\")) {
      stream.next();
      continue;
    }
    if (stream.eat(quote)) break;
    stream.next();
  }
  state.pending = null;
  state.tokenize = null;
  return "string";
}

/**
 * A string that does run over lines, ending at `close`. Raw strings, text
 * blocks and heredocs all work this way; what differs is only the delimiter.
 */
export function delimited(close: string) {
  return function open(stream: StringStream, state: CurlyState): string {
    state.pending = close;
    state.tokenize = carry;
    return carry(stream, state);
  };
}

function carry(stream: StringStream, state: CurlyState): string {
  const close = state.pending;
  while (!stream.eol()) {
    if (close && stream.match(close)) {
      state.pending = null;
      state.tokenize = null;
      return "string";
    }
    stream.next();
  }
  return "string";
}

function blockComment(stream: StringStream, state: CurlyState): string {
  while (!stream.eol()) {
    if (stream.match("*/")) {
      state.tokenize = null;
      return "comment";
    }
    stream.next();
  }
  return "comment";
}
