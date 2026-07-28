import { useEffect, useState } from "react";

let cachedResult: boolean | null = null;
let pendingRequest: Promise<boolean> | null = null;

async function fetchDevStatus(): Promise<boolean> {
  if (cachedResult !== null) return cachedResult;
  if (pendingRequest) return pendingRequest;

  pendingRequest = fetch("/api/dev/status")
    .then((res) => {
      // Require a JSON 200: an unknown /api path falls through to the SPA
      // fallback, which answers 200 with index.html (text/html) — that must
      // never read as "dev mode on".
      const enabled =
        res.ok &&
        (res.headers.get("content-type") ?? "").includes("application/json");
      cachedResult = enabled;
      pendingRequest = null;
      return enabled;
    })
    .catch(() => {
      cachedResult = false;
      pendingRequest = null;
      return false;
    });

  return pendingRequest;
}

export default function useDevMode(): boolean {
  const [devMode, setDevMode] = useState(cachedResult ?? false);

  useEffect(() => {
    fetchDevStatus().then(setDevMode);
  }, []);

  return devMode;
}
