import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import ConfirmDestructiveAction from "./confirm-destructive-action";

afterEach(() => cleanup());

function renderDialog(props: Partial<Parameters<typeof ConfirmDestructiveAction>[0]> = {}) {
  const onConfirm = vi.fn();
  render(
    <ConfirmDestructiveAction
      isOpen
      onClose={vi.fn()}
      onConfirm={onConfirm}
      title="Bulk Resubmit Failed Messages"
      description="This will resubmit 23 messages."
      confirmText="ErpEndpoint"
      {...props}
    />,
  );
  return onConfirm;
}

describe("ConfirmDestructiveAction", () => {
  it("is a dialog named by its title", () => {
    renderDialog();

    expect(screen.getByRole("dialog", { name: "Bulk Resubmit Failed Messages" })).toBeTruthy();
  });

  it("confirms only once the typed text matches", () => {
    const onConfirm = renderDialog({ confirmLabel: "Resubmit" });
    const confirm = screen.getByRole("button", { name: "Resubmit" }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);

    fireEvent.change(screen.getByPlaceholderText("ErpEndpoint"), { target: { value: "erpendpoint" } });
    fireEvent.click(confirm);

    expect(onConfirm).toHaveBeenCalledTimes(1);
  });

  it("warns that a destructive action cannot be undone, in red", () => {
    renderDialog({ confirmLabel: "Delete" });

    expect(screen.getByText("This action cannot be undone.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Delete" }).className).toMatch(/red|danger/);
  });

  // Spec 038 §6.4: Recovery operations replay messages; they are confirmed but not "destructive".
  it("uses a primary button and no undo warning for a recovery operation", () => {
    renderDialog({ confirmLabel: "Resubmit", tone: "primary" });

    expect(screen.queryByText("This action cannot be undone.")).toBeNull();
    expect(screen.getByRole("button", { name: "Resubmit" }).className).not.toMatch(/red|danger/);
  });
});
