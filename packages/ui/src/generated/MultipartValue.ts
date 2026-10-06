// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.

export type MultipartValue = { "kind": "text", value: string, } | { "kind": "file", path: string, fileName: string | null, contentType: string | null, };
