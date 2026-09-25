import { useEffect, useRef } from "react";
import { defaultKeymap, history, historyKeymap } from "@codemirror/commands";
import { css } from "@codemirror/lang-css";
import { html } from "@codemirror/lang-html";
import { javascript } from "@codemirror/lang-javascript";
import { json } from "@codemirror/lang-json";
import { xml } from "@codemirror/lang-xml";
import { yaml } from "@codemirror/lang-yaml";
import {
  HighlightStyle,
  StreamLanguage,
  bracketMatching,
  codeFolding,
  foldGutter,
  foldKeymap,
  indentOnInput,
  syntaxHighlighting,
} from "@codemirror/language";
import { csharp } from "../lib/csharp";
import { go, java, php, rust, zig } from "../lib/languages";
import { powershell } from "../lib/powershell";
import { python } from "../lib/python";
import { shell } from "../lib/shell";
import { Compartment, EditorState, type Extension } from "@codemirror/state";
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

import type { SyntaxLanguage } from "../lib/mime";
import { findInEditor } from "./findPanel";

/**
 * CodeMirror 6 rather than Monaco: it is a fraction of the size, it embeds
 * cleanly in a VS Code webview, and a body editor needs highlighting and line
 * numbers rather than a language server.
 *
 * The content is never reformatted while typing; pretty-printing is an explicit
 * action.
 */

/*
 * One style across every language, and one that never changes: each rule names
 * a `--ac-syntax-*` token, and the scheme picked in Settings is the document
 * attribute those tokens are defined against. Switching schemes is then a
 * repaint rather than a new highlight style, a reconfigured editor, and a lost
 * cursor. The tokens are documented in `styles.css`.
 *
 * A link is the exception: the underline makes it a piece of the interface
 * rather than a token, so it stays in the accent colour whatever the code
 * around it is wearing.
 */
const highlightStyle = HighlightStyle.define([
  /*
   * A plain name is the same colour as a key. Every language here calls the
   * thing on the left of a `=` and the thing after a `.` by the same colour,
   * and so does the editor everyone will compare this with; without the first
   * of these, a JavaScript body and the Node.js snippet were most of the way
   * to being one grey block.
   */
  {
    tag: [
      tags.propertyName,
      tags.attributeName,
      tags.tagName,
      tags.variableName,
      tags.standard(tags.variableName),
    ],
    color: "var(--ac-syntax-key)",
  },
  { tag: [tags.string, tags.attributeValue], color: "var(--ac-syntax-string)" },
  { tag: tags.number, color: "var(--ac-syntax-number)" },
  { tag: [tags.bool, tags.null, tags.atom, tags.keyword], color: "var(--ac-syntax-keyword)" },
  {
    tag: [tags.comment, tags.meta, tags.processingInstruction],
    color: "var(--ac-syntax-comment)",
  },
  {
    tag: [tags.operator, tags.punctuation, tags.separator],
    color: "var(--ac-syntax-punctuation)",
  },
  { tag: [tags.typeName, tags.className], color: "var(--ac-syntax-type)" },
  {
    tag: [
      tags.function(tags.variableName),
      tags.function(tags.propertyName),
      tags.definition(tags.variableName),
    ],
    color: "var(--ac-syntax-function)",
  },
  { tag: tags.link, color: "var(--ac-accent)", textDecoration: "underline" },
  { tag: tags.invalid, color: "var(--ac-syntax-invalid)" },
]);

/**
 * Everything this editor can colour: the languages a response body arrives in,
 * and the four a request can be written out as.
 */
export type EditorLanguage =
  | SyntaxLanguage
  | "shell"
  | "powershell"
  | "python"
  | "csharp"
  | "rust"
  | "go"
  | "java"
  | "php"
  | "zig";

/*
 * Every language is bundled rather than fetched: this app is read from disk,
 * and a body should be highlighted the moment it arrives.
 *
 * The four at the end are stream tokenizers rather than Lezer grammars, which
 * do not exist for a shell script or for C#. A tokenizer is the whole of what
 * colouring a twenty-line snippet needs: it is the parse tree they lack, and
 * only folding and indentation ever wanted one.
 *
 * All of them are ours. `legacy-modes` has a mode for most, and each of those
 * colours the keywords and the strings and leaves most of a generated snippet
 * the colour of plain text; the files in `lib/` say what each was missing.
 *
 * The five written with braces share `lib/curly`, which is the half of a
 * tokenizer they have in common; `lib/languages` is the half they do not.
 */
const LANGUAGES: Record<EditorLanguage, () => Extension> = {
  json,
  html,
  xml,
  css,
  javascript,
  yaml,
  text: () => [],
  shell: () => StreamLanguage.define(shell),
  powershell: () => StreamLanguage.define(powershell),
  python: () => StreamLanguage.define(python),
  csharp: () => StreamLanguage.define(csharp),
  rust: () => StreamLanguage.define(rust),
  go: () => StreamLanguage.define(go),
  java: () => StreamLanguage.define(java),
  php: () => StreamLanguage.define(php),
  zig: () => StreamLanguage.define(zig),
};

/**
 * Plain text has nothing to fold, and a stream tokenizer has no tree to fold
 * with. Everything with a real grammar folds its own blocks.
 */
export function isFoldable(language: EditorLanguage): boolean {
  return language !== "text" && !STREAM_LANGUAGES.includes(language);
}

const STREAM_LANGUAGES: readonly EditorLanguage[] = [
  "shell",
  "powershell",
  "python",
  "csharp",
  "rust",
  "go",
  "java",
  "php",
  "zig",
];

interface Props {
  value: string;
  language: EditorLanguage;
  readOnly?: boolean;
  ariaLabel: string;
  /** Gutter arrows that collapse and expand a block, object or element. */
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
          languageCompartment.current.of(LANGUAGES[language]()),
          folding ? [codeFolding(), foldGutter()] : [],
          EditorState.readOnly.of(readOnly),
          EditorView.editable.of(!readOnly),
          wrapCompartment.current.of(wrap ? EditorView.lineWrapping : []),
          findInEditor(),
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
          /*
           * A read-only editor is not `contenteditable`, so nothing in it can
           * take focus, and every keystroke falls through to the document: the
           * keymap above never runs, and Ctrl/Cmd+A selects the whole window
           * instead of the body being read. A tab stop fixes both — CodeMirror
           * focuses the content on mousedown, and from there its own select
           * all, copy and cursor keys apply.
           */
          EditorView.contentAttributes.of(
            readOnly ? { "aria-label": ariaLabel, tabindex: "0" } : { "aria-label": ariaLabel },
          ),
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

  /*
   * CodeMirror positions every line from measurements it takes once, and it
   * only retakes them when it notices something change. Two things it does not
   * notice: the pane being resized by the splitter, which changes the height
   * but not the width, and the mono web font arriving after the editor was
   * built, which changes how tall a line is. Either leaves the gutter drawn
   * against one set of numbers and the text against another — line numbers down
   * the side of an empty pane, with the body pushed somewhere below it.
   */
  useEffect(() => {
    const editor = view.current;
    const node = host.current;
    if (!editor || !node) return;

    const remeasure = () => editor.requestMeasure();
    const observer = new ResizeObserver(remeasure);
    observer.observe(node);

    let live = true;
    void document.fonts?.ready.then(() => {
      if (live) remeasure();
    });

    return () => {
      live = false;
      observer.disconnect();
    };
  }, []);

  useEffect(() => {
    view.current?.dispatch({
      effects: languageCompartment.current.reconfigure(LANGUAGES[language]()),
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
