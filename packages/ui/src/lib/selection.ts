/**
 * Select the whole of one element, and nothing around it.
 *
 * Select all belongs to whatever holds focus. A text field answers for itself;
 * anywhere else the browser would take the entire window, so the part of the
 * app that owns the key has to say what it meant instead.
 */
export function selectContents(node: Node): void {
  const selection = window.getSelection();
  if (!selection) return;

  const range = document.createRange();
  range.selectNodeContents(node);
  selection.removeAllRanges();
  selection.addRange(range);
}
