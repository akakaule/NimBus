// Polling helper used wherever the test needs to wait on cross-system effects:
// e.g. "wait for ERP to receive the CrmAccountCreated event and create a Customer".

export interface WaitForOptions {
  timeoutMs?: number;
  intervalMs?: number;
  description?: string;
}

export async function waitFor<T>(
  predicate: () => Promise<T | null | undefined | false>,
  options: WaitForOptions = {},
): Promise<T> {
  const timeoutMs = options.timeoutMs ?? 30_000;
  const intervalMs = options.intervalMs ?? 1_000;
  const description = options.description ?? "condition";
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown = null;

  while (Date.now() < deadline) {
    try {
      const result = await predicate();
      if (result) return result as T;
    } catch (err) {
      lastError = err;
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }

  const detail = lastError instanceof Error ? `; last error: ${lastError.message}` : "";
  throw new Error(`Timed out after ${timeoutMs}ms waiting for ${description}${detail}`);
}
