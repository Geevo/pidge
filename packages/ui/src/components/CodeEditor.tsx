import { useEffect, useRef } from "react";
import { defaultKeymap, history, historyKeymap } from "@codemirror/commands";
import { json } from "@codemirror/lang-json";
import {
  HighlightStyle,
  bracketMatching,
  indentOnInput,
  syntaxHighlighting,
} from "@codemirror/language";
import { Compartment, EditorState } from "@codemirror/state";
import { EditorView, keymap, lineNumbers } from "@codemirror/view";
import { tags } from "@lezer/highlight";

/**
 * CodeMirror 6 rather than Monaco: it is a fraction of the size, it embeds
 * cleanly in a VS Code webview, and a body editor needs highlighting and line
 * numbers rather than a language server.
 *
 * The content is never reformatted while typing; pretty-printing is an explicit
 * action.
 */

const highlightStyle = HighlightStyle.define([
  { tag: tags.propertyName, color: "var(--ac-accent)" },
  { tag: tags.string, color: "var(--ac-status-ok)" },
  { tag: tags.number, color: "var(--ac-status-redirect)" },
  { tag: tags.bool, color: "var(--ac-status-client)" },
  { tag: tags.null, color: "var(--ac-status-client)" },
  { tag: tags.invalid, color: "var(--ac-danger)" },
]);

interface Props {
  value: string;
  language: "json" | "text";
  readOnly?: boolean;
  ariaLabel: string;
  /** Ctrl/Cmd+Enter inside the editor should still send. */
  onSubmit?: () => void;
  onChange?: (value: string) => void;
}

export function CodeEditor({
  value,
  language,
  readOnly = false,
  ariaLabel,
  onSubmit,
  onChange,
}: Props) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const languageCompartment = useRef(new Compartment());
  // Held in a ref so changing the handler does not rebuild the editor.
  const handlers = useRef({ onChange, onSubmit });
  useEffect(() => {
    handlers.current = { onChange, onSubmit };
  }, [onChange, onSubmit]);

  useEffect(() => {
    if (!host.current) return;

    const editor = new EditorView({
      parent: host.current,
      state: EditorState.create({
        doc: value,
        extensions: [
          lineNumbers(),
          history(),
          indentOnInput(),
          bracketMatching(),
          syntaxHighlighting(highlightStyle),
          languageCompartment.current.of(language === "json" ? json() : []),
          EditorState.readOnly.of(readOnly),
          EditorView.editable.of(!readOnly),
          EditorView.lineWrapping,
          keymap.of([
            {
              key: "Mod-Enter",
              preventDefault: true,
              run: () => {
                handlers.current.onSubmit?.();
                return true;
              },
            },
            ...historyKeymap,
            ...defaultKeymap,
          ]),
          EditorView.updateListener.of((update) => {
            if (update.docChanged) {
              handlers.current.onChange?.(update.state.doc.toString());
            }
          }),
          EditorView.contentAttributes.of({ "aria-label": ariaLabel }),
        ],
      }),
    });

    view.current = editor;
    return () => {
      editor.destroy();
      view.current = null;
    };
    // Rebuilding on every prop change would lose the cursor; the effects below
    // reconcile the parts that can change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    view.current?.dispatch({
      effects: languageCompartment.current.reconfigure(language === "json" ? json() : []),
    });
  }, [language]);

  // Only write back when the value genuinely differs, so typing is not
  // interrupted by the round trip through React state.
  useEffect(() => {
    const editor = view.current;
    if (!editor) return;
    const current = editor.state.doc.toString();
    if (current === value) return;
    editor.dispatch({
      changes: { from: 0, to: current.length, insert: value },
    });
  }, [value]);

  return <div className="ac-editor" ref={host} data-testid="code-editor" />;
}
