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
  if (errorText.startsWith("[")) {
    const end = errorText.indexOf("]");
    if (end > 0) return errorText.substring(0, end + 1);
  }
  const colon = errorText.indexOf(":");
  if (colon > 0 && colon < 100) return errorText.substring(0, colon);
  return errorText.length > 100 ? errorText.substring(0, 100) : errorText;
}
