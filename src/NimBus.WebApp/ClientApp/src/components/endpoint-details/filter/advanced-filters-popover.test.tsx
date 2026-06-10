import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import AdvancedFiltersPopover, {
  AdvancedFilterChips,
} from "./advanced-filters-popover";
import {
  EMPTY_ADVANCED_FILTERS,
  type AdvancedFilters,
} from "./advanced-filters";

afterEach(cleanup);

const applied: AdvancedFilters = {
  ...EMPTY_ADVANCED_FILTERS,
  updatedFrom: "2026-06-01T10:00",
  payload: "orderId:4471",
};

describe("AdvancedFiltersPopover (trigger + popover)", () => {
  it("shows a count badge for the active filters", () => {
    render(<AdvancedFiltersPopover value={applied} onApply={vi.fn()} />);
    // Trigger button name includes the count badge ("2" active fields).
    expect(
      screen.getByRole("button", { name: /advanced filters\s*2/i }),
    ).toBeTruthy();
  });

  it("opens the popover and Apply commits the edited values then closes", async () => {
    const onApply = vi.fn();
    render(
      <AdvancedFiltersPopover value={EMPTY_ADVANCED_FILTERS} onApply={onApply} />,
    );

    await userEvent.click(
      screen.getByRole("button", { name: /advanced filters/i }),
    );
    const dialog = screen.getByRole("dialog", { name: /advanced filters/i });
    await userEvent.type(
      within(dialog).getByLabelText("Payload contains"),
      "abc",
    );
    await userEvent.click(within(dialog).getByRole("button", { name: /apply/i }));

    expect(onApply).toHaveBeenCalledWith({
      ...EMPTY_ADVANCED_FILTERS,
      payload: "abc",
    });
    expect(
      screen.queryByRole("dialog", { name: /advanced filters/i }),
    ).toBeNull();
  });

  it("Cancel closes the popover without applying", async () => {
    const onApply = vi.fn();
    render(
      <AdvancedFiltersPopover value={EMPTY_ADVANCED_FILTERS} onApply={onApply} />,
    );
    await userEvent.click(
      screen.getByRole("button", { name: /advanced filters/i }),
    );
    await userEvent.type(screen.getByLabelText("Payload contains"), "abc");
    await userEvent.click(screen.getByRole("button", { name: /cancel/i }));

    expect(onApply).not.toHaveBeenCalled();
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("AdvancedFilterChips", () => {
  it("renders nothing when no advanced filter is active", () => {
    const { container } = render(
      <AdvancedFilterChips value={EMPTY_ADVANCED_FILTERS} onApply={vi.fn()} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it("renders a removable chip per active filter, plus Clear all", () => {
    render(<AdvancedFilterChips value={applied} onApply={vi.fn()} />);
    expect(
      screen.getByRole("button", { name: /remove updated from/i }),
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: /remove payload/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /clear all/i })).toBeTruthy();
  });

  it("removing a chip applies the value with only that field cleared", async () => {
    const onApply = vi.fn();
    render(<AdvancedFilterChips value={applied} onApply={onApply} />);
    await userEvent.click(
      screen.getByRole("button", { name: /remove payload/i }),
    );
    expect(onApply).toHaveBeenCalledWith({ ...applied, payload: "" });
  });

  it("Clear all applies an empty filter set", async () => {
    const onApply = vi.fn();
    render(<AdvancedFilterChips value={applied} onApply={onApply} />);
    await userEvent.click(screen.getByRole("button", { name: /clear all/i }));
    expect(onApply).toHaveBeenCalledWith(EMPTY_ADVANCED_FILTERS);
  });

  it("omits Clear all when only one filter is active", () => {
    const one: AdvancedFilters = {
      ...EMPTY_ADVANCED_FILTERS,
      payload: "abc",
    };
    render(<AdvancedFilterChips value={one} onApply={vi.fn()} />);
    expect(screen.getByRole("button", { name: /remove payload/i })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /clear all/i })).toBeNull();
  });
});
