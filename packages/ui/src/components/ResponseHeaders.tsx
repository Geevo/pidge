import type { KeyValueEntry } from "../types";

interface Props {
  headers: readonly KeyValueEntry[];
}

export function ResponseHeaders({ headers }: Props) {
  if (headers.length === 0) {
    return <p className="ac-hint">No response headers.</p>;
  }

  return (
    <table className="ac-kv">
      <caption className="ac-visually-hidden">Response headers</caption>
      <thead>
        <tr>
          <th>Name</th>
          <th>Value</th>
        </tr>
      </thead>
      <tbody>
        {headers.map((header) => (
          <tr key={header.id}>
            <td>
              <span className="ac-response-header-name">{header.name}</span>
            </td>
            <td>
              <span className="ac-response-header-value">{header.value}</span>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
