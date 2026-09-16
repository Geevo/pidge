import type { HttpMethod } from "../types";
import { HTTP_METHODS } from "../types";

interface Props {
  value: HttpMethod;
  onChange: (method: HttpMethod) => void;
}

export function MethodSelector({ value, onChange }: Props) {
  return (
    <select
      className="ac-method"
      aria-label="Method"
      // Drives the Swagger colour; see `.ac-method[data-method]` in styles.css.
      data-method={value}
      value={value}
      onChange={(event) => onChange(event.target.value as HttpMethod)}
    >
      {HTTP_METHODS.map((method) => (
        <option key={method} value={method}>
          {method}
        </option>
      ))}
    </select>
  );
}
