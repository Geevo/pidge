import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { Select } from "./Select";

const OPTIONS = [
  { value: "none", label: "None" },
  { value: "bearer", label: "Bearer token" },
  { value: "basic", label: "Basic" },
];

function setup(value = "none") {
  const onChange = vi.fn();
  const user = userEvent.setup();
  render(<Select label="Auth" value={value} options={OPTIONS} onChange={onChange} />);
  return { onChange, user, trigger: screen.getByRole("combobox", { name: "Auth" }) };
}

describe("Select", () => {
  it("shows the label of the current value, not its value", () => {
    setup("bearer");
    expect(screen.getByRole("combobox", { name: "Auth" })).toHaveTextContent("Bearer token");
  });

  it("opens on click and reports the value that was chosen", async () => {
    const { onChange, user, trigger } = setup();

    expect(trigger).toHaveAttribute("aria-expanded", "false");
    await user.click(trigger);
    expect(trigger).toHaveAttribute("aria-expanded", "true");

    await user.click(screen.getByRole("option", { name: "Basic" }));

    expect(onChange).toHaveBeenCalledWith("basic");
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
  });

  it("marks the current option as selected", async () => {
    const { user, trigger } = setup("bearer");
    await user.click(trigger);

    expect(screen.getByRole("option", { name: "Bearer token" })).toHaveAttribute(
      "aria-selected",
      "true",
    );
    expect(screen.getByRole("option", { name: "Basic" })).toHaveAttribute("aria-selected", "false");
  });

  it("walks the list with the arrow keys and chooses with Enter", async () => {
    const { onChange, user, trigger } = setup();

    trigger.focus();
    await user.keyboard("{ArrowDown}");
    expect(screen.getByRole("listbox")).toBeInTheDocument();

    await user.keyboard("{ArrowDown}{Enter}");

    expect(onChange).toHaveBeenCalledWith("bearer");
  });

  it("goes to the ends with Home and End", async () => {
    const { onChange, user, trigger } = setup();

    await user.click(trigger);
    await user.keyboard("{End}{Enter}");

    expect(onChange).toHaveBeenCalledWith("basic");
  });

  it("closes on Escape without choosing, and hands the focus back", async () => {
    const { onChange, user, trigger } = setup();

    await user.click(trigger);
    await user.keyboard("{Escape}");

    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
    expect(trigger).toHaveFocus();
  });

  it("closes when something else is clicked", async () => {
    const { onChange, user, trigger } = setup();

    await user.click(trigger);
    await user.click(document.body);

    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("carries the value on each row, which is what colours the methods", async () => {
    const { user, trigger } = setup();
    await user.click(trigger);

    expect(screen.getByRole("option", { name: "Bearer token" })).toHaveAttribute(
      "data-value",
      "bearer",
    );
  });
});
