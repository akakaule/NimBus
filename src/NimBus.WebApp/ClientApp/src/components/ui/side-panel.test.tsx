import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { SidePanel } from "./side-panel";

afterEach(cleanup);

const Harness = ({
  open,
  onClose,
}: {
  open: boolean;
  onClose: () => void;
}) => (
  <>
    <button type="button">opener</button>
    <SidePanel isOpen={open} onClose={onClose} label="Details">
      <button type="button">first</button>
      <button type="button">last</button>
    </SidePanel>
  </>
);

describe("SidePanel", () => {
  it("focuses the dialog and returns focus when it closes", () => {
    const onClose = vi.fn();
    const { rerender } = render(<Harness open={false} onClose={onClose} />);
    screen.getByText("opener").focus();

    rerender(<Harness open onClose={onClose} />);
    expect(document.activeElement).toBe(
      screen.getByRole("dialog", { name: "Details" }),
    );

    rerender(<Harness open={false} onClose={onClose} />);
    expect(document.activeElement).toBe(screen.getByText("opener"));
  });

  it("closes on Escape", () => {
    const onClose = vi.fn();
    render(<Harness open onClose={onClose} />);

    fireEvent.keyDown(document, { key: "Escape" });

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("keeps Tab inside the panel", () => {
    render(<Harness open onClose={vi.fn()} />);
    const last = screen.getByText("last");
    last.focus();

    fireEvent.keyDown(document, { key: "Tab" });

    expect(document.activeElement).toBe(screen.getByText("first"));
  });
});
