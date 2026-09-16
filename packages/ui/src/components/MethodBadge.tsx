import type { HttpMethod } from "../types";

interface Props {
  method: HttpMethod;
  /** Tighter, for dense lists like history. */
  small?: boolean;
}

/**
 * The method as a Swagger-coloured pill.
 *
 * A pill rather than coloured text, because it carries its own contrast and so
 * reads the same against a light theme, a dark theme, or a VS Code theme
 * nobody has seen yet.
 */
export function MethodBadge({ method, small = false }: Props) {
  return (
    <span
      className={`ac-method-badge${small ? " ac-method-badge--small" : ""}`}
      data-method={method}
    >
      {method}
    </span>
  );
}
