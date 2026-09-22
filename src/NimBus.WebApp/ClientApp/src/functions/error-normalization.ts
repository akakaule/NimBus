// Mirrors NimBus.WebApp.Services.ErrorPatternNormalizer — keep the two in sync
// so the grouped view and the metrics insights agree on what an error pattern is.
const TIMESTAMP_PATTERN =
  /\b\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\b/g;
const GUID_PATTERN =
  /[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/g;
const DIMENSION_VALUE_PATTERN = /\bdimension value\s+['"]?[^'",.;:\s]+['"]?/gi;
const KEY_VALUE_PATTERN = /\bkey\s+['"][^'"]+['"]/gi;
const JOB_ID_PATTERN = /\bJobID\s+\[[^\]]+\]/gi;
const QUOTED_IDENTIFIER_PATTERN = /(['"])[A-Za-z0-9][A-Za-z0-9_.:-]{5,}\1/g;
const LONG_NUMBER_PATTERN = /\b\d{4,}\b/g;
const ACTION_SUFFIX = /\.?\s*Action:.*$/;
// A short unquoted value appended after the last ": " (e.g. "... update the
// existing contact: james.burton"). Only applies when an earlier ": " exists,
// so the reason after a single category colon ("Order rejected: timeout") is kept.
const TRAILING_VALUE_PATTERN = /^(.*:\s.*:\s+)[^:\s]+(?:\s+[^:\s]+){0,2}$/;

export function normalizeErrorPattern(
  errorText: string | undefined | null,
): string {
  if (!errorText) return "Unknown";
  let normalized = errorText.replace(TIMESTAMP_PATTERN, "<timestamp>");
  normalized = normalized.replace(GUID_PATTERN, "<id>");
  normalized = normalized.replace(
    DIMENSION_VALUE_PATTERN,
    "dimension value <value>",
  );
  normalized = normalized.replace(KEY_VALUE_PATTERN, "key '<value>'");
  normalized = normalized.replace(JOB_ID_PATTERN, "JobID [<id>]");
  normalized = normalized.replace(QUOTED_IDENTIFIER_PATTERN, "$1<value>$1");
  normalized = normalized.replace(LONG_NUMBER_PATTERN, "<number>");
  normalized = normalized.replace(ACTION_SUFFIX, "").replace(/[ .]+$/, "");
  return normalized.replace(TRAILING_VALUE_PATTERN, "$1<value>");
}

export function extractErrorCategory(
  errorText: string | undefined | null,
): string {
  if (!errorText) return "Unknown";
  // Strip GUIDs first so messages that differ only by an embedded id
  // (e.g. "Job with JobID {GUID} not found") collapse into one category
  // instead of one row per id.
  const text = errorText.replace(GUID_PATTERN, "<id>");
  if (text.startsWith("[")) {
    const end = text.indexOf("]");
    if (end > 0) return text.substring(0, end + 1);
  }
  const colon = text.indexOf(":");
  if (colon > 0 && colon < 100) return text.substring(0, colon);
  return text.length > 100 ? text.substring(0, 100) : text;
}
