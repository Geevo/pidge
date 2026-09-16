import type { HttpRequest, RequestPane } from "../types";
import { AuthEditor } from "./AuthEditor";
import { BodyEditor } from "./BodyEditor";
import { HeadersEditor } from "./HeadersEditor";
import { ParamsEditor } from "./ParamsEditor";

interface Props {
  request: HttpRequest;
  pane: RequestPane;
  onPaneChange: (pane: RequestPane) => void;
  onChange: (request: HttpRequest) => void;
  onSubmit: () => void;
}

export function RequestEditor({ request, pane, onPaneChange, onChange, onSubmit }: Props) {
  const activeCount = (rows: readonly { enabled: boolean; name: string }[]) =>
    rows.filter((row) => row.enabled && row.name.trim() !== "").length;

  const panes: { id: RequestPane; label: string; count?: number; marked?: boolean }[] = [
    { id: "params", label: "Params", count: activeCount(request.queryParams) },
    { id: "body", label: "Body", marked: request.body.type !== "none" },
    { id: "headers", label: "Headers", count: activeCount(request.headers) },
    { id: "auth", label: "Auth", marked: request.auth.type !== "none" },
  ];

  // The body editor needs a bounded height to virtualize, exactly as the
  // response one does; see `.ac-scroll--flush`.
  const editorOwnsScrolling =
    pane === "body" && (request.body.type === "json" || request.body.type === "text");

  return (
    <section className="ac-pane ac-pane--request" aria-label="Request">
      <div className="ac-subtabs" role="tablist" aria-label="Request sections">
        {panes.map((entry) => (
          <button
            key={entry.id}
            type="button"
            role="tab"
            className="ac-subtab"
            aria-selected={pane === entry.id}
            onClick={() => onPaneChange(entry.id)}
          >
            {entry.label}
            {entry.count ? <span className="ac-subtab__count">{entry.count}</span> : null}
            {entry.marked ? <span className="ac-subtab__count">•</span> : null}
          </button>
        ))}
      </div>

      <div className={`ac-scroll${editorOwnsScrolling ? " ac-scroll--flush" : ""}`} role="tabpanel">
        {pane === "params" ? <ParamsEditor request={request} onChange={onChange} /> : null}
        {pane === "body" ? (
          <BodyEditor request={request} onChange={onChange} onSubmit={onSubmit} />
        ) : null}
        {pane === "headers" ? <HeadersEditor request={request} onChange={onChange} /> : null}
        {pane === "auth" ? <AuthEditor request={request} onChange={onChange} /> : null}
      </div>
    </section>
  );
}
