import { useState } from "react";

import { EyeIcon, EyeOffIcon } from "./icons";

interface Props {
  value: string;
  onChange: (value: string) => void;
  /** What it holds, for the toggle's label: "password", "token". */
  name: string;
  id?: string;
  /** For a field with no `<label>` of its own, such as a table cell. */
  ariaLabel?: string;
  placeholder?: string;
}

/**
 * A field that keeps its value covered until the eye beside it is pressed.
 *
 * Covered each time it appears: what was shown for a moment should not stay
 * shown on the next request, or in front of the next person to see the screen.
 */
export function SecretInput({ value, onChange, name, id, ariaLabel, placeholder }: Props) {
  const [shown, setShown] = useState(false);

  return (
    <span className="ac-secret">
      <input
        id={id}
        type={shown ? "text" : "password"}
        aria-label={ariaLabel}
        autoComplete="off"
        spellCheck={false}
        placeholder={placeholder}
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
      <button
        type="button"
        className="ac-icon-button ac-secret__toggle"
        aria-label={`${shown ? "Hide" : "Show"} ${name}`}
        aria-pressed={shown}
        title={shown ? "Hide" : "Show"}
        onClick={() => setShown(!shown)}
      >
        {shown ? <EyeOffIcon size={13} /> : <EyeIcon size={13} />}
      </button>
    </span>
  );
}
