import { useState } from "react";

import type { FilePickRequest } from "../bridge";
import { FONT_SCALES } from "../lib/fontScale";
import { shortcutHint } from "../lib/shortcuts";
import type { ClientIdentitySettings, Settings, SyntaxTheme, Theme } from "../types";
import { CloseIcon, PlusIcon } from "./icons";
import { SecretInput } from "./SecretInput";
import logo from "../assets/app-icon.svg";

interface Props {
  settings: Settings;
  storagePath: string;
  /** The host's version, for the About section. */
  version: string;
  /**
   * The host's file chooser, when it has one. Absent on a host that cannot show
   * one, and the Browse buttons go with it.
   */
  onBrowse?: (request: FilePickRequest) => Promise<string | null>;
  /**
   * Called as the theme is picked, so the palette changes under the dialog
   * before it is saved. The preview is owned by the caller rather than written
   * to the document here: it is the caller that applies the theme, and having
   * two writers of `data-theme` would make "which one wins" a matter of order.
   */
  onPreviewTheme: (theme: Theme) => void;
  /** The same, for the syntax colours. */
  onPreviewSyntax: (syntaxTheme: SyntaxTheme) => void;
  /**
   * The text size, which is not a draft like the rest of this dialog: it
   * applies and is kept the moment it is clicked, the way the keyboard already
   * changes it from anywhere. Somebody who grew the text to read the screen
   * should not then have to find Save, or lose it to Cancel.
   */
  onFontScale: (fontScale: number) => void;
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
const THEMES: { value: Theme; label: string }[] = [
  { value: "system", label: "System" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
  { value: "warmDark", label: "Warm dark" },
  { value: "warmLight", label: "Warm light" },
];

/**
 * The app in miniature: the window, a tab, the URL bar with Send, and two
 * lines of text. A palette cannot be judged from its name, and it cannot be
 * judged from a row of coloured squares either — what matters is how the
 * surfaces sit on one another, so the sample is the screen in small.
 */
function ThemeSample({ theme }: { theme: Theme }) {
  return (
    <span className="ac-mini" data-theme={theme}>
      <span className="ac-mini__chrome">
        <span className="ac-mini__tab" />
      </span>
      <span className="ac-mini__bar">
        <span className="ac-mini__field" />
        <span className="ac-mini__send" />
      </span>
      <span className="ac-mini__text" />
      <span className="ac-mini__text ac-mini__text--short" />
    </span>
  );
}

const SYNTAX_THEMES: { value: SyntaxTheme; label: string }[] = [
  { value: "app", label: "Default" },
  { value: "vsCode", label: "VS Code" },
  { value: "one", label: "Atom One" },
  { value: "github", label: "GitHub" },
];

/*
 * A scheme is chosen by looking at it, so the swatch is the control: a few
 * lines of JSON in each scheme's own colours, on the background the editor
 * actually uses, because that background does not change with the scheme.
 *
 * Keys, a string, a number and a boolean cover every colour a response body
 * spends most of its time in. The rest — comments, types, function names —
 * belong to the other languages, and are not worth a wider sample here.
 */
const SAMPLE: { role: string; text: string }[][] = [
  [{ role: "punctuation", text: "{" }],
  [
    { role: "key", text: '  "id"' },
    { role: "punctuation", text: ": " },
    { role: "string", text: '"demo"' },
    { role: "punctuation", text: "," },
  ],
  [
    { role: "key", text: '  "port"' },
    { role: "punctuation", text: ": " },
    { role: "number", text: "8080" },
    { role: "punctuation", text: "," },
  ],
  [
    { role: "key", text: '  "live"' },
    { role: "punctuation", text: ": " },
    { role: "keyword", text: "true" },
  ],
  [{ role: "punctuation", text: "}" }],
];

/*
 * "Appearance" rather than "Themes": the section holds the text size as well
 * now, and a size is not a theme. Everything about how the app looks sits
 * behind the one word.
 */
type Section = "general" | "appearance" | "certs" | "about";

const SECTIONS: { id: Section; label: string }[] = [
  { id: "general", label: "General" },
  { id: "appearance", label: "Appearance" },
  { id: "certs", label: "Certs" },
  { id: "about", label: "About" },
];

/* The extensions each platform's chooser will offer. */
const CA_FILTERS = [
  { name: "Certificates", extensions: ["pem", "crt", "cer", "der"] },
  { name: "All files", extensions: ["*"] },
];

const IDENTITY_FILTERS = [
  { name: "Certificates and bundles", extensions: ["p12", "pfx", "pem"] },
  { name: "All files", extensions: ["*"] },
];

export function SettingsDialog({
  settings,
  storagePath,
  version,
  onBrowse,
  onPreviewTheme,
  onPreviewSyntax,
  onFontScale,
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

  const setIdentityPath = (value: string) =>
    patchTls({
      clientIdentity:
        value.trim() === "" ? null : { path: value, password: identity?.password ?? null },
    });

  /** Nothing happens when the chooser is dismissed, or when it fails. */
  const browseFor = async (
    title: string,
    filters: FilePickRequest["filters"],
    apply: (path: string) => void,
  ) => {
    if (!onBrowse) return;
    const chosen = await onBrowse({ title, filters }).catch(() => null);
    if (chosen) apply(chosen);
  };

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
              <div className="ac-group">
                <h3 className="ac-group__title">Requests</h3>

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

                <label className="ac-field ac-field--toggle">
                  <input
                    type="checkbox"
                    checked={draft.followRedirects}
                    onChange={(event) => patch({ followRedirects: event.target.checked })}
                  />
                  <span>Follow redirects</span>
                </label>
              </div>

              <div className="ac-group">
                <h3 className="ac-group__title">What is kept</h3>

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
                    checked={draft.restoreTabs}
                    onChange={(event) => patch({ restoreTabs: event.target.checked })}
                  />
                  <span>Reopen tabs on restart</span>
                </label>
              </div>

              <div className="ac-group">
                <h3 className="ac-group__title">Responses</h3>

                <label className="ac-field ac-field--toggle">
                  <input
                    type="checkbox"
                    checked={draft.wrapResponseLines}
                    onChange={(event) => patch({ wrapResponseLines: event.target.checked })}
                  />
                  <span>Wrap long response lines</span>
                </label>
              </div>
            </>
          ) : null}

          {section === "appearance" ? (
            <>
              <div className="ac-group">
                <h3 className="ac-group__title" id="ac-text-size-label">
                  Text size
                </h3>

                {/*
                 * A ladder of sizes rather than a number to type, and no
                 * sample beside each one: the app is drawn at the chosen size
                 * the instant it is clicked, this dialog with it, so the
                 * preview is the whole window rather than two letters of it.
                 * Click down the row until it reads.
                 */}
                <div className="ac-sizes" role="radiogroup" aria-labelledby="ac-text-size-label">
                  {FONT_SCALES.map((scale) => (
                    <label className="ac-size" key={scale}>
                      <input
                        className="ac-visually-hidden"
                        type="radio"
                        name="ac-font-scale"
                        value={scale}
                        checked={settings.fontScale === scale}
                        onChange={() => onFontScale(scale)}
                      />
                      <span className="ac-size__name">{scale}%</span>
                    </label>
                  ))}
                </div>

                <p className="ac-hint">
                  <code>{shortcutHint("textBigger")}</code> bigger,{" "}
                  <code>{shortcutHint("textSmaller")}</code> smaller,{" "}
                  <code>{shortcutHint("textReset")}</code> reset.
                </p>
              </div>

              <div className="ac-group">
                <h3 className="ac-group__title" id="ac-theme-label">
                  Theme
                </h3>

                <div className="ac-swatches" role="radiogroup" aria-labelledby="ac-theme-label">
                  {THEMES.map((entry) => (
                    <label className="ac-swatch" key={entry.value}>
                      <input
                        className="ac-visually-hidden"
                        type="radio"
                        name="ac-theme"
                        value={entry.value}
                        checked={draft.theme === entry.value}
                        onChange={() => {
                          patch({ theme: entry.value });
                          onPreviewTheme(entry.value);
                        }}
                      />
                      <span className="ac-swatch__sample ac-swatch__sample--mini" aria-hidden>
                        {/*
                         * "System" is both palettes, so it is drawn as both,
                         * split corner to corner. Showing whichever the desktop
                         * prefers today would make it a duplicate of Light or
                         * Dark, with nothing to say it will follow.
                         */}
                        {entry.value === "system" ? (
                          <>
                            <span className="ac-mini-half">
                              <ThemeSample theme="light" />
                            </span>
                            <span className="ac-mini-half ac-mini-half--far">
                              <ThemeSample theme="dark" />
                            </span>
                          </>
                        ) : (
                          <ThemeSample theme={entry.value} />
                        )}
                      </span>
                      <span className="ac-swatch__name">{entry.label}</span>
                    </label>
                  ))}
                </div>
              </div>

              <div className="ac-group">
                <h3 className="ac-group__title" id="ac-syntax-label">
                  Syntax colours
                </h3>

                <div
                  className="ac-swatches ac-swatches--code"
                  role="radiogroup"
                  aria-labelledby="ac-syntax-label"
                >
                  {SYNTAX_THEMES.map((entry) => (
                    <label className="ac-swatch" key={entry.value}>
                      <input
                        className="ac-visually-hidden"
                        type="radio"
                        name="ac-syntax"
                        value={entry.value}
                        checked={draft.syntaxTheme === entry.value}
                        onChange={() => {
                          patch({ syntaxTheme: entry.value });
                          onPreviewSyntax(entry.value);
                        }}
                      />
                      <span className="ac-swatch__sample" data-syntax={entry.value} aria-hidden>
                        {SAMPLE.map((line, index) => (
                          <span className="ac-swatch__line" key={index}>
                            {line.map((token, position) => (
                              <span className={`ac-tok-${token.role}`} key={position}>
                                {token.text}
                              </span>
                            ))}
                          </span>
                        ))}
                      </span>
                      <span className="ac-swatch__name">{entry.label}</span>
                    </label>
                  ))}
                </div>
              </div>
            </>
          ) : null}

          {section === "certs" ? (
            <>
              <div className="ac-group">
                <h3 className="ac-group__title">Trusted roots</h3>

                <label className="ac-field ac-field--toggle">
                  <input
                    type="checkbox"
                    checked={draft.tls.useSystemRoots}
                    onChange={(event) => patchTls({ useSystemRoots: event.target.checked })}
                  />
                  <span>
                    The system certificate store
                    <small>Windows, macOS Keychain, or the system CA bundle on Linux.</small>
                  </span>
                </label>

                <div className="ac-group__row">
                  <span className="ac-group__label">Additional CAs</span>
                  <button
                    type="button"
                    className="ac-button ac-button--quiet ac-button--icon"
                    onClick={() => patchTls({ extraCaFiles: [...draft.tls.extraCaFiles, ""] })}
                  >
                    <PlusIcon size={13} />
                    Add
                  </button>
                </div>

                {draft.tls.extraCaFiles.length === 0 ? (
                  <p className="ac-hint">
                    None. Add a PEM or DER file to trust an internal CA as well as the store above.
                  </p>
                ) : (
                  draft.tls.extraCaFiles.map((file, index) => (
                    <div className="ac-field ac-field--path" key={index}>
                      <input
                        type="text"
                        aria-label={`CA file ${index + 1}`}
                        placeholder="/path/to/internal-ca.pem"
                        spellCheck={false}
                        value={file}
                        onChange={(event) => setCaFile(index, event.target.value)}
                      />
                      {onBrowse ? (
                        <button
                          type="button"
                          className="ac-button ac-button--quiet"
                          onClick={() =>
                            void browseFor(`CA file ${index + 1}`, CA_FILTERS, (path) =>
                              setCaFile(index, path),
                            )
                          }
                        >
                          Browse…
                        </button>
                      ) : null}
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
                  ))
                )}
              </div>

              <div className="ac-group">
                <h3 className="ac-group__title">Client certificate</h3>
                <p className="ac-hint">
                  Sent when a server asks for one. A PEM holding the certificate and key, or a
                  PKCS#12 <code>.p12</code>/<code>.pfx</code> bundle, which is unpacked for you.
                </p>

                <div className="ac-field ac-field--path">
                  <label htmlFor="ac-client-cert">File</label>
                  <input
                    id="ac-client-cert"
                    type="text"
                    placeholder="/path/to/client.p12"
                    spellCheck={false}
                    value={identity?.path ?? ""}
                    onChange={(event) => setIdentityPath(event.target.value)}
                  />
                  {onBrowse ? (
                    <button
                      type="button"
                      className="ac-button ac-button--quiet"
                      onClick={() =>
                        void browseFor("Client certificate", IDENTITY_FILTERS, setIdentityPath)
                      }
                    >
                      Browse…
                    </button>
                  ) : null}
                </div>

                {identity ? (
                  <div className="ac-field">
                    <label htmlFor="ac-client-pass">Password</label>
                    <SecretInput
                      id="ac-client-pass"
                      name="certificate password"
                      placeholder="Required for .p12 / .pfx"
                      value={identity.password ?? ""}
                      onChange={(password) =>
                        patchTls({ clientIdentity: { path: identity.path, password } })
                      }
                    />
                  </div>
                ) : null}
              </div>

              <label className="ac-field ac-field--toggle ac-field--danger ac-danger-box">
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
              <img
                className="ac-about__logo"
                src={logo}
                alt=""
                width="96"
                height="96"
                draggable={false}
              />

              <h3 className="ac-about__name">API Client</h3>
              <p className="ac-about__version">{version || "unknown version"}</p>

              <p className="ac-about__tagline">A no-thrills, local-first HTTP client.</p>
              <p className="ac-about__tagline ac-about__tagline--minor">
                No cloud, no accounts, no workspaces. Just an API tester.
              </p>

              {storagePath ? (
                <p className="ac-about__storage">
                  State is kept in <code>{storagePath}</code>
                </p>
              ) : null}

              <p className="ac-about__licence">
                MIT licensed. IBM Plex Sans and Mono under the SIL Open Font License 1.1.
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
              // The text size is not in the draft; it was kept as it was picked.
              onSave({ ...draft, fontScale: settings.fontScale });
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
