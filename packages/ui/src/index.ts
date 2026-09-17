/**
 * What a host needs, and nothing else.
 *
 * Both frontends mount `App` and hand it a bridge; every component, reducer and
 * helper inside is an implementation detail of that. Exporting them as well
 * would invite a host to reach past the seam and grow a difference between the
 * two platforms, which is the one thing this layout exists to prevent.
 */
export { App } from "./components/App";

export type {
  FilePickFilter,
  FilePickRequest,
  HostCommand,
  LoadedState,
  PlatformBridge,
  ResizeEdge,
  SaveRequestInput,
  SendOutcome,
  WindowControls,
} from "./bridge";

export * from "./types";
