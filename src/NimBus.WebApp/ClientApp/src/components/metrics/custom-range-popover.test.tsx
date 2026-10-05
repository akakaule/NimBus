import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import CustomRangePopover, { formatRange } from "./custom-range-popover";

afterEach(cleanup);

const shown = {
  from: new Date(2026, 8, 24, 10, 0),
  to: new Date(2026, 8, 25, 10, 0),
};

describe("CustomRangePopover", () => {
  it("starts from the range on screen and applies the local start and end", () => {
    const onApply = vi.fn();
    render(<CustomRangePopover shown={shown} onApply={onApply} />);

    fireEvent.click(screen.getByRole("button", { name: "Custom…" }));
    expect(
      (screen.getByLabelText("Start time (local)") as HTMLInputElement).value,
    ).toBe("2026-09-24T10:00");
    fireEvent.change(screen.getByLabelText("Start time (local)"), {
      target: { value: "2026-09-20T08:00" },
    });
    fireEvent.change(screen.getByLabelText("End time (local)"), {
      target: { value: "2026-09-21T17:30" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      from: new Date(2026, 8, 20, 8, 0),
      to: new Date(2026, 8, 21, 17, 30),
    });
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("refuses a range that ends before it starts", () => {
    const onApply = vi.fn();
    render(<CustomRangePopover shown={shown} onApply={onApply} />);

    fireEvent.click(screen.getByRole("button", { name: "Custom…" }));
    fireEvent.change(screen.getByLabelText("End time (local)"), {
      target: { value: "2020-01-01T00:00" },
    });

    expect(screen.getByRole("alert").textContent).toMatch(/before the end/);
    expect(
      (screen.getByRole("button", { name: "Apply" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });

  it("labels and reopens on an applied range", () => {
    const range = {
      from: new Date(2026, 8, 20, 8, 0),
      to: new Date(2026, 8, 21, 17, 30),
    };
    render(
      <CustomRangePopover range={range} shown={shown} onApply={vi.fn()} />,
    );

    const trigger = screen.getByRole("button", { name: formatRange(range) });
    expect(trigger.textContent).toBe("20/09 08:00–21/09 17:30");
    expect(trigger.getAttribute("aria-pressed")).toBe("true");
    fireEvent.click(trigger);
    expect(
      (screen.getByLabelText("Start time (local)") as HTMLInputElement).value,
    ).toBe("2026-09-20T08:00");
  });
});
