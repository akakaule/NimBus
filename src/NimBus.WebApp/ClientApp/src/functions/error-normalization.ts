const GUID_PATTERN =
  /[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/g;
const ACTION_SUFFIX = /\.?\s*Action:.*$/;

export function normalizeErrorPattern(
  errorText: string | undefined | null,
): string {
  if (!errorText) return "Unknown";
  let normalized = errorText.replace(GUID_PATTERN, "<id>");
  normalized = normalized.replace(ACTION_SUFFIX, "");
  return normalized.replace(/[\s.]+$/, "");
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
