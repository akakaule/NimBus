import { useEffect, useState } from "react";

let cachedResult: boolean | null = null;
let pendingRequest: Promise<boolean> | null = null;

async function fetchDevStatus(): Promise<boolean> {
  if (cachedResult !== null) return cachedResult;
  if (pendingRequest) return pendingRequest;

  pendingRequest = fetch("/api/dev/status")
    .then((res) => {
      const enabled = res.ok;
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
