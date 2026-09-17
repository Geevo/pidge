import { matchEditingCommand } from "./shortcuts";
import { createHistory, record, redo, undo, type History, type Snapshot } from "./textHistory";

/**
 * Undo and redo for every text field in the app, installed once.
 *
 * Doing this per component would mean wiring forty-odd inputs and remembering
 * to wire the next one. The keys and the history are the same everywhere, so
 * they live here: the app listens at the document, keeps a history per element,
 * and steps it when someone presses Ctrl+Z.
 *
 * Why it has to exist at all is in `textHistory`: WebKitGTK never binds the
 * keys, and the history browsers do keep undoes a whole run of typing at once.
 */

type Field = HTMLInputElement | HTMLTextAreaElement;

/*
 * The input types that hold text a person edits by typing. Checkboxes, radios
 * and the rest are not ours, and `number` is left out because reading a caret
 * position from one throws.
 */
const TEXTUAL = new Set(["text", "search", "url", "email", "tel", "password", ""]);

function isField(target: EventTarget | null): target is Field {
  if (target instanceof HTMLTextAreaElement) return true;
  if (!(target instanceof HTMLInputElement)) return false;
  return TEXTUAL.has(target.type);
}

/** CodeMirror keeps its own history and binds its own keys; leave it be. */
function isOurs(node: Field): boolean {
  return node.closest(".cm-editor") === null;
}

function snapshot(node: Field): Snapshot {
  return { value: node.value, caret: node.selectionStart ?? node.value.length };
}

/**
 * Writes through the prototype's setter rather than the property.
 *
 * React watches the property to decide whether a value really changed, and a
 * plain assignment updates what it watches — so the input event that follows
 * would look like nothing happened and the component's state would drift away
 * from what is on screen.
 */
function write(node: Field, value: string): void {
  const prototype =
    node instanceof HTMLTextAreaElement
      ? HTMLTextAreaElement.prototype
      : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(prototype, "value")?.set?.call(node, value);
}

export function installFieldHistory(target: Document = document): () => void {
  const histories = new WeakMap<Field, History>();
  // Set while a step is being applied, so the edit it causes is not recorded.
  let applying = false;

  const onFocusIn = (event: Event) => {
    const node = event.target;
    if (!isField(node) || !isOurs(node)) return;
    if (!histories.has(node)) histories.set(node, createHistory(snapshot(node)));
  };

  const onInput = (event: Event) => {
    if (applying) return;
    const node = event.target;
    if (!isField(node) || !isOurs(node)) return;

    const history = histories.get(node) ?? createHistory({ value: "", caret: 0 });
    histories.set(node, record(history, snapshot(node), Date.now()));
  };

  const onKeyDown = (event: KeyboardEvent) => {
    const node = event.target;
    if (!isField(node) || !isOurs(node)) return;

    const command = matchEditingCommand(event);
    if (!command) return;
    event.preventDefault();

    const history = histories.get(node);
    const current = history?.entries[history.index];

    /*
     * The text was replaced by something other than typing — another tab's
     * request, the params table rewriting the query, a restored session. There
     * is nothing coherent to step back to, so this becomes the new beginning.
     */
    if (!history || !current || current.value !== node.value) {
      histories.set(node, createHistory(snapshot(node)));
      return;
    }

    const step = command === "undo" ? undo(history) : redo(history);
    if (!step) return;

    applying = true;
    write(node, step.snapshot.value);
    node.setSelectionRange(step.snapshot.caret, step.snapshot.caret);
    node.dispatchEvent(new Event("input", { bubbles: true }));
    applying = false;

    histories.set(node, step.history);
  };

  target.addEventListener("focusin", onFocusIn, true);
  target.addEventListener("input", onInput, true);
  target.addEventListener("keydown", onKeyDown, true);

  return () => {
    target.removeEventListener("focusin", onFocusIn, true);
    target.removeEventListener("input", onInput, true);
    target.removeEventListener("keydown", onKeyDown, true);
  };
}
