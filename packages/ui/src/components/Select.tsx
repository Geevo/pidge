import { useEffect, useId, useRef, useState } from "react";
import { createPortal } from "react-dom";

export interface SelectOption {
  value: string;
  label: string;
}

interface Props {
  value: string;
  options: readonly SelectOption[];
  onChange: (value: string) => void;
  /** Set when a `<label htmlFor>` points at this control. */
  id?: string;
  /** The id of that visible label. Give this or `label`. */
  labelledBy?: string;
  label?: string;
  className?: string;
  title?: string;
}

/**
 * A dropdown, drawn here rather than by the platform.
 *
 * A native `<select>` can be styled shut but not open: the list that drops down
 * is the platform's own and no CSS reaches it. In a WebKit webview on Linux it
 * is a GTK menu in the desktop's theme, so a light palette on a dark desktop
 * produced a black list with black text in it — and `gtk-application-prefer-
 * dark-theme` does not help, because a theme like Breeze ships its dark variant
 * as a separate theme rather than as a variant.
 *
 * So the list is a `<ul>`. That buys consistency across platforms too, which is
 * the same reason the app bundles its own fonts and draws its own checkbox.
 *
 * It is rendered into `document.body` and positioned against the trigger, or an
 * ancestor with `overflow: hidden` — the settings dialog, the tab strip — would
 * clip it.
 */
export function Select({
  value,
  options,
  onChange,
  id,
  labelledBy,
  label,
  className,
  title,
}: Props) {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [box, setBox] = useState<{ left: number; top: number; width: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const listId = useId();

  const selectedIndex = options.findIndex((option) => option.value === value);
  const selected = selectedIndex === -1 ? null : options[selectedIndex];

  const openList = () => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (rect) setBox({ left: rect.left, top: rect.bottom + 2, width: rect.width });
    setActive(Math.max(0, selectedIndex));
    setOpen(true);
  };

  const close = () => {
    setOpen(false);
    triggerRef.current?.focus();
  };

  const choose = (index: number) => {
    const option = options[index];
    if (option) onChange(option.value);
    close();
  };

  // The list takes the keys once it is open, so it takes the focus with them.
  useEffect(() => {
    if (open) listRef.current?.focus();
  }, [open]);

  useEffect(() => {
    if (!open) return;

    const onPointerDown = (event: PointerEvent) => {
      const target = event.target as Node | null;
      if (!target) return;
      if (triggerRef.current?.contains(target) || listRef.current?.contains(target)) return;
      setOpen(false);
    };
    /*
     * The list is fixed where the trigger was, so it must not outlive that
     * position: a scroll or a resize closes it rather than leaving it adrift.
     */
    const onMoved = () => setOpen(false);

    document.addEventListener("pointerdown", onPointerDown, true);
    window.addEventListener("scroll", onMoved, true);
    window.addEventListener("resize", onMoved);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown, true);
      window.removeEventListener("scroll", onMoved, true);
      window.removeEventListener("resize", onMoved);
    };
  }, [open]);

  const onListKeyDown = (event: React.KeyboardEvent) => {
    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        setActive((index) => Math.min(options.length - 1, index + 1));
        break;
      case "ArrowUp":
        event.preventDefault();
        setActive((index) => Math.max(0, index - 1));
        break;
      case "Home":
        event.preventDefault();
        setActive(0);
        break;
      case "End":
        event.preventDefault();
        setActive(options.length - 1);
        break;
      case "Enter":
      case " ":
        event.preventDefault();
        choose(active);
        break;
      case "Escape":
        event.preventDefault();
        close();
        break;
      case "Tab":
        setOpen(false);
        break;
    }
  };

  return (
    <>
      <button
        type="button"
        ref={triggerRef}
        id={id}
        className={`ac-select${className ? ` ${className}` : ""}`}
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-labelledby={labelledBy}
        aria-label={label}
        title={title}
        // Drives the method colours; see `[data-value]` in styles.css.
        data-value={value}
        onClick={() => (open ? setOpen(false) : openList())}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            openList();
          }
        }}
      >
        <span className="ac-select__value">{selected?.label ?? ""}</span>
      </button>

      {open && box
        ? createPortal(
            <ul
              ref={listRef}
              id={listId}
              className="ac-select__list"
              role="listbox"
              tabIndex={-1}
              aria-labelledby={labelledBy}
              aria-label={label}
              aria-activedescendant={`${listId}-${active}`}
              style={{ left: box.left, top: box.top, minWidth: box.width }}
              onKeyDown={onListKeyDown}
            >
              {options.map((option, index) => (
                <li
                  key={option.value}
                  id={`${listId}-${index}`}
                  className={`ac-select__option${index === active ? " ac-select__option--active" : ""}`}
                  role="option"
                  aria-selected={option.value === value}
                  data-value={option.value}
                  onPointerEnter={() => setActive(index)}
                  onClick={() => choose(index)}
                >
                  {option.label}
                </li>
              ))}
            </ul>,
            document.body,
          )
        : null}
    </>
  );
}
