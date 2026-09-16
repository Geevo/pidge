import type { HttpResponse } from "../types";
import { formatBytes, formatDuration, statusClass } from "../lib/format";

interface Props {
  response: HttpResponse;
}

/** `200 OK · 143 ms · 2.4 KB` */
export function StatusSummary({ response }: Props) {
  return (
    <div className="ac-status" role="status">
      <span className="ac-status__code" data-band={statusClass(response.status)}>
        {response.status} {response.statusText}
      </span>
      <span className="ac-status__sep">·</span>
      <span>{formatDuration(response.durationMs)}</span>
      <span className="ac-status__sep">·</span>
      <span>{formatBytes(response.sizeBytes)}</span>
      {response.truncated ? (
        <>
          <span className="ac-status__sep">·</span>
          <span title="The response hit the size limit and was cut short.">truncated</span>
        </>
      ) : null}
    </div>
  );
}
