import { curly, delimited, rest, type Context, type CurlyState } from "./curly";
import type { StringStream } from "@codemirror/language";

/**
 * The five languages built on `lib/curly`: what each one has that the others
 * do not, and nothing else.
 *
 * Every rule here earns its place by appearing in a generated snippet. A raw
 * string and a multiline string carry a JSON body; a lifetime, a macro and an
 * attribute are what Rust code looks like; `$name` is most of a PHP program.
 */

export const rust = curly({
  name: "rust",
  keywords:
    /^(?:as|async|await|break|const|continue|crate|dyn|else|enum|extern|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|Self|static|struct|super|trait|type|unsafe|use|where|while)\b/,
  constants: /^(?:true|false|None|Some|Ok|Err)\b/,
  builtinTypes:
    /^(?:bool|char|str|String|f32|f64|i8|i16|i32|i64|i128|isize|u8|u16|u32|u64|u128|usize|Vec|Box|Option|Result)\b/,
  accessors: /^(?:::|\.)/,
  rules(stream, state, context) {
    // `r#"..."#`, which is how a body with quotes in it is written.
    const raw = stream.match(/^r(#*)"/) as RegExpMatchArray | null;
    if (raw) return delimited(`"${raw[1]}`)(stream, state);

    // An attribute: `#[tokio::main]`. Taken whole, brackets and all.
    if (stream.match(/^#!?\[[^\]]*\]/)) return "attribute";

    // A macro, which is a name with a bang on it: `println!`.
    if (!context.accessor && stream.match(/^[A-Za-z_]\w*!/)) return "macro";

    // A lifetime, which looks like the start of a character literal and is not.
    if (stream.match(/^'[A-Za-z_]\w*(?!')/)) return "keyword";
    if (stream.match(/^'(?:\\.|[^'])'/)) return "string";

    return null;
  },
});

export const go = curly({
  name: "go",
  keywords:
    /^(?:break|case|chan|const|continue|default|defer|else|fallthrough|for|func|go|goto|if|import|interface|map|package|range|return|select|struct|switch|type|var)\b/,
  constants: /^(?:true|false|nil|iota)\b/,
  builtinTypes:
    /^(?:bool|byte|complex64|complex128|error|float32|float64|int|int8|int16|int32|int64|rune|string|uint|uint8|uint16|uint32|uint64|uintptr|any)\b/,
  rules(stream, state) {
    // A backtick string, which keeps its newlines and has no escapes.
    if (stream.eat("`")) return delimited("`")(stream, state);
    if (stream.match(/^'(?:\\.|[^'])'/)) return "string";
    return null;
  },
});

export const java = curly({
  name: "java",
  keywords:
    /^(?:abstract|assert|break|case|catch|class|const|continue|default|do|else|enum|extends|final|finally|for|goto|if|implements|import|instanceof|interface|native|new|package|private|protected|public|record|return|sealed|static|strictfp|super|switch|synchronized|this|throw|throws|transient|try|var|volatile|while|yield)\b/,
  constants: /^(?:true|false|null)\b/,
  builtinTypes: /^(?:boolean|byte|char|double|float|int|long|short|void)\b/,
  rules(stream, state) {
    /*
     * A text block, which is the one Java string that runs over lines. Matched
     * before the plain quote, or `"""` would read as an empty string followed
     * by another one.
     */
    if (stream.match('"""')) return delimited('"""')(stream, state);
    // An annotation: `@Override`.
    if (stream.match(/^@[A-Za-z_]\w*/)) return "attribute";
    if (stream.match(/^'(?:\\.|[^'])'/)) return "string";
    return null;
  },
});

export const zig = curly({
  name: "zig",
  keywords:
    /^(?:align|allowzero|and|anyframe|anytype|asm|async|await|break|callconv|catch|comptime|const|defer|else|enum|errdefer|error|export|extern|fn|for|if|inline|linksection|noalias|nosuspend|noinline|opaque|or|orelse|packed|pub|resume|return|struct|suspend|switch|test|threadlocal|try|union|unreachable|usingnamespace|var|volatile|while)\b/,
  constants: /^(?:true|false|null|undefined)\b/,
  builtinTypes:
    /^(?:anyerror|anyopaque|bool|c_int|c_long|c_short|comptime_float|comptime_int|f16|f32|f64|f80|f128|i8|i16|i32|i64|i128|isize|noreturn|type|u8|u16|u32|u64|u128|usize|void)\b/,
  rules(stream, _state, context) {
    /*
     * A multiline string, which is a run of lines each opened by `\\` and
     * closed by its own end. Every body this app writes in Zig is one.
     */
    if (stream.match("\\\\")) {
      stream.skipToEnd();
      return "string";
    }
    // A builtin: `@import`, `@intFromEnum`. Always a call.
    if (stream.match(/^@[A-Za-z_]\w*/)) return "method";
    /*
     * A name after a dot where a value could begin, which in a struct literal
     * is either a field being set or an enum value being read — `.method =
     * .POST`. The `=` is what tells those two apart, as it does in a C# object
     * initializer. After a value the same dot is a member access, and belongs
     * to the shared rules rather than here.
     */
    if (!context.afterValue && stream.match(/^\.[A-Za-z_]\w*(?![\w(])/)) {
      return stream.match(/^\s*=[^=]/, false) ? "property" : "constant";
    }
    if (stream.match(/^'(?:\\.|[^'])'/)) return "string";
    return null;
  },
});

export const php = curly({
  name: "php",
  keywords:
    /^(?:abstract|and|array|as|break|callable|case|catch|class|clone|const|continue|declare|default|do|echo|else|elseif|empty|enddeclare|endfor|endforeach|endif|endswitch|endwhile|enum|extends|final|finally|fn|for|foreach|function|global|goto|if|implements|include|include_once|instanceof|insteadof|interface|isset|list|match|namespace|new|or|print|private|protected|public|readonly|require|require_once|return|static|switch|throw|trait|try|unset|use|var|while|xor|yield)\b/,
  constants: /^(?:true|false|null|TRUE|FALSE|NULL)\b/i,
  hashComments: true,
  accessors: /^(?:->|::)/,
  rules(stream, state) {
    // The open and close tags, which are neither code nor comment.
    if (stream.match(/^<\?php\b/) || stream.match(/^\?>/)) return "keyword";

    /*
     * A single-quoted string, in which nothing is interpolated and the only
     * escapes are the quote and the backslash — and which runs over lines, as
     * every body this app writes in PHP does.
     */
    if (stream.eat("'")) return singleQuoted(stream, state);

    if (stream.match(/^\$\{?[A-Za-z_]\w*\}?/)) return "variable";
    // A constant in caps, which is how the cURL extension names every option.
    if (!state.afterAccessor && stream.match(/^[A-Z][A-Z0-9_]{2,}\b/)) return "constant";
    return null;
  },
});

function singleQuoted(stream: StringStream, state: CurlyState): string {
  state.tokenize = carrySingle;
  return carrySingle(stream, state);
}

function carrySingle(stream: StringStream, state: CurlyState): string {
  while (!stream.eol()) {
    if (stream.eat("\\")) {
      stream.next();
      continue;
    }
    if (stream.eat("'")) {
      state.tokenize = null;
      return "string";
    }
    stream.next();
  }
  return "string";
}

// Referenced so the shared helper's type is exercised by every language here.
export type { Context, CurlyState };
export { rest };
