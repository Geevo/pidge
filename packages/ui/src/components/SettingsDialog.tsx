import { useState } from "react";

import type { ClientIdentitySettings, Settings, Theme } from "../types";
import { CloseIcon, PlusIcon } from "./icons";

interface Props {
  settings: Settings;
  storagePath: string;
  onSave: (settings: Settings) => void;
  onClose: () => void;
}

/**
 * Settings, in one dialog rather than a permanent panel.
 *
 * The certificate section is the reason this screen exists: the client already
 * trusts whatever the operating system trusts, and this is for the two cases
 * the OS store cannot cover — an internal CA, and a client certificate.
 */
export function SettingsDialog({ settings, storagePath, onSave, onClose }: Props) {
  const [draft, setDraft] = useState<Settings>(() => ({
    ...settings,
    tls: { ...settings.tls, extraCaFiles: [...settings.tls.extraCaFiles] },
  }));

  const patch = (change: Partial<Settings>) => setDraft((current) => ({ ...current, ...change }));
  const patchTls = (change: Partial<Settings["tls"]>) =>
    setDraft((current) => ({ ...current, tls: { ...current.tls, ...change } }));

  const identity: ClientIdentitySettings | null = draft.tls.clientIdentity;

  const setCaFile = (index: number, value: string) => {
    const next = [...draft.tls.extraCaFiles];
    next[index] = value;
    patchTls({ extraCaFiles: next });
  };

  return (
    <div className="ac-dialog-backdrop" role="presentation" onClick={onClose}>
      <div
        className="ac-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Settings"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="ac-dialog__header">
          <h2 className="ac-dialog__title">Settings</h2>
        </div>

        <div className="ac-dialog__body">
          <div className="ac-field">
            <label htmlFor="ac-theme">Theme</label>
            <select
              id="ac-theme"
              value={draft.theme}
              onChange={(event) => patch({ theme: event.target.value as Theme })}
            >
              <option value="system">Follow the system</option>
              <option value="light">Light</option>
              <option value="dark">Dark</option>
            </select>
          </div>

          <div className="ac-field">
            <label htmlFor="ac-timeout">Timeout</label>
            <input
              id="ac-timeout"
              type="text"
              inputMode="numeric"
              value={String(draft.timeoutMs)}
              onChange={(event) =>
                patch({ timeoutMs: Number(event.target.value.replace(/\D/g, "")) || 0 })
              }
            />
            <span className="ac-field__suffix">ms</span>
          </div>

          <div className="ac-field">
            <label htmlFor="ac-max-history">Keep history</label>
            <input
              id="ac-max-history"
              type="text"
              inputMode="numeric"
              value={String(draft.maxHistory)}
              onChange={(event) =>
                patch({ maxHistory: Number(event.target.value.replace(/\D/g, "")) || 0 })
              }
            />
            <span className="ac-field__suffix">entries</span>
          </div>

          <label className="ac-field ac-field--toggle">
            <input
              type="checkbox"
              checked={draft.followRedirects}
              onChange={(event) => patch({ followRedirects: event.target.checked })}
            />
            <span>Follow redirects</span>
          </label>

          <label className="ac-field ac-field--toggle">
            <input
              type="checkbox"
              checked={draft.restoreTabs}
              onChange={(event) => patch({ restoreTabs: event.target.checked })}
            />
            <span>Reopen tabs on restart</span>
          </label>

          <label className="ac-field ac-field--toggle">
            <input
              type="checkbox"
              checked={draft.wrapResponseLines}
              onChange={(event) => patch({ wrapResponseLines: event.target.checked })}
            />
            <span>Wrap long response lines</span>
          </label>

          <h3 className="ac-section">Certificates</h3>

          <label className="ac-field ac-field--toggle">
            <input
              type="checkbox"
              checked={draft.tls.useSystemRoots}
              onChange={(event) => patchTls({ useSystemRoots: event.target.checked })}
            />
            <span>
              Trust the system certificate store
              <small>Windows, macOS Keychain, or the system CA bundle on Linux.</small>
            </span>
          </label>

          <div className="ac-section__body">
            <div className="ac-section__label">Additional trusted CAs</div>
            {draft.tls.extraCaFiles.length === 0 ? (
              <p className="ac-hint">None. Add a PEM or DER file to trust an internal CA.</p>
            ) : null}

            {draft.tls.extraCaFiles.map((file, index) => (
              <div className="ac-field" key={index}>
                <input
                  type="text"
                  aria-label={`CA file ${index + 1}`}
                  placeholder="/path/to/internal-ca.pem"
                  spellCheck={false}
                  value={file}
                  onChange={(event) => setCaFile(index, event.target.value)}
                />
                <button
                  type="button"
                  className="ac-icon-button"
                  aria-label={`Remove CA file ${index + 1}`}
                  onClick={() =>
                    patchTls({
                      extraCaFiles: draft.tls.extraCaFiles.filter(
                        (_, position) => position !== index,
                      ),
                    })
                  }
                >
                  <CloseIcon size={12} />
                </button>
              </div>
            ))}

            <div className="ac-field">
              <button
                type="button"
                className="ac-button ac-button--icon"
                onClick={() => patchTls({ extraCaFiles: [...draft.tls.extraCaFiles, ""] })}
              >
                <PlusIcon size={13} />
                Add CA file
              </button>
            </div>
          </div>

          <div className="ac-section__body">
            <div className="ac-section__label">Client certificate</div>
            <p className="ac-hint">
              Sent when a server asks for one. A PEM holding the certificate and key, or a PKCS#12{" "}
              <code>.p12</code>/<code>.pfx</code> bundle, which is unpacked for you.
            </p>

            <div className="ac-field">
              <label htmlFor="ac-client-cert">File</label>
              <input
                id="ac-client-cert"
                type="text"
                placeholder="/path/to/client.p12"
                spellCheck={false}
                value={identity?.path ?? ""}
                onChange={(event) =>
                  patchTls({
                    clientIdentity:
                      event.target.value.trim() === ""
                        ? null
                        : { path: event.target.value, password: identity?.password ?? null },
                  })
                }
              />
            </div>

            {identity ? (
              <div className="ac-field">
                <label htmlFor="ac-client-pass">Password</label>
                <input
                  id="ac-client-pass"
                  type="password"
                  placeholder="Required for .p12 / .pfx"
                  value={identity.password ?? ""}
                  onChange={(event) =>
                    patchTls({
                      clientIdentity: { path: identity.path, password: event.target.value },
                    })
                  }
                />
              </div>
            ) : null}
          </div>

          <label className="ac-field ac-field--toggle ac-field--danger">
            <input
              type="checkbox"
              checked={draft.tls.acceptInvalidCerts}
              onChange={(event) => patchTls({ acceptInvalidCerts: event.target.checked })}
            />
            <span>
              Accept invalid certificates
              <small>
                Turns off verification for every request. Adding the CA above is the better answer.
              </small>
            </span>
          </label>

          {storagePath ? (
            <p className="ac-hint">
              Stored in <code>{storagePath}</code>
            </p>
          ) : null}
        </div>

        <div className="ac-dialog__footer">
          <button type="button" className="ac-button" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="ac-button ac-button--primary"
            onClick={() => {
              onSave(draft);
              onClose();
            }}
          >
            Save
          </button>
        </div>
      </div>
    </div>
  );
}
