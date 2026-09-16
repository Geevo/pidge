import type { KeyValueEntry } from "../types";
import { emptyRow } from "../state/factories";
import { CloseIcon } from "./icons";

interface Props {
  rows: readonly KeyValueEntry[];
  nameLabel?: string;
  valueLabel?: string;
  namePlaceholder?: string;
  valuePlaceholder?: string;
  label: string;
  onChange: (rows: KeyValueEntry[]) => void;
}

/**
 * The editor behind params, headers, url-encoded bodies, and environment
 * variables. There is always one blank row at the end, so adding a row is just
 * typing rather than hunting for a button.
 */
export function KeyValueTable({
  rows,
  label,
  nameLabel = "Name",
  valueLabel = "Value",
  namePlaceholder = "name",
  valuePlaceholder = "value",
  onChange,
}: Props) {
  const displayed =
    rows.length > 0 && isBlank(rows[rows.length - 1]!) ? rows : [...rows, emptyRow()];

  const update = (index: number, patch: Partial<KeyValueEntry>) => {
    const next = displayed.map((row, position) =>
      position === index ? { ...row, ...patch } : row,
    );
    // Keep exactly one trailing blank row.
    onChange(trimTrailingBlanks(next));
  };

  const remove = (index: number) => {
    onChange(trimTrailingBlanks(displayed.filter((_, position) => position !== index)));
  };

  return (
    <table className="ac-kv">
      <caption className="ac-visually-hidden">{label}</caption>
      <thead>
        <tr>
          <th className="ac-kv__check">
            <span className="ac-visually-hidden">Enabled</span>
          </th>
          <th>{nameLabel}</th>
          <th>{valueLabel}</th>
          <th className="ac-kv__remove">
            <span className="ac-visually-hidden">Remove</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {displayed.map((row, index) => (
          <tr key={row.id}>
            <td className="ac-kv__check">
              <input
                type="checkbox"
                aria-label={`Enable ${row.name || `row ${index + 1}`}`}
                checked={row.enabled}
                onChange={(event) => update(index, { enabled: event.target.checked })}
              />
            </td>
            <td>
              <input
                type="text"
                aria-label={`${nameLabel} ${index + 1}`}
                placeholder={namePlaceholder}
                spellCheck={false}
                value={row.name}
                onChange={(event) => update(index, { name: event.target.value })}
              />
            </td>
            <td>
              <input
                type="text"
                aria-label={`${valueLabel} ${index + 1}`}
                placeholder={valuePlaceholder}
                spellCheck={false}
                value={row.value}
                onChange={(event) => update(index, { value: event.target.value })}
              />
            </td>
            <td className="ac-kv__remove">
              {isBlank(row) ? null : (
                <button
                  type="button"
                  className="ac-icon-button"
                  aria-label={`Remove ${row.name || `row ${index + 1}`}`}
                  onClick={() => remove(index)}
                >
                  <CloseIcon size={12} />
                </button>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function isBlank(row: KeyValueEntry): boolean {
  return row.name === "" && row.value === "";
}

function trimTrailingBlanks(rows: KeyValueEntry[]): KeyValueEntry[] {
  const next = [...rows];
  while (next.length > 0 && isBlank(next[next.length - 1]!)) next.pop();
  return next;
}
