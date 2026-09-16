import type { Environment } from "../types";

interface Props {
  environments: readonly Environment[];
  activeEnvironmentId: string | null;
  onChange: (environmentId: string | null) => void;
  onManage: () => void;
}

/** Environments are a flat list of variable sets. Nothing owns them. */
export function EnvironmentSelector({
  environments,
  activeEnvironmentId,
  onChange,
  onManage,
}: Props) {
  return (
    <>
      <select
        className="ac-method"
        aria-label="Environment"
        style={{ fontWeight: 400 }}
        value={activeEnvironmentId ?? ""}
        onChange={(event) => {
          if (event.target.value === "__manage__") {
            onManage();
            return;
          }
          onChange(event.target.value === "" ? null : event.target.value);
        }}
      >
        <option value="">No environment</option>
        {environments.map((environment) => (
          <option key={environment.id} value={environment.id}>
            {environment.name}
          </option>
        ))}
        <option value="__manage__">Manage…</option>
      </select>
    </>
  );
}
