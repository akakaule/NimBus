import { execFile } from "node:child_process";
import { promisify } from "node:util";

const execute = promisify(execFile);

/** Read real dispatcher failures without changing the receiving application. */
export async function outboxFailureLog(session: string): Promise<string | null> {
  const appHost = process.env.E2E_APPHOST;
  if (!appHost) throw new Error("Run via npm run test:live to discover the outbox resource.");
  const { stdout } = await execute(process.platform === "win32" ? "aspire.exe" : "aspire",
    ["logs", "erp-api", "--apphost", appHost, "--search", session, "--format", "Json", "--non-interactive"],
    { timeout: 30_000, maxBuffer: 4 * 1024 * 1024, windowsHide: true });
  return stdout.includes("Outbox dispatch failed for message") && stdout.includes(session) ? stdout : null;
}

/** Only the two demo adapters can be interrupted, and only with an explicit AppHost. */
export async function adapterCommand(resource: "crm-adapter" | "erp-adapter", command: "stop" | "start"): Promise<void> {
  const appHost = process.env.E2E_APPHOST;
  if (!appHost?.replaceAll("\\", "/").endsWith("/CrmErpDemo.AppHost/CrmErpDemo.AppHost.csproj"))
    throw new Error("Run via npm run test:live to select the CRM/ERP AppHost explicitly.");
  const aspire = process.platform === "win32" ? "aspire.exe" : "aspire";
  await execute(aspire, ["resource", resource, command, "--apphost", appHost, "--non-interactive"], { timeout: 120_000, windowsHide: true });
  if (command === "start") await execute(aspire, ["wait", resource, "--apphost", appHost, "--timeout", "120", "--non-interactive"],
    { timeout: 130_000, windowsHide: true });
}
