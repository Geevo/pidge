import { useState } from "react";

import type { Environment } from "../types";
import { newId } from "../lib/ids";
import { KeyValueTable } from "./KeyValueTable";

interface Props {
  environments: readonly Environment[];
  activeEnvironmentId: string | null;
  onSave: (environments: Environment[], activeEnvironmentId: string | null) => void;
  onClose: () => void;
}

/**
 * Editing environments is a rare, deliberate act, so it gets a dialog rather
 * than permanent screen space next to the request.
 */
export function EnvironmentsDialog({ environments, activeEnvironmentId, onSave, onClose }: Props) {
  const [draft, setDraft] = useState<Environment[]>(() => environments.map((e) => ({ ...e })));
  const [selectedId, setSelectedId] = useState<string | null>(
    activeEnvironmentId ?? environments[0]?.id ?? null,
  );

  const selected = draft.find((environment) => environment.id === selectedId) ?? null;

  const update = (id: string, patch: Partial<Environment>) => {
    setDraft((current) =>
      current.map((environment) =>
        environment.id === id ? { ...environment, ...patch } : environment,
      ),
    );
  };

  const add = () => {
    const environment: Environment = {
      id: newId(),
      name: `Environment ${draft.length + 1}`,
      variables: [],
    };
    setDraft((current) => [...current, environment]);
    setSelectedId(environment.id);
  };

  return (
    <div className="ac-dialog-backdrop" role="presentation" onClick={onClose}>
      <div
        className="ac-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Environments"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="ac-drawer__header">
          <span>Environments</span>
          <button type="button" className="ac-button ac-button--quiet" onClick={add}>
            New
          </button>
        </div>

        <div className="ac-dialog__body">
          {draft.length === 0 ? (
            <p className="ac-hint">
              No environments yet. Add one to use <code>{"{{variables}}"}</code> in requests.
            </p>
          ) : (
            <>
              <div className="ac-field">
                <label htmlFor="ac-env-pick">Editing</label>
                <select
                  id="ac-env-pick"
                  value={selectedId ?? ""}
                  onChange={(event) => setSelectedId(event.target.value)}
                >
                  {draft.map((environment) => (
                    <option key={environment.id} value={environment.id}>
                      {environment.name}
                    </option>
                  ))}
                </select>
                {selected ? (
                  <button
                    type="button"
                    className="ac-button ac-button--quiet ac-button--danger"
                    onClick={() => {
                      setDraft((current) =>
                        current.filter((environment) => environment.id !== selected.id),
                      );
                      setSelectedId(null);
                    }}
                  >
                    Delete
                  </button>
                ) : null}
              </div>

              {selected ? (
                <>
                  <div className="ac-field">
                    <label htmlFor="ac-env-name">Name</label>
                    <input
                      id="ac-env-name"
                      type="text"
                      value={selected.name}
                      onChange={(event) => update(selected.id, { name: event.target.value })}
                    />
                  </div>
                  <KeyValueTable
                    label="Variables"
                    rows={selected.variables}
                    nameLabel="Variable"
                    namePlaceholder="baseUrl"
                    valuePlaceholder="http://localhost:3000"
                    onChange={(variables) => update(selected.id, { variables })}
                  />
                </>
              ) : null}
            </>
          )}
        </div>

        <div className="ac-dialog__footer">
          <button type="button" className="ac-button" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="ac-button ac-button--primary"
            onClick={() => {
              const stillExists = draft.some(
                (environment) => environment.id === activeEnvironmentId,
              );
              onSave(draft, stillExists ? activeEnvironmentId : (selectedId ?? null));
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
