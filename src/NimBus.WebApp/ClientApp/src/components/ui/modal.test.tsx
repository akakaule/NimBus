import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { Modal } from "./modal";
import { SidePanel } from "./side-panel";

afterEach(() => {
  cleanup();
  document.body.style.overflow = "";
});

describe("Modal", () => {
  it("is named by its label", () => {
    render(
      <Modal isOpen onClose={vi.fn()} label="Discard changes?">
        body
      </Modal>,
    );

    expect(screen.getByRole("dialog", { name: "Discard changes?" })).toBeTruthy();
  });

  // A Modal opened from inside a SidePanel (MCP's turn-off confirm, Simulation's
  // ownership confirm) must take Escape for itself, not close the panel too.
  it("handles Escape before a SidePanel underneath sees it", () => {
    const closePanel = vi.fn();
    const closeModal = vi.fn();
    render(
      <SidePanel isOpen onClose={closePanel} label="Panel">
        <Modal isOpen onClose={closeModal} label="Confirm">
          body
        </Modal>
      </SidePanel>,
    );

    fireEvent.keyDown(document, { key: "Escape" });

    expect(closeModal).toHaveBeenCalledTimes(1);
    expect(closePanel).not.toHaveBeenCalled();
  });

  it("restores the page's scroll lock when it closes over a SidePanel", () => {
    const Harness = ({ modalOpen }: { modalOpen: boolean }) => (
      <SidePanel isOpen onClose={vi.fn()} label="Panel">
        <Modal isOpen={modalOpen} onClose={vi.fn()} label="Confirm">
          body
        </Modal>
      </SidePanel>
    );
    const { rerender } = render(<Harness modalOpen={false} />);
    expect(document.body.style.overflow).toBe("hidden");

    rerender(<Harness modalOpen />);
    rerender(<Harness modalOpen={false} />);

    expect(document.body.style.overflow).toBe("hidden");
  });
});
