import { act, render, screen } from "@testing-library/react";
import { EditorView } from "@codemirror/view";
import { describe, expect, it, vi } from "vitest";

import { blankRequest } from "../state/factories";
import type { HttpRequest } from "../types";
import { CurlImportView } from "./CurlImportView";

/** What a paste does to the editor: one change replacing everything. */
function paste(text: string) {
  const content = screen.getByRole("textbox", { name: "curl command" });
  const view = EditorView.findFromDOM(content)!;
  act(() => {
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text } });
  });
}

describe("the cURL pane", () => {
  it("fills the request from a pasted command and keeps the tab's id", () => {
    const request = blankRequest();
    const onChange = vi.fn<(request: HttpRequest) => void>();
    render(<CurlImportView request={request} onChange={onChange} onSubmit={() => {}} />);

    paste(`curl -X POST 'https://e.com/a?x=1' -H 'Authorization: Bearer t' -d '{"a":1}'`);

    expect(onChange).toHaveBeenCalledTimes(1);
    const imported = onChange.mock.calls[0]![0];
    expect(imported.id).toBe(request.id);
    expect(imported.method).toBe("POST");
    expect(imported.queryParams.map((row) => [row.name, row.value])).toEqual([["x", "1"]]);
    expect(imported.auth).toEqual({ type: "bearer", token: "t" });
    expect(imported.body).toEqual({ type: "json", text: '{"a":1}' });
    expect(screen.getByRole("status")).toHaveTextContent("Imported");
  });

  it("leaves the request alone and says why when the command will not read", () => {
    const onChange = vi.fn();
    render(<CurlImportView request={blankRequest()} onChange={onChange} onSubmit={() => {}} />);

    paste("curl -X POST");

    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole("status")).toHaveTextContent("There is no URL in this command.");
  });
});
