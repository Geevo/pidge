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
    const synced = paramsChanged(request.url, queryParams, request.encodeQuery);
    onChange({ ...request, url: synced.url, queryParams: [...synced.queryParams] });
  };

  // Flipping the switch rewrites the URL under the new rule, so the answer to
  // "what will you send?" is on screen rather than a send away.
  const setEncodeQuery = (encodeQuery: boolean) => {
    const synced = paramsChanged(request.url, request.queryParams, encodeQuery);
    onChange({ ...request, encodeQuery, url: synced.url });
  };

  return (
    <>
      <label className="ac-table-check" title="Percent-encode parameter names and values">
        <input
          type="checkbox"
          checked={request.encodeQuery}
          onChange={(event) => setEncodeQuery(event.target.checked)}
        />
        <span>URL-encode parameters</span>
      </label>

      <KeyValueTable
        label="Query parameters"
        rows={request.queryParams}
        namePlaceholder="param"
        onChange={handleChange}
      />
    </>
  );
}
