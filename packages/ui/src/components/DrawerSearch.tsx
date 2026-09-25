import { CloseIcon } from "./icons";

interface Props {
  /** Names the field for a screen reader, and is its placeholder. */
  label: string;
  value: string;
  onChange: (value: string) => void;
}

/**
 * The filter at the top of a drawer. It narrows the list as it is typed into,
 * and Escape or the cross empties it again.
 *
 * A plain text field rather than `type="search"`, whose built-in clear button
 * each engine draws its own way and none of them in the theme.
 */
export function DrawerSearch({ label, value, onChange }: Props) {
  return (
    <div className="ac-drawer__search">
      <input
        type="text"
        aria-label={label}
        placeholder={label}
        spellCheck={false}
        autoComplete="off"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === "Escape" && value !== "") {
            event.stopPropagation();
            onChange("");
          }
        }}
      />
      {value !== "" ? (
        <button
          type="button"
          className="ac-icon-button"
          aria-label="Clear search"
          onClick={() => onChange("")}
        >
          <CloseIcon size={12} />
        </button>
      ) : null}
    </div>
  );
}
