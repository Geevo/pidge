import type { HttpRequest, KeyValueEntry } from "../types";
import { paramsChanged } from "../lib/url";
import { KeyValueTable } from "./KeyValueTable";

interface Props {
  request: HttpRequest;
  onChange: (request: HttpRequest) => void;
}

/**
 * Editing a param rewrites only the query string of the URL; the scheme, host,
 * path, and fragment are left exactly as the user typed them.
 */
export function ParamsEditor({ request, onChange }: Props) {
  const handleChange = (queryParams: KeyValueEntry[]) => {
    const synced = paramsChanged(request.url, queryParams);
    onChange({ ...request, url: synced.url, queryParams: [...synced.queryParams] });
  };

  return (
    <KeyValueTable
      label="Query parameters"
      rows={request.queryParams}
      namePlaceholder="param"
      onChange={handleChange}
    />
  );
}
