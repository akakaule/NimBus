import { describe, expect, it } from "vitest";
import { composeGuidance, FAILURE_CATEGORIES, SAMPLE_FAILURES } from "./failure-classification";

const defaults = { minimumCategoryConfidence: 0.6, retryLikely: 0.75, changeRequired: 0.75 };
const signals = { category: "transient_dependency", categoryConfidence: 0.9, retryLikelihood: 0.9, changeRequiredLikelihood: 0.9 };

describe("composeGuidance", () => {
  it("returns Uncertain below the minimum category confidence, before any other rule", () => {
    expect(composeGuidance({ ...signals, categoryConfidence: 0.59 }, defaults)).toEqual({ rule: 0, guidance: "Uncertain" });
  });

  it("returns RetryMayHelp only for a transient dependency at or above the retry threshold", () => {
    expect(composeGuidance({ ...signals, retryLikelihood: 0.75 }, defaults).guidance).toBe("RetryMayHelp");
    expect(composeGuidance({ ...signals, category: "business_rule" }, defaults).guidance).toBe("ChangeLikelyRequired");
  });

  it("returns ChangeLikelyRequired at or above the change threshold", () => {
    expect(composeGuidance({ ...signals, retryLikelihood: 0.74, changeRequiredLikelihood: 0.75 }, defaults))
      .toEqual({ rule: 2, guidance: "ChangeLikelyRequired" });
  });

  it("falls back to Investigate", () => {
    expect(composeGuidance({ ...signals, retryLikelihood: 0.74, changeRequiredLikelihood: 0.74 }, defaults))
      .toEqual({ rule: 3, guidance: "Investigate" });
  });

  it("gives the samples one of each guidance with default thresholds", () => {
    const outcomes = SAMPLE_FAILURES.map(sample => composeGuidance(sample, defaults).guidance);
    expect(outcomes).toEqual(["Investigate", "RetryMayHelp", "ChangeLikelyRequired", "Uncertain"]);
  });

  it("keeps sample distributions valid for question set v1", () => {
    for (const sample of SAMPLE_FAILURES) {
      expect(Object.keys(sample.categoryProbabilities).sort()).toEqual(FAILURE_CATEGORIES.map(item => item.id).sort());
      expect(Object.values(sample.categoryProbabilities).reduce((sum, value) => sum + value, 0)).toBeCloseTo(1, 3);
      expect(sample.categoryConfidence).toBe(Math.max(...Object.values(sample.categoryProbabilities)));
    }
  });
});
