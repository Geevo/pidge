import type { StreamParser, StringStream } from "@codemirror/language";

/**
 * Shell, tokenized here rather than taken from `@codemirror/legacy-modes`.
 *
 * That mode colours the flags and the quoted strings and leaves the rest: the
 * command itself, every unquoted argument, and the backslashes that hold a
 * curl command together across a dozen lines.
 *
 * It also splits `'\''` into three pieces of three different colours, which is
 * strictly what the shell does with it and is not what anybody reading it wants
 * to see. That sequence is how a quote is put inside a quoted string — close,
 * escape, reopen — and every apostrophe in a generated body is one, so it is
 * painted as the string it is part of.
 */

/** Everything a value can be, which is what makes the next word an argument. */
const VALUE = new Set(["string", "word", "variable", "number", "escape"]);

/**
 * What each name is painted as, as the tag paths CodeMirror resolves against
 * `@lezer/highlight`. See `lib/powershell` for why not a `tokenTable`.
 *
 * An unquoted argument gets the string colour because that is what it is: in a
 * shell, a bare word is a string literal, and `POST` in `--request POST` is the
 * value of the flag before it exactly as a quoted one would be.
 */
const TAGS: Record<string, string | null> = {
  comment: "comment",
  string: "string",
  escape: "string",
  word: "string",
  number: "number",
  keyword: "keyword",
  operator: "operator",
  punctuation: "punctuation",
  flag: "attributeName",
  variable: "propertyName",
  command: "variableName.function",
};

const KEYWORDS =
  /^(?:if|then|elif|else|fi|for|while|until|do|done|case|esac|in|function|select|time|return|export|local|readonly|declare|set|unset|shift|source|exit)\b/;

interface State {
  /** Set while a quoted string is still open at the end of a line. */
  tokenize: ((stream: StringStream, state: State) => string) | null;
  /**
   * Whether the next word is the command being run rather than an argument to
   * one. True at the start, and again after anything that ends a command.
   */
  expectCommand: boolean;
  /** Set when a line ended with a backslash, joining it to the next. */
  continued: boolean;
}

export const shell: StreamParser<State> = {
  name: "shell",

  startState: () => ({ tokenize: null, expectCommand: true, continued: false }),

  token(stream, state) {
    // A new line is a new command, unless a backslash joined it to the last.
    if (stream.sol()) {
      if (!state.continued) state.expectCommand = true;
      state.continued = false;
    }

    if (state.tokenize) return state.tokenize(stream, state);
    if (stream.eatSpace()) return null;

    const expectCommand = state.expectCommand;
    const token = read(stream, state, expectCommand);

    // A command is the first word of one; everything after it is an argument,
    // until something ends the command and the next word starts a new one.
    if (VALUE.has(token) || token === "command" || token === "flag") state.expectCommand = false;

    return TAGS[token] ?? null;
  },

  languageData: { commentTokens: { line: "#" } },
};

function read(stream: StringStream, state: State, expectCommand: boolean): string {
  if (stream.eat("#")) {
    stream.skipToEnd();
    return "comment";
  }

  if (stream.eat("'")) {
    state.tokenize = singleQuoted;
    return singleQuoted(stream, state);
  }
  if (stream.eat('"')) {
    state.tokenize = doubleQuoted;
    return doubleQuoted(stream, state);
  }

  /*
   * A backslash at the end of a line joins it to the next, which is what every
   * curl command here is built out of. Anywhere else it escapes one character —
   * including the `\'` in the middle of `'\''`.
   */
  if (stream.eat("\\")) {
    if (stream.eol()) {
      state.continued = true;
      return "operator";
    }
    stream.next();
    return "escape";
  }

  if (stream.match(/^\$(?:\{[^}]*\}|[A-Za-z_]\w*|[@*#?$!0-9-])/)) return "variable";

  // A flag, which is the only thing a leading dash means here.
  if (stream.match(/^--?[A-Za-z][\w-]*/)) return "flag";

  if (stream.match(/^\d+(?:\.\d+)?\b/)) return "number";

  // Anything that ends a command, so the next word starts a new one.
  if (stream.match(/^(?:&&|\|\||[|;&])/)) {
    state.expectCommand = true;
    return "operator";
  }
  if (stream.match(/^(?:>>|<<|[<>=])/)) return "operator";
  if (stream.match(/^[(){}[\]]/)) {
    state.expectCommand = true;
    return "punctuation";
  }

  if (KEYWORDS.test(stream.string.slice(stream.pos))) {
    stream.match(KEYWORDS);
    state.expectCommand = true;
    return "keyword";
  }

  /*
   * A bare word: the command if one is expected, an argument otherwise. It runs
   * to whitespace or to anything with its own meaning, so that `photo=@/path`
   * is one word and `a;b` is two commands.
   */
  if (stream.match(/^[^\s'"\\$;&|<>(){}[\]#]+/)) return expectCommand ? "command" : "word";

  stream.next();
  return "word";
}

/**
 * `'...'`, in which there are no escapes at all: the string ends at the very
 * next quote. It may run over as many lines as it likes, which is what a JSON
 * body passed to `--data-raw` does.
 */
function singleQuoted(stream: StringStream, state: State): string {
  while (!stream.eol()) {
    if (stream.eat("'")) {
      state.tokenize = null;
      return "string";
    }
    stream.next();
  }
  return "string";
}

/** `"..."`, in which a backslash escapes the character after it. */
function doubleQuoted(stream: StringStream, state: State): string {
  while (!stream.eol()) {
    if (stream.eat("\\")) {
      stream.next();
      continue;
    }
    if (stream.eat('"')) {
      state.tokenize = null;
      return "string";
    }
    stream.next();
  }
  return "string";
}
