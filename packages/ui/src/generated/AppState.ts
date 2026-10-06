// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { Environment } from "./Environment";
import type { HistoryEntry } from "./HistoryEntry";
import type { SavedRequest } from "./SavedRequest";
import type { ScratchTab } from "./ScratchTab";
import type { Settings } from "./Settings";

/**
 * Everything persisted, in one file.
 */
export type AppState = { version: number, settings: Settings, savedRequests: Array<SavedRequest>, history: Array<HistoryEntry>, tabs: Array<ScratchTab>, activeTabId: string | null, environments: Array<Environment>, activeEnvironmentId: string | null, };
