import { useEffect, useState } from "react";
import * as api from "api-client";

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

  // Make single request and cache result
  const client = new api.Client(api.CookieAuth());
  pendingRequest = client.getApiAppStats().then((status) => {
    cachedStatus = status;
    pendingRequest = null;
    return status;
  });

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
