import type { CodeTarget, HttpRequest, RequestPane } from "../types";
import { AuthEditor } from "./AuthEditor";
import { BodyEditor } from "./BodyEditor";
import { CodeView } from "./CodeView";
import { CurlImportView } from "./CurlImportView";
import { HeadersEditor } from "./HeadersEditor";
import { ParamsEditor } from "./ParamsEditor";

interface Props {
  request: HttpRequest;
  pane: RequestPane;
  codeTarget: CodeTarget;
  onPaneChange: (pane: RequestPane) => void;
  onCodeTargetChange: (target: CodeTarget) => void;
  onChange: (request: HttpRequest) => void;
  onSubmit: () => void;
  generateCode: (request: HttpRequest, target: CodeTarget) => Promise<string>;
}

export function RequestEditor({
  request,
  pane,
  codeTarget,
  onPaneChange,
  onCodeTargetChange,
  onChange,
  onSubmit,
  generateCode,
}: Props) {
  const activeCount = (rows: readonly { enabled: boolean; name: string }[]) =>
    rows.filter((row) => row.enabled && row.name.trim() !== "").length;

  const panes: { id: RequestPane; label: string; count?: number; marked?: boolean }[] = [
    { id: "params", label: "Params", count: activeCount(request.queryParams) },
    { id: "body", label: "Body", marked: request.body.type !== "none" },
    { id: "headers", label: "Headers", count: activeCount(request.headers) },
    { id: "auth", label: "Auth", marked: request.auth.type !== "none" },
    // Last, and never marked: it is a view of the other four rather than a
    // fifth thing to fill in.
    { id: "code", label: "Code" },
    // The way back from the Code pane: a command pasted in fills the others.
    { id: "curl", label: "cURL" },
  ];

  /*
   * The body editor needs a bounded height to virtualize, exactly as the
   * response one does; see `.ac-scroll--flush`.
   *
   * The Code pane takes the same treatment for a different reason: its toolbar
   * carries the language and the Copy button, and if the panel scrolled they
   * would scroll away from the snippet they belong to. The cURL pane is laid
   * out the same way, with what the paste did in place of the toolbar.
   */
  const editorOwnsScrolling =
    pane === "code" ||
    pane === "curl" ||
    (pane === "body" && (request.body.type === "json" || request.body.type === "text"));

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
        {pane === "code" ? (
          <CodeView
            request={request}
            target={codeTarget}
            onTargetChange={onCodeTargetChange}
            generate={generateCode}
          />
        ) : null}
        {pane === "curl" ? (
          <CurlImportView request={request} onChange={onChange} onSubmit={onSubmit} />
        ) : null}
      </div>
    </section>
  );
}
