import { useState } from "react";

import type { Environment } from "../types";
import { newId } from "../lib/ids";
import { KeyValueTable } from "./KeyValueTable";
import { PlusIcon, TrashIcon } from "./icons";
import { Select } from "./Select";

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
        <div className="ac-dialog__header">
          <h2 className="ac-dialog__title">Environments</h2>
          <button type="button" className="ac-button ac-button--icon" onClick={add}>
            <PlusIcon size={13} />
            New environment
          </button>
        </div>

        <div className="ac-dialog__body">
          {draft.length === 0 ? (
            <div className="ac-empty ac-empty--dialog">
              <p>
                No environments yet. Add one to use <code>{"{{variables}}"}</code> in requests.
              </p>
              <button type="button" className="ac-button ac-button--primary" onClick={add}>
                New environment
              </button>
            </div>
          ) : (
            <>
              <div className="ac-field">
                <label id="ac-env-pick-label" htmlFor="ac-env-pick">
                  Editing
                </label>
                <Select
                  id="ac-env-pick"
                  labelledBy="ac-env-pick-label"
                  value={selectedId ?? ""}
                  options={draft.map((environment) => ({
                    value: environment.id,
                    label: environment.name,
                  }))}
                  onChange={setSelectedId}
                />
                {selected ? (
                  <button
                    type="button"
                    className="ac-button ac-button--icon ac-button--danger"
                    onClick={() => {
                      setDraft((current) =>
                        current.filter((environment) => environment.id !== selected.id),
                      );
                      setSelectedId(null);
                    }}
                  >
                    <TrashIcon size={13} />
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
