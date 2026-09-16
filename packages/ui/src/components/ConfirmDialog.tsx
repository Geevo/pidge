import { useEffect, useRef } from "react";

interface Props {
  title: string;
  message: string;
  confirmLabel?: string;
  /** Red confirm button, for anything that destroys work. */
  danger?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}

/**
 * Yes or no, in the app rather than through `window.confirm`.
 *
 * The platform's dialog cannot be styled or sized from here, and looks like a
 * different application on each operating system — the same reason the
 * dropdowns and the save prompt are drawn in the app.
 */
export function ConfirmDialog({
  title,
  message,
  confirmLabel = "OK",
  danger = false,
  onConfirm,
  onClose,
}: Props) {
  const confirmRef = useRef<HTMLButtonElement>(null);

  // Focused, so Enter answers it and Tab reaches Cancel.
  useEffect(() => {
    confirmRef.current?.focus();
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

  return (
    <div className="ac-dialog-backdrop" role="presentation" onClick={onClose}>
      <div
        className="ac-dialog ac-dialog--prompt"
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="ac-dialog__header">
          <h2 className="ac-dialog__title">{title}</h2>
        </div>

        <div className="ac-dialog__body">
          <p className="ac-dialog__message">{message}</p>
        </div>

        <div className="ac-dialog__footer">
          <button type="button" className="ac-button" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            ref={confirmRef}
            className={`ac-button ${danger ? "ac-button--danger-solid" : "ac-button--primary"}`}
            onClick={() => {
              onConfirm();
              onClose();
            }}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
