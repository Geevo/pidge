import { useEffect, useRef, useState } from "react";

interface Props {
  title: string;
  label: string;
  initialValue: string;
  submitLabel?: string;
  onSubmit: (value: string) => void;
  onClose: () => void;
}

/**
 * Asking for one line of text, in the app rather than through `window.prompt`.
 *
 * The platform's prompt is a native dialog sized by the platform: in a WebKit
 * webview on Linux the entry is too narrow to show a URL, so the thing you were
 * being asked to confirm was the thing you could not read. It cannot be styled
 * or sized from here, which is the same reason the dropdowns are drawn in the
 * app.
 */
export function PromptDialog({
  title,
  label,
  initialValue,
  submitLabel = "Save",
  onSubmit,
  onClose,
}: Props) {
  const [value, setValue] = useState(initialValue);
  const inputRef = useRef<HTMLInputElement>(null);

  /*
   * Selected, not just focused: the suggestion is usually the thing to replace.
   * Selected backwards, so that a value too long for the field is shown from
   * its start — `select()` leaves the caret at the end, which scrolls a URL to
   * the one part of it nobody needs to read.
   */
  useEffect(() => {
    const input = inputRef.current;
    if (!input) return;
    input.focus();
    input.setSelectionRange(0, input.value.length, "backward");
    // The direction alone does not move the viewport in every engine.
    input.scrollLeft = 0;
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        onClose();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  const submit = () => {
    const trimmed = value.trim();
    if (trimmed === "") return;
    onSubmit(trimmed);
    onClose();
  };

  return (
    <div className="ac-dialog-backdrop" role="presentation" onClick={onClose}>
      <form
        className="ac-dialog ac-dialog--prompt"
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onClick={(event) => event.stopPropagation()}
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <div className="ac-dialog__header">
          <h2 className="ac-dialog__title">{title}</h2>
        </div>

        <div className="ac-dialog__body">
          <div className="ac-field">
            <label htmlFor="ac-prompt-value">{label}</label>
            <input
              id="ac-prompt-value"
              ref={inputRef}
              type="text"
              spellCheck={false}
              value={value}
              onChange={(event) => setValue(event.target.value)}
            />
          </div>
        </div>

        <div className="ac-dialog__footer">
          <button type="button" className="ac-button" onClick={onClose}>
            Cancel
          </button>
          <button
            type="submit"
            className="ac-button ac-button--primary"
            disabled={value.trim() === ""}
          >
            {submitLabel}
          </button>
        </div>
      </form>
    </div>
  );
}
