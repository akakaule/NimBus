import { useEffect, useState } from "react";
// Type-only import: the api-client module (345KB, pulls in moment) must not
// land in the eager entry chunk — the runtime module is loaded on demand
// inside getApplicationStatus via dynamic import.
import type * as api from "api-client";

// Module-level cache for static app status (never changes during runtime)
let cachedStatus: api.ApplicationStatus | null = null;
let pendingRequest: Promise<api.ApplicationStatus> | null = null;

export const getEnv = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.env);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};

export const getApplicationStatus = async () => {
  // Return cached value if available
  if (cachedStatus) {
    return cachedStatus;
  }

  // Deduplicate concurrent requests - return existing promise
  if (pendingRequest) {
    return pendingRequest;
  }

  // Make single request and cache result. The async IIFE is assigned to
  // pendingRequest synchronously (before any await), so concurrent callers
  // still dedupe onto the same in-flight promise while the api-client module
  // itself loads lazily. pendingRequest is cleared in finally — success AND
  // failure — so a transient fetch error doesn't pin a rejected promise as
  // the answer for the rest of the page's lifetime; the next call retries.
  pendingRequest = (async () => {
    try {
      const apiMod = await import("api-client");
      const client = new apiMod.Client(apiMod.CookieAuth());
      const status = await client.getApiAppStats();
      cachedStatus = status;
      return status;
    } finally {
      pendingRequest = null;
    }
  })();

  return pendingRequest;
};

/* export const getPlatformVersion = () => {
  const data = useAsyncMemo(async () => {
    return getApplicationStatus()
  }, [])

  return data?.platformVersion;
};*/

export const getPlatformVersion = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.platformVersion);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};

export const getStorageProvider = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.storageProvider);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};

// "{ticket}" placeholder URL template for reported-event deep links; undefined
// or empty when no ticket system is configured (the Reported column then
// renders a plain badge without a link).
export const getTicketLinkTemplate = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.ticketLinkTemplate ?? undefined);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};
