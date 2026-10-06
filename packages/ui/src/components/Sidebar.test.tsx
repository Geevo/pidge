import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { blankRequest } from "../state/factories";
import { historyEntry, response } from "../test/fakeBridge";
import type { SavedRequest } from "../types";
import { Sidebar, type SidebarTab } from "./Sidebar";

const SAVED: SavedRequest = {
  id: "saved-1",
  name: "List users",
  request: { ...blankRequest(), url: "https://api.example.com/users" },
  createdAt: 0,
  updatedAt: 0,
};

function setup(tab: SidebarTab) {
  const props = {
    history: [historyEntry(response({ finalUrl: "https://api.example.com/orders" }))],
    savedRequests: [SAVED],
    tab,
    onTabChange: vi.fn(),
    onNewRequest: vi.fn(),
    onOpenHistory: vi.fn(),
    onOpenSaved: vi.fn(),
    rowAttributes: (kind: SidebarTab, id: string) => ({ "data-row": `${kind}:${id}` }),
  };
  render(<Sidebar {...props} />);
  return { props, user: userEvent.setup() };
}

describe("Sidebar", () => {
  it("shows the chosen tab's list and opens a row", async () => {
    const { props, user } = setup("history");

    expect(screen.getByRole("tab", { name: "History" })).toHaveAttribute("aria-selected", "true");
    const panel = screen.getByRole("tabpanel");
    expect(within(panel).queryByText("List users")).not.toBeInTheDocument();

    await user.click(within(panel).getByRole("button", { name: /orders/ }));
    expect(props.onOpenHistory).toHaveBeenCalledWith(props.history[0]);
  });

  it("asks for the other tab and a new request", async () => {
    const { props, user } = setup("history");

    await user.click(screen.getByRole("tab", { name: "Saved" }));
    expect(props.onTabChange).toHaveBeenCalledWith("saved");

    await user.click(screen.getByRole("button", { name: "New Request" }));
    expect(props.onNewRequest).toHaveBeenCalled();
  });

  it("gives each row the host's attributes", async () => {
    const { props, user } = setup("saved");

    const row = screen.getByText("List users").closest("li");
    expect(row).toHaveAttribute("data-row", "saved:saved-1");

    await user.click(screen.getByRole("button", { name: /List users/ }));
    expect(props.onOpenSaved).toHaveBeenCalledWith(SAVED);
  });
});
