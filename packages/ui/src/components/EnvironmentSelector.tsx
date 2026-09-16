import type { Environment } from "../types";
import { Select } from "./Select";

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
  const options = [
    { value: "", label: "No environment" },
    ...environments.map((environment) => ({ value: environment.id, label: environment.name })),
    { value: "__manage__", label: "Manage…" },
  ];

  return (
    <Select
      className="ac-select--environment"
      label="Environment"
      value={activeEnvironmentId ?? ""}
      options={options}
      onChange={(value) => {
        if (value === "__manage__") {
          onManage();
          return;
        }
        onChange(value === "" ? null : value);
      }}
    />
  );
}
