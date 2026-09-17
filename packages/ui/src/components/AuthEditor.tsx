import type { ApiKeyPlacement, AuthConfig, HttpRequest } from "../types";
import { Select } from "./Select";

interface Props {
  request: HttpRequest;
  onChange: (request: HttpRequest) => void;
}

type AuthKind = AuthConfig["type"];

const AUTH_KINDS = [
  { value: "none", label: "None" },
  { value: "bearer", label: "Bearer token" },
  { value: "basic", label: "Basic" },
  { value: "digest", label: "Digest" },
  { value: "apiKey", label: "API key" },
];

const PLACEMENTS = [
  { value: "header", label: "Header" },
  { value: "query", label: "Query parameter" },
];

/** What each scheme starts as when it is picked. */
function blank(kind: AuthKind): AuthConfig {
  switch (kind) {
    case "bearer":
      return { type: "bearer", token: "" };
    case "basic":
      return { type: "basic", username: "", password: "" };
    case "digest":
      return { type: "digest", username: "", password: "" };
    case "apiKey":
      return { type: "apiKey", key: "", value: "", placement: "header" };
    case "none":
      return { type: "none" };
  }
}

export function AuthEditor({ request, onChange }: Props) {
  const auth = request.auth;
  const setAuth = (next: AuthConfig) => onChange({ ...request, auth: next });

  return (
    <div>
      <div className="ac-field">
        <label id="ac-auth-kind-label" htmlFor="ac-auth-kind">
          Auth
        </label>
        <Select
          id="ac-auth-kind"
          labelledBy="ac-auth-kind-label"
          value={auth.type}
          options={AUTH_KINDS}
          onChange={(value) => {
            const kind = value as AuthKind;
            if (kind !== auth.type) setAuth(blank(kind));
          }}
        />
      </div>

      {auth.type === "bearer" ? (
        <div className="ac-field">
          <label htmlFor="ac-auth-token">Token</label>
          <input
            id="ac-auth-token"
            type="text"
            spellCheck={false}
            placeholder="{{token}}"
            value={auth.token}
            onChange={(event) => setAuth({ ...auth, token: event.target.value })}
          />
        </div>
      ) : null}

      {auth.type === "basic" || auth.type === "digest" ? (
        <>
          <div className="ac-field">
            <label htmlFor="ac-auth-user">Username</label>
            <input
              id="ac-auth-user"
              type="text"
              spellCheck={false}
              value={auth.username}
              onChange={(event) => setAuth({ ...auth, username: event.target.value })}
            />
          </div>
          <div className="ac-field">
            <label htmlFor="ac-auth-pass">Password</label>
            <input
              id="ac-auth-pass"
              type="password"
              value={auth.password}
              onChange={(event) => setAuth({ ...auth, password: event.target.value })}
            />
          </div>
          {auth.type === "digest" ? (
            <p className="ac-hint">
              Nothing is sent until the server asks for it: the first request comes back 401 with a
              challenge, and the credentials go with the second.
            </p>
          ) : null}
        </>
      ) : null}

      {auth.type === "apiKey" ? (
        <>
          <div className="ac-field">
            <label htmlFor="ac-auth-key">Key</label>
            <input
              id="ac-auth-key"
              type="text"
              spellCheck={false}
              placeholder="X-API-Key"
              value={auth.key}
              onChange={(event) => setAuth({ ...auth, key: event.target.value })}
            />
          </div>
          <div className="ac-field">
            <label htmlFor="ac-auth-key-value">Value</label>
            <input
              id="ac-auth-key-value"
              type="text"
              spellCheck={false}
              placeholder="{{apiKey}}"
              value={auth.value}
              onChange={(event) => setAuth({ ...auth, value: event.target.value })}
            />
          </div>
          <div className="ac-field">
            <label id="ac-auth-key-in-label" htmlFor="ac-auth-key-in">
              Send in
            </label>
            <Select
              id="ac-auth-key-in"
              labelledBy="ac-auth-key-in-label"
              value={auth.placement}
              options={PLACEMENTS}
              onChange={(value) => setAuth({ ...auth, placement: value as ApiKeyPlacement })}
            />
          </div>
          {auth.placement === "query" ? (
            <p className="ac-hint">
              A key in the query string ends up in server logs and browser history. A header is
              safer where the API accepts one.
            </p>
          ) : null}
        </>
      ) : null}

      {auth.type === "none" ? (
        <p className="ac-hint">No auth. Add an Authorization header directly if you need one.</p>
      ) : null}
    </div>
  );
}
