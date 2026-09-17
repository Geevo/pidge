import { useState } from "react";

import type { ClientIdentitySettings, Settings, Theme } from "../types";
import { CloseIcon, PlusIcon } from "./icons";
import { Select } from "./Select";

interface Props {
  settings: Settings;
  storagePath: string;
  /** The host's version, for the About section. */
  version: string;
  /**
   * Called as the theme is picked, so the palette changes under the dialog
   * before it is saved. The preview is owned by the caller rather than written
   * to the document here: it is the caller that applies the theme, and having
   * two writers of `data-theme` would make "which one wins" a matter of order.
   */
  onPreviewTheme: (theme: Theme) => void;
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
const THEMES = [
  { value: "system", label: "Follow the system" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
  { value: "warmDark", label: "Warm dark" },
  { value: "warmLight", label: "Warm light" },
];

type Section = "general" | "certs" | "about";

const SECTIONS: { id: Section; label: string }[] = [
  { id: "general", label: "General" },
  { id: "certs", label: "Certs" },
  { id: "about", label: "About" },
];

export function SettingsDialog({
  settings,
  storagePath,
  version,
  onPreviewTheme,
  onSave,
  onClose,
}: Props) {
  const [section, setSection] = useState<Section>("general");
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
        className="ac-dialog ac-dialog--settings"
        role="dialog"
        aria-modal="true"
        aria-label="Settings"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="ac-dialog__header">
          <h2 className="ac-dialog__title">Settings</h2>
        </div>

        <div className="ac-subtabs" role="tablist" aria-label="Settings sections">
          {SECTIONS.map((entry) => (
            <button
              key={entry.id}
              type="button"
              role="tab"
              className="ac-subtab"
              aria-selected={section === entry.id}
              onClick={() => setSection(entry.id)}
            >
              {entry.label}
            </button>
          ))}
        </div>

        <div className="ac-dialog__body">
          {section === "general" ? (
            <>
              <div className="ac-field">
                <label id="ac-theme-label" htmlFor="ac-theme">
                  Theme
                </label>
                <Select
                  id="ac-theme"
                  labelledBy="ac-theme-label"
                  value={draft.theme}
                  options={THEMES}
                  onChange={(value) => {
                    const theme = value as Theme;
                    patch({ theme });
                    onPreviewTheme(theme);
                  }}
                />
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
            </>
          ) : null}

          {section === "certs" ? (
            <>
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
                  Sent when a server asks for one. A PEM holding the certificate and key, or a
                  PKCS#12 <code>.p12</code>/<code>.pfx</code> bundle, which is unpacked for you.
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
                    Turns off verification for every request. Adding the CA above is the better
                    answer.
                  </small>
                </span>
              </label>
            </>
          ) : null}

          {section === "about" ? (
            <div className="ac-about">
              <p className="ac-about__name">
                API Client <span className="ac-about__version">{version || "unknown version"}</span>
              </p>
              <p className="ac-hint">
                A local-first HTTP client. Nothing leaves this machine except the requests you send.
              </p>
              {storagePath ? (
                <p className="ac-hint">
                  State is kept in <code>{storagePath}</code>
                </p>
              ) : null}
              <p className="ac-hint">
                MIT licensed. IBM Plex Sans and IBM Plex Mono are bundled under the SIL Open Font
                License 1.1.
              </p>
            </div>
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
