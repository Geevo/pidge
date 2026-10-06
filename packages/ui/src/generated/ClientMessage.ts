// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { AppState } from "./AppState";
import type { CodeTarget } from "./CodeTarget";
import type { ExportFormat } from "./ExportFormat";
import type { HttpRequest } from "./HttpRequest";

export type ClientMessage = { "type": "handshake", clientName: string, clientVersion: string, } | { "type": "sendRequest", request: HttpRequest, 
/**
 * Already-flattened environment variables for this send.
 */
variables: { [key in string]: string }, } | { "type": "cancelRequest", requestId: string, } | { "type": "generateCode", request: HttpRequest, target: CodeTarget, variables: { [key in string]: string }, } | { "type": "loadState" } | { "type": "saveState", state: AppState, } | { "type": "saveRequest", savedRequestId: string | null, name: string, request: HttpRequest, } | { "type": "deleteSavedRequest", savedRequestId: string, } | { "type": "clearHistory" } | { "type": "deleteHistoryEntry", historyEntryId: string, } | { "type": "exportSavedRequests", savedRequestIds: Array<string>, format: ExportFormat, includeSecrets: boolean, } | { "type": "importSavedRequests", contents: string, } | { "type": "shutdown" };
