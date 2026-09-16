import type { AuthConfig, HttpRequest } from "../types";
import { Select } from "./Select";

interface Props {
  request: HttpRequest;
  onChange: (request: HttpRequest) => void;
}

type AuthKind = AuthConfig["type"];

/** None, Bearer, Basic. Anything else is a header you type yourself. */
const AUTH_KINDS = [
  { value: "none", label: "None" },
  { value: "bearer", label: "Bearer token" },
  { value: "basic", label: "Basic" },
];

export function AuthEditor({ request, onChange }: Props) {
  const setAuth = (auth: AuthConfig) => onChange({ ...request, auth });

  const changeKind = (kind: AuthKind) => {
    if (kind === request.auth.type) return;
    if (kind === "none") setAuth({ type: "none" });
    else if (kind === "bearer") setAuth({ type: "bearer", token: "" });
    else setAuth({ type: "basic", username: "", password: "" });
  };

  return (
    <div>
      <div className="ac-field">
        <label id="ac-auth-kind-label" htmlFor="ac-auth-kind">
          Auth
        </label>
        <Select
          id="ac-auth-kind"
          labelledBy="ac-auth-kind-label"
          value={request.auth.type}
          options={AUTH_KINDS}
          onChange={(value) => changeKind(value as AuthKind)}
        />
      </div>

      {request.auth.type === "bearer" ? (
        <div className="ac-field">
          <label htmlFor="ac-auth-token">Token</label>
          <input
            id="ac-auth-token"
            type="text"
            spellCheck={false}
            placeholder="{{token}}"
            value={request.auth.token}
            onChange={(event) => setAuth({ type: "bearer", token: event.target.value })}
          />
        </div>
      ) : null}

      {request.auth.type === "basic" ? (
        <>
          <div className="ac-field">
            <label htmlFor="ac-auth-user">Username</label>
            <input
              id="ac-auth-user"
              type="text"
              spellCheck={false}
              value={request.auth.username}
              onChange={(event) =>
                setAuth({
                  type: "basic",
                  username: event.target.value,
                  password: request.auth.type === "basic" ? request.auth.password : "",
                })
              }
            />
          </div>
          <div className="ac-field">
            <label htmlFor="ac-auth-pass">Password</label>
            <input
              id="ac-auth-pass"
              type="password"
              value={request.auth.password}
              onChange={(event) =>
                setAuth({
                  type: "basic",
                  username: request.auth.type === "basic" ? request.auth.username : "",
                  password: event.target.value,
                })
              }
            />
          </div>
        </>
      ) : null}

      {request.auth.type === "none" ? (
        <p className="ac-hint">No auth. Add an Authorization header directly if you need one.</p>
      ) : null}
    </div>
  );
}
