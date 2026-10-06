// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { KeyValueEntry } from "./KeyValueEntry";

/**
 * A named set of variables. Environments are flat and have no relationship to
 * workspaces, projects, or anything else.
 */
export type Environment = { id: string, name: string, variables: Array<KeyValueEntry>, };
