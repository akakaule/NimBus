import { expect, type Page } from "@playwright/test";
import type { NimBusEvent } from "./nimbus-api-client.js";

/** Open the real row action menu; never accidentally click a bulk action. */
export async function actOnEvent(page: Page, endpoint: string, event: NimBusEvent, action: "Resubmit" | "Skip"): Promise<void> {
  await page.goto(`/Endpoints/Details/${encodeURIComponent(endpoint)}?eventId=${encodeURIComponent(event.eventId)}`);
  const rows = page.locator("table:visible tbody tr:visible");
  const row = rows.filter({ hasText: event.eventId.substring(0, 8) });
  await expect(rows).toHaveCount(1);
  await expect(row).toHaveCount(1);
  await row.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: action, exact: true }).click({ timeout: 10_000 });
}
