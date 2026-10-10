import type {
  AuthConfig,
  HttpMethod,
  HttpRequest,
  KeyValueEntry,
  MultipartEntry,
  RequestBody,
} from "../types";
import { HTTP_METHODS } from "../types";
import { newId } from "./ids";
import { baseMimeType, isJsonMime } from "./mime";
import { parseQueryParams } from "./url";

/**
 * A curl command read back into a request: the other direction from the Code
 * pane's curl target.
 *
 * Written for the commands people actually paste — a browser's "Copy as cURL",
 * an API's documentation, this app's own snippets — rather than for every
 * option curl has. What it does not understand it says so about, rather than
 * guessing at whether the option took an argument and losing the URL to it.
 */

export type CurlImport =
  | { readonly ok: true; readonly request: HttpRequest; readonly notes: readonly string[] }
  | { readonly ok: false; readonly message: string };

/** A list of options written out as one string, which is easier to scan. */
function optionSet(list: string): ReadonlySet<string> {
  return new Set(list.trim().split(/\s+/));
}

/**
 * Every option that takes an argument, long and short, including the ones that
 * are then ignored: knowing that `--output` takes one is what stops the file
 * name being read as the URL.
 */
const TAKES_VALUE = optionSet(`
  -X --request -H --header -d --data --data-ascii --data-binary --data-raw
  --data-urlencode --json -F --form --form-string -u --user --oauth2-bearer --url
  -A --user-agent -e --referer -b --cookie -m --max-time -o --output -c
  --cookie-jar -x --proxy -U --proxy-user -w --write-out -E --cert --cert-type
  --key --key-type --pass --cacert --capath --connect-timeout --retry --retry-delay
  --retry-max-time --max-redirs --resolve --connect-to --interface --limit-rate -r
  --range -T --upload-file -K --config -z --time-cond --aws-sigv4 --trace
  --trace-ascii --stderr -D --dump-header -y --speed-time -Y --speed-limit
  --expect100-timeout --keepalive-time --dns-servers --unix-socket
  --abstract-unix-socket --ciphers --tls-max --pinnedpubkey
`);

/** Long and short spellings of the same option, so the parser handles one. */
const ALIASES: Record<string, string> = {
  "-X": "--request",
  "-H": "--header",
  "-d": "--data",
  "-F": "--form",
  "-u": "--user",
  "-A": "--user-agent",
  "-e": "--referer",
  "-b": "--cookie",
  "-m": "--max-time",
  "-G": "--get",
  "-I": "--head",
  "-L": "--location",
  "-k": "--insecure",
};

/**
 * Options that change nothing a request here can hold, or that the app does
 * anyway, and so pass without comment. Anything else unrecognized is noted.
 */
const SILENT = optionSet(`
  --location --insecure --compressed -s --silent -S --show-error -v --verbose -i
  --include -f --fail --fail-with-body -# --progress-bar --http1.0 --http1.1
  --http2 --http2-prior-knowledge --http3 -N --no-buffer --globoff -g -o --output
  -w --write-out --connect-timeout --max-redirs --retry --retry-delay
  --retry-max-time -D --dump-header --no-progress-meter -O --remote-name -J
  --remote-header-name --path-as-is --tr-encoding --raw
`);

/** Splits shell text into words the way a POSIX shell would, before expansion. */
export function shellWords(text: string): string[] {
  const words: string[] = [];
  let word = "";
  // Whether a word has started, which an empty `''` does with no characters.
  let started = false;
  let i = 0;

  const end = () => {
    if (started) words.push(word);
    word = "";
    started = false;
  };

  while (i < text.length) {
    const ch = text[i]!;

    if (ch === "\\") {
      const next = text[i + 1];
      // A backslash before a newline joins the lines; it is not a character.
      if (next === "\n") {
        i += 2;
        continue;
      }
      if (next === "\r" && text[i + 2] === "\n") {
        i += 3;
        continue;
      }
      if (next !== undefined) {
        word += next;
        started = true;
      }
      i += 2;
      continue;
    }

    if (ch === "'") {
      const close = text.indexOf("'", i + 1);
      if (close === -1) throw new Error("A single-quoted string is never closed.");
      word += text.slice(i + 1, close);
      started = true;
      i = close + 1;
      continue;
    }

    // `$'…'` is how a browser quotes a body with a newline or a quote in it.
    if (ch === "$" && text[i + 1] === "'") {
      const [value, next] = ansiQuoted(text, i + 2);
      word += value;
      started = true;
      i = next;
      continue;
    }

    if (ch === '"') {
      i += 1;
      let closed = false;
      while (i < text.length) {
        const inner = text[i]!;
        if (inner === '"') {
          closed = true;
          i += 1;
          break;
        }
        // Inside double quotes a backslash only escapes these; elsewhere it is
        // itself, which is what leaves `"C:\temp"` alone.
        if (inner === "\\" && i + 1 < text.length && '"\\$`\n'.includes(text[i + 1]!)) {
          if (text[i + 1] !== "\n") word += text[i + 1];
          i += 2;
          continue;
        }
        word += inner;
        i += 1;
      }
      if (!closed) throw new Error("A double-quoted string is never closed.");
      started = true;
      continue;
    }

    if (ch === " " || ch === "\t" || ch === "\n" || ch === "\r") {
      end();
      i += 1;
      continue;
    }

    word += ch;
    started = true;
    i += 1;
  }

  end();
  return words;
}

/** The inside of a `$'…'` string, from just after the quote. */
function ansiQuoted(text: string, start: number): [string, number] {
  let value = "";
  let i = start;
  const simple: Record<string, string> = {
    n: "\n",
    t: "\t",
    r: "\r",
    a: "\x07",
    b: "\b",
    e: "\x1b",
    E: "\x1b",
    f: "\f",
    v: "\v",
    "\\": "\\",
    "'": "'",
    '"': '"',
    "?": "?",
  };

  while (i < text.length) {
    const ch = text[i]!;
    if (ch === "'") return [value, i + 1];
    if (ch !== "\\") {
      value += ch;
      i += 1;
      continue;
    }

    const next = text[i + 1] ?? "";
    if (next in simple) {
      value += simple[next];
      i += 2;
      continue;
    }

    const hex = /^(?:x([0-9a-fA-F]{1,2})|u([0-9a-fA-F]{1,4})|U([0-9a-fA-F]{1,8}))/.exec(
      text.slice(i + 1),
    );
    if (hex) {
      value += String.fromCodePoint(parseInt(hex[1] ?? hex[2] ?? hex[3]!, 16));
      i += 1 + hex[0].length;
      continue;
    }

    const octal = /^[0-7]{1,3}/.exec(text.slice(i + 1));
    if (octal) {
      value += String.fromCharCode(parseInt(octal[0], 8));
      i += 1 + octal[0].length;
      continue;
    }

    value += "\\" + next;
    i += 2;
  }

  throw new Error("A $'…' string is never closed.");
}

interface Parsed {
  method: string | null;
  urls: string[];
  headers: [string, string][];
  /** Each `--data*` argument, already in the form it is sent in. */
  data: string[];
  json: boolean;
  forms: { name: string; value: string; literal: boolean }[];
  user: string | null;
  bearer: string | null;
  digest: boolean;
  ntlm: boolean;
  get: boolean;
  head: boolean;
  timeoutMs: number | null;
  notes: string[];
}

/**
 * Reads a pasted curl command into a request. Fails only where there is no
 * request to be had: text that does not parse as shell, or no URL in it.
 */
export function importCurl(text: string): CurlImport {
  let words: string[];
  try {
    words = shellWords(text.trim());
  } catch (error) {
    return { ok: false, message: (error as Error).message };
  }

  // The prompt a command was copied with, and `curl` itself, are optional.
  if (words[0] === "$") words.shift();
  if (words[0] && /(?:^|[\\/])curl(?:\.exe)?$/i.test(words[0])) words.shift();

  const parsed = parseArguments(words);
  if (typeof parsed === "string") return { ok: false, message: parsed };

  const url = parsed.urls[0];
  if (url === undefined) return { ok: false, message: "There is no URL in this command." };
  if (parsed.urls.length > 1) {
    parsed.notes.push(`Only the first of ${parsed.urls.length} URLs was imported.`);
  }

  return { ok: true, request: buildRequest(parsed, url), notes: parsed.notes };
}

function parseArguments(words: readonly string[]): Parsed | string {
  const parsed: Parsed = {
    method: null,
    urls: [],
    headers: [],
    data: [],
    json: false,
    forms: [],
    user: null,
    bearer: null,
    digest: false,
    ntlm: false,
    get: false,
    head: false,
    timeoutMs: null,
    notes: [],
  };
  const unknown = new Set<string>();
  let optionsEnded = false;

  for (let i = 0; i < words.length; i += 1) {
    const word = words[i]!;

    if (optionsEnded || !word.startsWith("-") || word === "-") {
      parsed.urls.push(word);
      continue;
    }
    if (word === "--") {
      optionsEnded = true;
      continue;
    }

    // `-sSL`, `-XPOST` and `-H'Accept: */*'` are each several options, or one
    // with its value attached, written as one word.
    const options: [string, string | null][] = [];
    if (word.startsWith("--")) {
      options.push([word, null]);
    } else {
      for (let j = 1; j < word.length; j += 1) {
        const flag = `-${word[j]}`;
        if (TAKES_VALUE.has(flag) && j + 1 < word.length) {
          options.push([flag, word.slice(j + 1)]);
          break;
        }
        options.push([flag, null]);
      }
    }

    for (const [flag, attached] of options) {
      let value = attached;
      if (value === null && TAKES_VALUE.has(flag)) {
        const next = words[i + 1];
        if (next === undefined) return `${flag} needs a value after it.`;
        value = next;
        i += 1;
      }
      if (!apply(parsed, ALIASES[flag] ?? flag, value ?? "")) unknown.add(flag);
    }
  }

  if (unknown.size > 0) {
    parsed.notes.push(`Ignored: ${[...unknown].join(", ")}.`);
  }
  return parsed;
}

/** Applies one option. False for one this does not know, which is then noted. */
function apply(parsed: Parsed, option: string, value: string): boolean {
  switch (option) {
    case "--request":
      parsed.method = value;
      return true;
    case "--url":
      parsed.urls.push(value);
      return true;
    case "--header": {
      if (value.startsWith("@")) {
        parsed.notes.push(`Headers read from a file (${value}) were not imported.`);
        return true;
      }
      const colon = value.indexOf(":");
      // `Name;` is curl's way of sending a header with no value.
      if (colon === -1) {
        if (value.endsWith(";")) parsed.headers.push([value.slice(0, -1).trim(), ""]);
        return true;
      }
      const name = value.slice(0, colon).trim();
      const headerValue = value.slice(colon + 1).trim();
      // `Name:` with nothing after removes one of curl's own; there is none here.
      if (headerValue !== "") parsed.headers.push([name, headerValue]);
      return true;
    }
    case "--data":
    case "--data-ascii":
    case "--data-binary":
      if (value.startsWith("@")) {
        parsed.notes.push(`A body read from a file (${value}) was not imported.`);
        return true;
      }
      parsed.data.push(value);
      return true;
    case "--data-raw":
      parsed.data.push(value);
      return true;
    case "--data-urlencode":
      parsed.data.push(urlEncodedArgument(value));
      return true;
    case "--json":
      parsed.json = true;
      parsed.data.push(value);
      return true;
    case "--form":
    case "--form-string": {
      const equals = value.indexOf("=");
      const name = equals === -1 ? value : value.slice(0, equals);
      parsed.forms.push({
        name,
        value: equals === -1 ? "" : value.slice(equals + 1),
        literal: option === "--form-string",
      });
      return true;
    }
    case "--user":
      parsed.user = value;
      return true;
    case "--oauth2-bearer":
      parsed.bearer = value;
      return true;
    case "--digest":
      parsed.digest = true;
      return true;
    case "--ntlm":
      parsed.ntlm = true;
      return true;
    case "--basic":
      return true;
    case "--get":
      parsed.get = true;
      return true;
    case "--head":
      parsed.head = true;
      return true;
    case "--user-agent":
      parsed.headers.push(["User-Agent", value]);
      return true;
    case "--referer":
      parsed.headers.push(["Referer", value]);
      return true;
    case "--cookie":
      // Without an `=` it is the name of a cookie file rather than cookies.
      if (value.includes("=")) parsed.headers.push(["Cookie", value]);
      else parsed.notes.push(`Cookies read from a file (${value}) were not imported.`);
      return true;
    case "--max-time": {
      const seconds = Number(value);
      if (Number.isFinite(seconds) && seconds > 0) parsed.timeoutMs = Math.round(seconds * 1000);
      return true;
    }
    default:
      return SILENT.has(option);
  }
}

/** One `--data-urlencode` argument, encoded the way curl encodes it. */
function urlEncodedArgument(value: string): string {
  const encode = (text: string) => encodeURIComponent(text).replace(/%20/g, "+");
  // `=content` encodes all of it; `name=content` encodes only the content.
  const equals = value.indexOf("=");
  if (equals === -1) return encode(value);
  if (equals === 0) return encode(value.slice(1));
  return `${value.slice(0, equals)}=${encode(value.slice(equals + 1))}`;
}

function buildRequest(parsed: Parsed, rawUrl: string): HttpRequest {
  let url = rawUrl;
  let headers = [...parsed.headers];
  const data = parsed.data.join("&");
  const hasData = parsed.data.length > 0;

  // `--get` sends the data in the query string instead of the body.
  if (parsed.get && hasData) {
    url += (url.includes("?") ? "&" : "?") + data;
  }

  const method = methodOf(parsed, hasData && !parsed.get, parsed.forms.length > 0);
  const contentType = findHeader(headers, "content-type");

  let body: RequestBody = { type: "none" };
  let dropContentType = false;

  if (parsed.forms.length > 0) {
    body = { type: "multipart", entries: parsed.forms.map(multipartEntry) };
    // The boundary is the app's to choose, so the header can only be wrong.
    dropContentType = true;
    if (hasData)
      parsed.notes.push("The command has both --form and --data; the --data was dropped.");
  } else if (hasData && !parsed.get) {
    const declared = parsed.json ? "application/json" : contentType;
    const looksJson = /^\s*[[{]/.test(data) && isJson(data);
    const mime = declared ? baseMimeType(declared) : null;

    if (mime === "application/json" || (declared === null && looksJson)) {
      body = { type: "json", text: data };
      dropContentType = true;
    } else if (declared !== null && isJsonMime(declared)) {
      // A JSON type with a name of its own, such as `application/vnd.api+json`,
      // is the JSON editor with the header kept.
      body = { type: "json", text: data };
    } else if (
      (mime === null || mime === "application/x-www-form-urlencoded") &&
      isFormEncoded(data)
    ) {
      body = { type: "urlEncoded", entries: formEntries(data) };
      dropContentType = true;
    } else {
      body = { type: "text", text: data, contentType: declared };
      dropContentType = true;
    }
  }

  if (dropContentType) {
    headers = headers.filter(([name]) => name.toLowerCase() !== "content-type");
  }
  // `--json` asks for JSON back as well as sending it.
  if (parsed.json && findHeader(headers, "accept") === null) {
    headers.push(["Accept", "application/json"]);
  }

  const [auth, remaining] = authOf(parsed, headers);
  headers = remaining;

  return {
    id: newId(),
    method,
    url,
    queryParams: parseQueryParams(url),
    headers: headers.map(([name, value]) => row(name, value)),
    auth,
    body,
    timeoutMs: parsed.timeoutMs,
    encodeQuery: true,
  };
}

function methodOf(parsed: Parsed, sendsData: boolean, sendsForm: boolean): HttpMethod {
  if (parsed.method !== null) {
    const upper = parsed.method.toUpperCase();
    if ((HTTP_METHODS as readonly string[]).includes(upper)) return upper as HttpMethod;
    parsed.notes.push(`${parsed.method} is not a method this app can send; it was left as GET.`);
    return "GET";
  }
  if (parsed.head) return "HEAD";
  return sendsData || sendsForm ? "POST" : "GET";
}

/**
 * The auth helper the command implies, and the headers left once anything it
 * took over is gone. A `--user` wins over an `Authorization` header, as it does
 * in curl.
 */
function authOf(parsed: Parsed, headers: [string, string][]): [AuthConfig, [string, string][]] {
  if (parsed.user !== null) {
    const colon = parsed.user.indexOf(":");
    const username = colon === -1 ? parsed.user : parsed.user.slice(0, colon);
    const password = colon === -1 ? "" : parsed.user.slice(colon + 1);
    const rest = withoutHeader(headers, "authorization");

    if (parsed.ntlm) {
      const slash = username.indexOf("\\");
      return [
        {
          type: "ntlm",
          username: slash === -1 ? username : username.slice(slash + 1),
          password,
          domain: slash === -1 ? "" : username.slice(0, slash),
          workstation: "",
        },
        rest,
      ];
    }
    if (parsed.digest) return [{ type: "digest", username, password }, rest];
    return [{ type: "basic", username, password }, rest];
  }

  if (parsed.bearer !== null) {
    return [{ type: "bearer", token: parsed.bearer }, withoutHeader(headers, "authorization")];
  }

  const authorization = findHeader(headers, "authorization");
  if (authorization !== null) {
    const bearer = /^Bearer\s+(\S.*)$/i.exec(authorization);
    if (bearer) {
      return [
        { type: "bearer", token: bearer[1]!.trim() },
        withoutHeader(headers, "authorization"),
      ];
    }

    const basic = /^Basic\s+([A-Za-z0-9+/=]+)$/i.exec(authorization);
    const decoded = basic ? decodeBasic(basic[1]!) : null;
    if (decoded) {
      return [{ type: "basic", ...decoded }, withoutHeader(headers, "authorization")];
    }
  }

  return [{ type: "none" }, headers];
}

/** `user:password` out of a Basic credential, or null if it is not one. */
function decodeBasic(encoded: string): { username: string; password: string } | null {
  try {
    const bytes = Uint8Array.from(atob(encoded), (ch) => ch.charCodeAt(0));
    const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    const colon = text.indexOf(":");
    if (colon === -1) return null;
    return { username: text.slice(0, colon), password: text.slice(colon + 1) };
  } catch {
    return null;
  }
}

function multipartEntry(form: Parsed["forms"][number]): MultipartEntry {
  // `@path` attaches a file and `<path` sends its contents as a field; both
  // are a file here. `--form-string` takes neither.
  if (!form.literal && (form.value.startsWith("@") || form.value.startsWith("<"))) {
    const [path, ...modifiers] = form.value.slice(1).split(";");
    let contentType: string | null = null;
    let fileName: string | null = null;
    for (const modifier of modifiers) {
      const equals = modifier.indexOf("=");
      if (equals === -1) continue;
      const key = modifier.slice(0, equals).trim().toLowerCase();
      const value = modifier
        .slice(equals + 1)
        .trim()
        .replace(/^"(.*)"$/, "$1");
      if (key === "type") contentType = value;
      if (key === "filename") fileName = value;
    }
    return {
      id: newId(),
      enabled: true,
      name: form.name,
      value: { kind: "file", path: path!, fileName, contentType },
    };
  }

  // A text field can carry `;type=` too, which a text part here cannot.
  const value = form.literal ? form.value : form.value.replace(/;type=[^;]*$/, "");
  return { id: newId(), enabled: true, name: form.name, value: { kind: "text", value } };
}

/** `a=1&b=2`, as a form body would have it. */
function isFormEncoded(data: string): boolean {
  if (data === "" || /\s/.test(data.trim())) return false;
  return data.split("&").every((pair) => /^[^=&]+=[^&]*$/.test(pair));
}

function formEntries(data: string): KeyValueEntry[] {
  return parseQueryParams(`?${data}`);
}

function isJson(text: string): boolean {
  try {
    JSON.parse(text);
    return true;
  } catch {
    return false;
  }
}

function findHeader(headers: readonly [string, string][], name: string): string | null {
  const found = headers.find(([candidate]) => candidate.toLowerCase() === name);
  return found ? found[1] : null;
}

function withoutHeader(headers: readonly [string, string][], name: string): [string, string][] {
  return headers.filter(([candidate]) => candidate.toLowerCase() !== name);
}

function row(name: string, value: string): KeyValueEntry {
  return { id: newId(), enabled: true, name, value };
}
