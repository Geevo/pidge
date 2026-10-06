// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { HistoryEntry } from "./HistoryEntry";
import type { HttpResponse } from "./HttpResponse";
import type { RequestError } from "./RequestError";

/**
 * What a send produced: a response or an error, plus the history row it
 * created. Returning the row lets a frontend keep its history list live
 * without re-fetching the whole state.
 */
export type SendOutcome = { response: HttpResponse | null, error: RequestError | null, 
/**
 * `null` for a cancelled request, which is never recorded.
 */
historyEntry: HistoryEntry | null, };
