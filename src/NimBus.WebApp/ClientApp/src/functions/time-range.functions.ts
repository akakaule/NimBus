/**
 * Longest custom time range. Mirrors the server's MaxWindow (FailedImplementation, also used
 * by MetricsImplementation).
 */
export const MAX_CUSTOM_RANGE_DAYS = 90;

/** A custom time range; `to` is exclusive. */
export interface TimeRange {
  from: Date;
  to: Date;
}

/** Why the server would refuse a custom range, or undefined when it is valid. */
export function customRangeError(from: Date, to: Date): string | undefined {
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()))
    return "Enter a start and an end time.";
  if (from >= to) return "The start must be before the end.";
  if (to.getTime() - from.getTime() > MAX_CUSTOM_RANGE_DAYS * 86_400_000)
    return `A custom range may span at most ${MAX_CUSTOM_RANGE_DAYS} days.`;
  return undefined;
}
