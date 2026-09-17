import type { TabStatus } from "../types";
import { errorTitle } from "../lib/errors";
import { StatusSummary } from "./StatusSummary";

/**
 * The strip along the bottom of the window.
 *
 * The result of a send belongs at the edge of the frame rather than above the
 * body: it is a fact about the request, not a heading for the text underneath,
 * and putting it here gives the body its full height back.
 *
 * The bar is always present, so nothing shifts when a response arrives.
 */
export function StatusBar({ status }: { status: TabStatus }) {
  return (
    <footer className="ac-statusbar" aria-label="Request status">
      {status.state === "done" ? <StatusSummary response={status.response} /> : null}
      {status.state === "sending" ? <span className="ac-statusbar__note">Sending…</span> : null}
      {status.state === "failed" ? (
        <span className="ac-statusbar__note ac-statusbar__note--bad">
          {errorTitle(status.error)}
        </span>
      ) : null}
    </footer>
  );
}
