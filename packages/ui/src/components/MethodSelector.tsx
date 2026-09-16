import type { HttpMethod } from "../types";
import { HTTP_METHODS } from "../types";
import { Select } from "./Select";

interface Props {
  value: HttpMethod;
  onChange: (method: HttpMethod) => void;
}

const OPTIONS = HTTP_METHODS.map((method) => ({ value: method, label: method }));

export function MethodSelector({ value, onChange }: Props) {
  return (
    <Select
      className="ac-method"
      label="Method"
      value={value}
      options={OPTIONS}
      onChange={(method) => onChange(method as HttpMethod)}
    />
  );
}
