import { useEffect } from "react";

import type { TlsDetails } from "../types";

interface Props {
  host: string;
  tls: TlsDetails;
  onClose: () => void;
}

/**
 * What the server presented, for the connection this response came back on.
 *
 * It is the leaf certificate only: reqwest hands back the peer certificate and
 * not the chain above it. Showing one certificate honestly beats implying a
 * chain that was never captured.
 */
export function CertificateDialog({ host, tls, onClose }: Props) {
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        onClose();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  const certificate = tls.certificate;

  return (
    <div className="ac-dialog-backdrop" role="presentation" onClick={onClose}>
      <div
        className="ac-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Certificate"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="ac-dialog__header">
          <h2 className="ac-dialog__title">Certificate</h2>
        </div>

        <div className="ac-dialog__body">
          {certificate === null ? (
            <p className="ac-hint">
              The connection to {host} was encrypted{tls.protocol ? ` with ${tls.protocol}` : ""},
              but its certificate could not be read.
            </p>
          ) : (
            <>
              {certificate.expired ? (
                <p className="ac-cert-note ac-cert-note--bad">
                  This certificate expired on {formatDate(certificate.notAfter)}.
                </p>
              ) : null}
              {certificate.selfSigned ? (
                <p className="ac-cert-note">
                  Self-signed: nothing above this certificate vouches for it.
                </p>
              ) : null}

              <dl className="ac-cert">
                <Row label="Host" value={host} />
                {tls.protocol ? <Row label="Protocol" value={tls.protocol} /> : null}
                <Row label="Subject" value={certificate.subject} />
                <Row label="Issuer" value={certificate.issuer} />
                <Row
                  label="Valid for"
                  value={
                    certificate.subjectAltNames.length > 0
                      ? certificate.subjectAltNames.join(", ")
                      : "No subject alternative names"
                  }
                />
                <Row label="Valid from" value={formatDate(certificate.notBefore)} />
                <Row label="Valid until" value={formatDate(certificate.notAfter)} />
                <Row label="Serial" value={certificate.serial} mono />
                <Row label="Signature" value={certificate.signatureAlgorithm} />
                <Row label="SHA-256" value={certificate.sha256Fingerprint} mono />
              </dl>
            </>
          )}
        </div>

        <div className="ac-dialog__footer">
          <button type="button" className="ac-button ac-button--primary" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

function Row({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="ac-cert__row">
      <dt>{label}</dt>
      <dd className={mono ? "ac-cert__value ac-cert__value--mono" : "ac-cert__value"}>{value}</dd>
    </div>
  );
}

/** RFC 3339 from the engine, shown in the reader's own timezone. */
function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}
