import type { HttpRequest, KeyValueEntry } from "../types";
import { KeyValueTable } from "./KeyValueTable";

interface Props {
  request: HttpRequest;
  onChange: (request: HttpRequest) => void;
}

export function HeadersEditor({ request, onChange }: Props) {
  const handleChange = (headers: KeyValueEntry[]) => onChange({ ...request, headers });

  const authHeaderSet = request.headers.some(
    (header) => header.enabled && header.name.trim().toLowerCase() === "authorization",
  );

  return (
    <>
      <KeyValueTable
        label="Headers"
        rows={request.headers}
        namePlaceholder="Header-Name"
        onChange={handleChange}
      />
      {authHeaderSet && request.auth.type !== "none" ? (
        <p className="ac-hint">
          This Authorization header wins; the Auth tab will be ignored for this request.
        </p>
      ) : null}
    </>
  );
}
