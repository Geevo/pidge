import { useEffect, useRef } from "react";
import { defaultKeymap, history, historyKeymap } from "@codemirror/commands";
import { json } from "@codemirror/lang-json";
import {
  HighlightStyle,
  bracketMatching,
  codeFolding,
  foldGutter,
  foldKeymap,
  indentOnInput,
  syntaxHighlighting,
} from "@codemirror/language";
import { Compartment, EditorState } from "@codemirror/state";
import {
  EditorView,
  drawSelection,
  dropCursor,
  highlightActiveLine,
  highlightActiveLineGutter,
  keymap,
  lineNumbers,
} from "@codemirror/view";
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
  /** Gutter arrows that collapse and expand `{...}` and `[...]`. */
  folding?: boolean;
  /** Off by default: a response body is easier to scan unwrapped. */
  wrap?: boolean;
  /** Ctrl/Cmd+Enter inside the editor should still send. */
  onSubmit?: () => void;
  onChange?: (value: string) => void;
  /** Handed the view once, so a toolbar can run commands against it. */
  onReady?: (view: EditorView) => void;
}

export function CodeEditor({
  value,
  language,
  readOnly = false,
  ariaLabel,
  folding = false,
  wrap = true,
  onSubmit,
  onChange,
  onReady,
}: Props) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const languageCompartment = useRef(new Compartment());
  const wrapCompartment = useRef(new Compartment());
  // Held in a ref so changing the handler does not rebuild the editor.
  const handlers = useRef({ onChange, onSubmit, onReady });
  useEffect(() => {
    handlers.current = { onChange, onSubmit, onReady };
  }, [onChange, onSubmit, onReady]);

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
          // Without drawSelection the caret is the browser's own, which the
          // webview draws in a colour we do not control and which disappears
          // against a dark background. These four also make the active line
          // visible, so you can see where you are in a long body.
          drawSelection(),
          dropCursor(),
          highlightActiveLine(),
          highlightActiveLineGutter(),
          syntaxHighlighting(highlightStyle),
          languageCompartment.current.of(language === "json" ? json() : []),
          folding ? [codeFolding(), foldGutter()] : [],
          EditorState.readOnly.of(readOnly),
          EditorView.editable.of(!readOnly),
          wrapCompartment.current.of(wrap ? EditorView.lineWrapping : []),
          keymap.of([
            ...(folding ? foldKeymap : []),
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
    handlers.current.onReady?.(editor);
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

  useEffect(() => {
    view.current?.dispatch({
      effects: wrapCompartment.current.reconfigure(wrap ? EditorView.lineWrapping : []),
    });
  }, [wrap]);

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
