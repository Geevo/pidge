import type {
  ApiKeyPlacement,
  AuthConfig,
  HttpRequest,
  OAuth1Signature,
  OAuth2ClientAuth,
  OAuth2Grant,
} from "../types";
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
  { value: "oauth1", label: "OAuth 1" },
  { value: "oauth2", label: "OAuth 2" },
];

const SIGNATURES = [
  { value: "hmacSha1", label: "HMAC-SHA1" },
  { value: "hmacSha256", label: "HMAC-SHA256" },
  { value: "plaintext", label: "PLAINTEXT" },
];

const GRANTS = [
  { value: "clientCredentials", label: "Client credentials" },
  { value: "password", label: "Password" },
  { value: "refreshToken", label: "Refresh token" },
];

const CLIENT_AUTH = [
  { value: "basicHeader", label: "Basic header" },
  { value: "requestBody", label: "Request body" },
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
    case "oauth1":
      return {
        type: "oauth1",
        consumerKey: "",
        consumerSecret: "",
        token: "",
        tokenSecret: "",
        signatureMethod: "hmacSha1",
        realm: "",
      };
    case "oauth2":
      return {
        type: "oauth2",
        grant: "clientCredentials",
        tokenUrl: "",
        clientId: "",
        clientSecret: "",
        scope: "",
        username: "",
        password: "",
        refreshToken: "",
        clientAuth: "basicHeader",
      };
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

      {auth.type === "oauth1" ? (
        <>
          <div className="ac-field">
            <label htmlFor="ac-auth-consumer-key">Consumer key</label>
            <input
              id="ac-auth-consumer-key"
              type="text"
              spellCheck={false}
              value={auth.consumerKey}
              onChange={(event) => setAuth({ ...auth, consumerKey: event.target.value })}
            />
          </div>
          <div className="ac-field">
            <label htmlFor="ac-auth-consumer-secret">Consumer secret</label>
            <input
              id="ac-auth-consumer-secret"
              type="password"
              value={auth.consumerSecret}
              onChange={(event) => setAuth({ ...auth, consumerSecret: event.target.value })}
            />
          </div>
          <div className="ac-field">
            <label htmlFor="ac-auth-oauth-token">Token</label>
            <input
              id="ac-auth-oauth-token"
              type="text"
              spellCheck={false}
              placeholder="Optional, for two-legged requests"
              value={auth.token}
              onChange={(event) => setAuth({ ...auth, token: event.target.value })}
            />
          </div>
          <div className="ac-field">
            <label htmlFor="ac-auth-token-secret">Token secret</label>
            <input
              id="ac-auth-token-secret"
              type="password"
              value={auth.tokenSecret}
              onChange={(event) => setAuth({ ...auth, tokenSecret: event.target.value })}
            />
          </div>
          <div className="ac-field">
            <label id="ac-auth-signature-label" htmlFor="ac-auth-signature">
              Signature
            </label>
            <Select
              id="ac-auth-signature"
              labelledBy="ac-auth-signature-label"
              value={auth.signatureMethod}
              options={SIGNATURES}
              onChange={(value) => setAuth({ ...auth, signatureMethod: value as OAuth1Signature })}
            />
          </div>
          <div className="ac-field">
            <label htmlFor="ac-auth-realm">Realm</label>
            <input
              id="ac-auth-realm"
              type="text"
              spellCheck={false}
              placeholder="Optional"
              value={auth.realm}
              onChange={(event) => setAuth({ ...auth, realm: event.target.value })}
            />
          </div>
          {auth.signatureMethod === "plaintext" ? (
            <p className="ac-hint">
              PLAINTEXT sends both secrets as the signature. Only over HTTPS.
            </p>
          ) : (
            <p className="ac-hint">
              Each request is signed over its own method, URL and parameters, including a
              form-encoded body.
            </p>
          )}
        </>
      ) : null}

      {auth.type === "oauth2" ? (
        <>
          <div className="ac-field">
            <label id="ac-auth-grant-label" htmlFor="ac-auth-grant">
              Grant
            </label>
            <Select
              id="ac-auth-grant"
              labelledBy="ac-auth-grant-label"
              value={auth.grant}
              options={GRANTS}
              onChange={(value) => setAuth({ ...auth, grant: value as OAuth2Grant })}
            />
          </div>

          <div className="ac-field">
            <label htmlFor="ac-auth-token-url">Token URL</label>
            <input
              id="ac-auth-token-url"
              type="text"
              spellCheck={false}
              placeholder="https://id.example.com/oauth/token"
              value={auth.tokenUrl}
              onChange={(event) => setAuth({ ...auth, tokenUrl: event.target.value })}
            />
          </div>

          <div className="ac-field">
            <label htmlFor="ac-auth-client-id">Client ID</label>
            <input
              id="ac-auth-client-id"
              type="text"
              spellCheck={false}
              value={auth.clientId}
              onChange={(event) => setAuth({ ...auth, clientId: event.target.value })}
            />
          </div>

          <div className="ac-field">
            <label htmlFor="ac-auth-client-secret">Client secret</label>
            <input
              id="ac-auth-client-secret"
              type="password"
              value={auth.clientSecret}
              onChange={(event) => setAuth({ ...auth, clientSecret: event.target.value })}
            />
          </div>

          {auth.grant === "password" ? (
            <>
              <div className="ac-field">
                <label htmlFor="ac-auth-owner">Username</label>
                <input
                  id="ac-auth-owner"
                  type="text"
                  spellCheck={false}
                  value={auth.username}
                  onChange={(event) => setAuth({ ...auth, username: event.target.value })}
                />
              </div>
              <div className="ac-field">
                <label htmlFor="ac-auth-owner-pass">Password</label>
                <input
                  id="ac-auth-owner-pass"
                  type="password"
                  value={auth.password}
                  onChange={(event) => setAuth({ ...auth, password: event.target.value })}
                />
              </div>
            </>
          ) : null}

          {auth.grant === "refreshToken" ? (
            <div className="ac-field">
              <label htmlFor="ac-auth-refresh">Refresh token</label>
              <input
                id="ac-auth-refresh"
                type="text"
                spellCheck={false}
                value={auth.refreshToken}
                onChange={(event) => setAuth({ ...auth, refreshToken: event.target.value })}
              />
            </div>
          ) : null}

          <div className="ac-field">
            <label htmlFor="ac-auth-scope">Scope</label>
            <input
              id="ac-auth-scope"
              type="text"
              spellCheck={false}
              placeholder="read:things write:things"
              value={auth.scope}
              onChange={(event) => setAuth({ ...auth, scope: event.target.value })}
            />
          </div>

          <div className="ac-field">
            <label id="ac-auth-client-auth-label" htmlFor="ac-auth-client-auth">
              Send client
            </label>
            <Select
              id="ac-auth-client-auth"
              labelledBy="ac-auth-client-auth-label"
              value={auth.clientAuth}
              options={CLIENT_AUTH}
              onChange={(value) => setAuth({ ...auth, clientAuth: value as OAuth2ClientAuth })}
            />
          </div>

          <p className="ac-hint">
            The token is fetched before the request and kept until it expires. Authorization code
            and the other browser flows are not here.
          </p>
        </>
      ) : null}

      {auth.type === "none" ? (
        <p className="ac-hint">No auth. Add an Authorization header directly if you need one.</p>
      ) : null}
    </div>
  );
}
