// Same .env / .env.local precedence the Playwright configs use, for the standalone
// scripts that run outside Playwright.

import * as dotenv from "dotenv";

export function loadEnv(): void {
  dotenv.config({ path: ".env", quiet: true });
  dotenv.config({ path: ".env.local", override: true, quiet: true });
}
