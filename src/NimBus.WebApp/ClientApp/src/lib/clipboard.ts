/**
 * Thin wrapper around `navigator.clipboard.writeText` so callers can test
 * copy behavior by mocking this module instead of patching jsdom's
 * `Navigator.prototype.clipboard` getter.
 */
export function copyToClipboard(text: string): Promise<void> {
  return navigator.clipboard.writeText(text);
}
