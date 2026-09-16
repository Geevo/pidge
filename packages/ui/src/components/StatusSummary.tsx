import { useState } from "react";

import type { HttpResponse } from "../types";
import { formatBytes, formatDuration, statusClass } from "../lib/format";
import { CertificateDialog } from "./CertificateDialog";
import { LockIcon } from "./icons";

interface Props {
  response: HttpResponse;
}

/** `200 OK · 143 ms · 2.4 KB`, with the padlock when the connection was TLS. */
export function StatusSummary({ response }: Props) {
  const [certificateOpen, setCertificateOpen] = useState(false);
  const tls = response.tls;
  const expired = tls?.certificate?.expired ?? false;

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

      {tls ? (
        <>
          <span className="ac-status__sep">·</span>
          <button
            type="button"
            className={`ac-lock${expired ? " ac-lock--bad" : ""}`}
            aria-label="View certificate"
            title={`${tls.protocol ?? "Encrypted"} — view the certificate`}
            onClick={() => setCertificateOpen(true)}
          >
            <LockIcon size={12} />
            <span>{tls.protocol ?? "TLS"}</span>
          </button>
        </>
      ) : null}

      {certificateOpen && tls ? (
        <CertificateDialog
          host={hostOf(response.finalUrl)}
          tls={tls}
          onClose={() => setCertificateOpen(false)}
        />
      ) : null}
    </div>
  );
}

/** The host alone; the certificate was issued for that, not for the path. */
function hostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}
