import type { StreamParser, StringStream } from "@codemirror/language";

/**
 * PowerShell, tokenized here rather than taken from `@codemirror/legacy-modes`.
 *
 * That mode knows the language but not the parts of it a generated snippet is
 * made of: a cmdlet, a hashtable key, a type literal and a bare word all come
 * back as the same thing, so `Invoke-RestMethod`, `Uri` and
 * `[System.Net.Http.HttpClient]` are the colour of plain text while the
 * punctuation around them is not. The result reads as one grey block with a few
 * green strings in it.
 *
 * What this adds is the vocabulary those snippets are written in — cmdlets,
 * parameters, hashtable keys, type literals, static and instance members — and
 * nothing else. It is a tokenizer, not a parser: there is no tree here, because
 * colouring twenty lines never needed one.
 *
 * The colours follow VS Code's dark theme, which is where the comparison will
 * be drawn: a variable and a hashtable key are the same, a cmdlet reads as a
 * call, and a type reads as a type.
 */

/**
 * Everything that can end a value, and so decides what the next `[` means.
 *
 * A cmdlet is deliberately not one. What follows it is its arguments, and an
 * argument is where a type is written — `New-Object System.Net.WebClient` —
 * rather than where one is indexed.
 */
const VALUE = new Set([
  "variable",
  "constant",
  "string",
  "number",
  "member",
  "method",
  "type",
  "word",
  "close",
]);

/*
 * `-Something` is a parameter unless it is one of these, which are how
 * PowerShell spells the operators other languages give symbols to. The `c` and
 * `i` prefixes are the case-sensitive and case-insensitive forms.
 */
const OPERATOR_WORDS =
  /^-(?:[ci]?(?:eq|ne|gt|ge|lt|le|like|notlike|match|notmatch|contains|notcontains|in|notin|replace|split|join)|is|isnot|as|and|or|xor|not|band|bor|bxor|bnot|shl|shr|f)\b/i;

const KEYWORDS =
  /^(?:begin|break|catch|class|continue|data|define|do|dynamicparam|else|elseif|end|enum|exit|filter|finally|for|foreach|from|function|hidden|if|in|inlinescript|parallel|param|process|return|sequence|static|switch|throw|trap|try|until|using|var|while|workflow)\b/i;

/** `$true`, `$false` and `$null` are constants; everything else is a variable. */
const CONSTANTS = /^\$(?:true|false|null)\b/i;

/**
 * What each of those names is painted as, written as the tag paths CodeMirror
 * resolves against `@lezer/highlight`.
 *
 * Named rather than returned directly so that `read` can say what a token means
 * without also having to say what colour it is — and by a path rather than
 * through a `tokenTable`, because that table is only consulted for names
 * CodeMirror does not already know. `variable` is one it knows, and its own
 * mapping silently wins, which is how `$headers` came back uncoloured.
 *
 * The colours follow VS Code's dark theme, which is where the comparison will
 * be drawn: a variable, a key, a parameter and a member are one colour, being
 * names of things rather than things being done; a cmdlet reads as a call, and
 * a type reads as a type.
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
  cmdlet: "variableName.function",
  method: "variableName.function",
  variable: "propertyName",
  key: "propertyName",
  member: "propertyName",
  parameter: "attributeName",
  // A bare word is whatever the user meant by it, and is left alone.
  word: null,
};

interface State {
  /** Set while a string or a block comment is still open at the end of a line. */
  tokenize: ((stream: StringStream, state: State) => string) | null;
  /**
   * Whether what came last can be indexed or have a member taken from it. This
   * is what tells the `[` of `$headers['Accept']` from the `[` of `[int]`.
   */
  afterValue: boolean;
  /** Set between `.` or `::` and the name that follows it. */
  afterAccessor: boolean;
  /** What a here-string is waiting for at the start of a line: `'@` or `"@`. */
  hereStringEnd: string | null;
}

export const powershell: StreamParser<State> = {
  name: "powershell",

  startState: () => ({
    tokenize: null,
    afterValue: false,
    afterAccessor: false,
    hereStringEnd: null,
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

  languageData: { commentTokens: { line: "#", block: { open: "<#", close: "#>" } } },
};

function read(stream: StringStream, state: State, afterAccessor: boolean): string {
  // The name after a `.` or a `::`, which is a call if a bracket follows it.
  if (afterAccessor && stream.match(/^[A-Za-z_]\w*/)) {
    return stream.peek() === "(" ? "method" : "member";
  }

  if (stream.match("<#")) {
    state.tokenize = blockComment;
    return blockComment(stream, state);
  }
  if (stream.eat("#")) {
    stream.skipToEnd();
    return "comment";
  }

  /*
   * Here-strings, before anything else that starts with `@`. The opening
   * delimiter has to be the last thing on its line, and the closing one the
   * first thing on its own, which is what `hereString` waits for.
   */
  const here = stream.match(/^@(['"])/) as RegExpMatchArray | null;
  if (here) {
    state.hereStringEnd = `${here[1]}@`;
    state.tokenize = hereString;
    stream.skipToEnd();
    return "string";
  }

  if (stream.match("@{")) return "punctuation";
  // A splatted hashtable: `Invoke-RestMethod @parameters`.
  if (stream.match(/^@[A-Za-z_]\w*/)) return "variable";

  if (stream.eat("'")) {
    state.tokenize = singleQuoted;
    return singleQuoted(stream, state);
  }
  if (stream.eat('"')) {
    state.tokenize = doubleQuoted;
    return doubleQuoted(stream, state);
  }

  if (stream.match("::")) {
    state.afterAccessor = true;
    return "operator";
  }
  if (state.afterValue && stream.eat(".")) {
    state.afterAccessor = true;
    return "punctuation";
  }

  /*
   * A type literal, taken whole. It is only a type where a value could begin —
   * after a value, the same bracket is an index — which is the one piece of
   * context this needs to keep.
   */
  if (!state.afterValue && stream.match(/^\[[A-Za-z_][\w.]*(?:\[\])*\]/)) return "type";

  if (CONSTANTS.test(stream.string.slice(stream.pos))) {
    stream.match(CONSTANTS);
    return "constant";
  }
  // `$name`, `${any name}`, `$script:name`, `$_`.
  if (stream.match(/^\$(?:\{[^}]*\}|[A-Za-z_][\w:]*|[_?^$]|\d+)/)) return "variable";

  if (stream.match(OPERATOR_WORDS)) return "operator";
  // Anything else beginning with a dash names a parameter.
  if (stream.match(/^-[A-Za-z][\w-]*/)) return "parameter";

  if (stream.match(/^0[xX][\da-fA-F]+[lL]?/)) return "number";
  if (stream.match(/^\d+(?:\.\d+)?(?:[eE][-+]?\d+)?(?:[kmgtp]b)?[lLdD]?/i)) return "number";

  if (stream.match(KEYWORDS)) return "keyword";

  // `Verb-Noun`, which is what a cmdlet is called and how one is recognised.
  if (stream.match(/^[A-Za-z][\w]*-[A-Za-z][\w]*/)) return "cmdlet";

  /*
   * A dotted bare name where a value could begin is a type written without its
   * brackets — `New-Object System.Net.WebClient`. Taken whole, because the last
   * segment is the type's own name and not a call, whatever follows it.
   */
  if (!state.afterValue && stream.match(/^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+/)) return "type";

  /*
   * A bare name followed by `=` is a hashtable key. The lookahead refuses `==`
   * and the comparison forms, which are operators rather than assignment.
   */
  if (stream.match(/^[A-Za-z_]\w*(?=\s*=[^=~])/)) return "key";

  if (stream.match(/^[A-Za-z_]\w*/)) return stream.peek() === "(" ? "method" : "word";

  if (stream.match(/^[)\]}]/)) return "close";
  if (stream.match(/^[[({,;]/)) return "punctuation";
  if (stream.match(/^(?:\+=|-=|\*=|\/=|%=|\|\||&&|[=+\-*/%|&!<>])/)) return "operator";

  stream.next();
  return "word";
}

/**
 * A single-quoted string, which PowerShell lets run over as many lines as it
 * likes — a JSON body written by this app is exactly that — and in which the
 * only escape is a doubled quote.
 */
function singleQuoted(stream: StringStream, state: State): string {
  while (!stream.eol()) {
    if (stream.eat("'")) {
      if (!stream.eat("'")) {
        state.tokenize = null;
        return "string";
      }
      continue;
    }
    stream.next();
  }
  return "string";
}

/** The same, with the backtick escape and a doubled quote. */
function doubleQuoted(stream: StringStream, state: State): string {
  while (!stream.eol()) {
    if (stream.eat("`")) {
      stream.next();
      continue;
    }
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

/** Ends at a line that begins with the delimiter, and nowhere else. */
function hereString(stream: StringStream, state: State): string {
  if (state.hereStringEnd && stream.match(state.hereStringEnd)) {
    state.tokenize = null;
    state.hereStringEnd = null;
    return "string";
  }
  stream.skipToEnd();
  return "string";
}

function blockComment(stream: StringStream, state: State): string {
  while (!stream.eol()) {
    if (stream.match("#>")) {
      state.tokenize = null;
      return "comment";
    }
    stream.next();
  }
  return "comment";
}
