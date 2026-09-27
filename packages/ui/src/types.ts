/**
 * The wire types are generated from the Rust crates by `pnpm gen:types`
 * (ts-rs). Nothing in `generated/` should be edited by hand: change the Rust
 * struct and regenerate, so the two sides cannot drift.
 */
export type { AppState } from "./generated/AppState";
export type { ApiKeyPlacement } from "./generated/ApiKeyPlacement";
export type { AuthConfig } from "./generated/AuthConfig";
export type { ClientEnvelope } from "./generated/ClientEnvelope";
export type { ClientMessage } from "./generated/ClientMessage";
export type { CodeTarget } from "./generated/CodeTarget";
export type { Environment } from "./generated/Environment";
export type { ExportFormat } from "./generated/ExportFormat";
export type { HistoryEntry } from "./generated/HistoryEntry";
export type { HttpMethod } from "./generated/HttpMethod";
export type { HttpRequest } from "./generated/HttpRequest";
export type { HttpResponse } from "./generated/HttpResponse";
export type { KeyValueEntry } from "./generated/KeyValueEntry";
export type { MultipartEntry } from "./generated/MultipartEntry";
export type { PaneLayout } from "./generated/PaneLayout";
export type { MultipartValue } from "./generated/MultipartValue";
export type { OAuth1Settings } from "./generated/OAuth1Settings";
export type { OAuth1Signature } from "./generated/OAuth1Signature";
export type { OAuth2ClientAuth } from "./generated/OAuth2ClientAuth";
export type { OAuth2Grant } from "./generated/OAuth2Grant";
export type { OAuth2Settings } from "./generated/OAuth2Settings";
export type { RequestBody } from "./generated/RequestBody";
export type { RequestError } from "./generated/RequestError";
export type { RequestErrorKind } from "./generated/RequestErrorKind";
export type { SavedRequest } from "./generated/SavedRequest";
export type { ScratchTab } from "./generated/ScratchTab";
export type { ServerEnvelope } from "./generated/ServerEnvelope";
export type { ServerMessage } from "./generated/ServerMessage";
export type { Settings } from "./generated/Settings";
export type { SyntaxTheme } from "./generated/SyntaxTheme";
export type { ClientIdentitySettings } from "./generated/ClientIdentitySettings";
export type { Theme } from "./generated/Theme";
export type { PeerCertificate } from "./generated/PeerCertificate";
export type { TlsDetails } from "./generated/TlsDetails";
export type { TlsSettings } from "./generated/TlsSettings";

import type { HttpMethod } from "./generated/HttpMethod";
import type { HttpResponse } from "./generated/HttpResponse";
import type { RequestError } from "./generated/RequestError";

export const HTTP_METHODS: readonly HttpMethod[] = [
  "GET",
  "POST",
  "PUT",
  "PATCH",
  "DELETE",
  "HEAD",
  "OPTIONS",
];

/** Which tab of the request editor is showing. */
export type RequestPane = "params" | "body" | "headers" | "auth" | "code";

/** Which tab of the response pane is showing. */
export type ResponsePane = "body" | "headers";

/** What a tab is currently doing. `undefined` means it has never been sent. */
export type TabStatus =
  | { readonly state: "idle" }
  | { readonly state: "sending" }
  | { readonly state: "done"; readonly response: HttpResponse }
  | { readonly state: "failed"; readonly error: RequestError };
