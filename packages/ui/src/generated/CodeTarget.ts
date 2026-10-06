// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.

/**
 * One way of writing a request out: a language, and the library it uses.
 *
 * Flat rather than a language and a library side by side, because most of
 * these are a single choice and a pair would make every one of them carry an
 * empty half. The picker groups them back together by language.
 *
 * The wire names of the first four are what they have always been, so a
 * webview and a sidecar that disagree about this list still understand each
 * other about those.
 */
export type CodeTarget = "curl" | "powershell" | "python" | "csharp" | "rust-blocking" | "rust-async" | "node-fetch" | "node-axios" | "go" | "java-httpclient" | "java-okhttp" | "php-curl" | "php-guzzle" | "zig";
