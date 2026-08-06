// Resolves the crm-web / erp-web URLs.
//
// Unlike crm-api (5080), erp-api (5090) and nimbus-ops (28376), the two Vite SPAs
// are registered with AddViteApp and get Aspire-assigned ports, so there is
// nothing stable to hard-code. Resolution order:
//   1. CRM_WEB_URL / ERP_WEB_URL environment variables (set these to skip probing)
//   2. a cached result from a previous run, re-validated
//   3. probe every listening TCP port and match on the SPA's <title>
//
// The titles are declared in each SPA's index.html and are distinct, which is
// what makes the probe unambiguous.

import { execSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { request, type APIRequestContext } from "@playwright/test";

const CACHE_FILE = path.resolve(process.cwd(), ".demo-web-urls.json");

const CRM_TITLE = "CRM Demo";
const ERP_TITLE = "ERP Demo";

export interface WebUrls {
  crmWeb: string;
  erpWeb: string;
}

let cached: WebUrls | null = null;

async function titleOf(ctx: APIRequestContext, port: number): Promise<string | null> {
  try {
    const res = await ctx.get(`http://127.0.0.1:${port}/`, { timeout: 2000 });
    if (!res.ok()) return null;
    const body = await res.text();
    return body.match(/<title>([^<]*)<\/title>/i)?.[1] ?? null;
  } catch {
    return null;
  }
}

function listeningPorts(): number[] {
  try {
    const out = execSync("netstat -ano -p tcp", { encoding: "utf8", windowsHide: true });
    const ports = new Set<number>();
    for (const line of out.split(/\r?\n/)) {
      if (!/LISTENING/i.test(line)) continue;
      const m = line.match(/:(\d+)\s+\S+\s+LISTENING/i);
      if (!m) continue;
      const port = Number(m[1]);
      if (port > 1024 && port < 65536) ports.add(port);
    }
    return [...ports].sort((a, b) => a - b);
  } catch {
    return [];
  }
}

/** Probe `ports` with bounded concurrency, returning the first port per wanted title. */
async function probeForTitles(ports: number[]): Promise<Map<string, number>> {
  const ctx = await request.newContext({ ignoreHTTPSErrors: true });
  const found = new Map<string, number>();
  const queue = [...ports];
  const CONCURRENCY = 24;

  async function worker(): Promise<void> {
    for (;;) {
      const port = queue.shift();
      if (port === undefined) return;
      if (found.size === 2) return;
      const title = await titleOf(ctx, port);
      if (!title) continue;
      if (title.includes(CRM_TITLE) && !found.has(CRM_TITLE)) found.set(CRM_TITLE, port);
      else if (title.includes(ERP_TITLE) && !found.has(ERP_TITLE)) found.set(ERP_TITLE, port);
    }
  }

  await Promise.all(Array.from({ length: CONCURRENCY }, () => worker()));
  await ctx.dispose();
  return found;
}

async function stillServes(url: string, expectedTitle: string): Promise<boolean> {
  const ctx = await request.newContext({ ignoreHTTPSErrors: true });
  try {
    const port = Number(new URL(url).port);
    const title = await titleOf(ctx, port);
    return !!title && title.includes(expectedTitle);
  } catch {
    return false;
  } finally {
    await ctx.dispose();
  }
}

export async function resolveWebUrls(): Promise<WebUrls> {
  if (cached) return cached;

  const fromEnv = process.env.CRM_WEB_URL && process.env.ERP_WEB_URL
    ? { crmWeb: process.env.CRM_WEB_URL.replace(/\/$/, ""), erpWeb: process.env.ERP_WEB_URL.replace(/\/$/, "") }
    : null;
  if (fromEnv) {
    cached = fromEnv;
    return cached;
  }

  if (fs.existsSync(CACHE_FILE)) {
    try {
      const prior = JSON.parse(fs.readFileSync(CACHE_FILE, "utf8")) as WebUrls;
      if (
        (await stillServes(prior.crmWeb, CRM_TITLE)) &&
        (await stillServes(prior.erpWeb, ERP_TITLE))
      ) {
        cached = prior;
        return cached;
      }
    } catch {
      /* fall through to a fresh probe */
    }
  }

  const ports = listeningPorts();
  const found = await probeForTitles(ports);
  const crmPort = found.get(CRM_TITLE);
  const erpPort = found.get(ERP_TITLE);

  if (!crmPort || !erpPort) {
    throw new Error(
      `Could not locate the demo SPAs by probing ${ports.length} listening ports ` +
        `(crm-web: ${crmPort ?? "not found"}, erp-web: ${erpPort ?? "not found"}). ` +
        "Is the AppHost running? Otherwise set CRM_WEB_URL and ERP_WEB_URL explicitly.",
    );
  }

  cached = { crmWeb: `http://localhost:${crmPort}`, erpWeb: `http://localhost:${erpPort}` };
  fs.writeFileSync(CACHE_FILE, JSON.stringify(cached, null, 2));
  return cached;
}
