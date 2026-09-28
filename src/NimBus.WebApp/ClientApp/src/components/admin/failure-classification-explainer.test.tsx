import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import FailureClassificationExplainer from "./failure-classification-explainer";
import type { GuidanceThresholds } from "lib/failure-classification";

afterEach(cleanup);

function Harness({ onChange }: { onChange?: (key: string, value: number) => void }) {
  const [thresholds, setThresholds] = useState<GuidanceThresholds>({ minimumCategoryConfidence: 0.6, retryLikely: 0.75, changeRequired: 0.75 });
  return <FailureClassificationExplainer thresholds={thresholds} onChange={(key, value) => { onChange?.(key, value); setThresholds(current => ({ ...current, [key]: value })); }} />;
}

const matchedRule = () => within(screen.getByRole("list", { name: "Guidance rules" })).getAllByRole("listitem")
  .find(item => item.getAttribute("aria-current") === "step");

describe("FailureClassificationExplainer", () => {
  it("evaluates the guidance rules top-down for each sample", async () => {
    render(<Harness />);
    expect(matchedRule()?.textContent).toContain("Otherwise");
    await userEvent.click(screen.getByRole("button", { name: "Downstream timeout" }));
    expect(matchedRule()?.textContent).toContain("RetryMayHelp");
    await userEvent.click(screen.getByRole("button", { name: "Missing property" }));
    expect(matchedRule()?.textContent).toContain("ChangeLikelyRequired");
    await userEvent.click(screen.getByRole("button", { name: "Vague exception" }));
    expect(matchedRule()?.textContent).toContain("Uncertain");
  });

  it("re-evaluates the sample when a threshold changes and reports the edit", () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    fireEvent.change(screen.getByLabelText(/Retry-likely threshold/), { target: { value: "0.7" } });
    expect(onChange).toHaveBeenCalledWith("retryLikely", 0.7);
    expect(matchedRule()?.textContent).toContain("RetryMayHelp");
    expect(screen.getByRole("status", { name: "Sample guidance" }).textContent).toContain("RetryMayHelp");
  });
});
