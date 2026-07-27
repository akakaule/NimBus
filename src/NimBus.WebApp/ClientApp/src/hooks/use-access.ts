import { useEffect, useState } from "react";
import * as api from "api-client";

// Module-level cache (mirrors use-current-user): the resolved access rarely
// changes during a page's lifetime, so every consumer shares one
// /api/access-control/me round trip. Mutating pages call invalidateAccess()
// after a grant/revoke so the next consumer refetches.
let cachedAccess: api.CurrentUserAccessInfo | null | undefined;
let pendingRequest: Promise<api.CurrentUserAccessInfo | null> | null = null;

export const invalidateAccess = (): void => {
  cachedAccess = undefined;
};

const fetchAccess = (): Promise<api.CurrentUserAccessInfo | null> => {
  if (cachedAccess !== undefined) return Promise.resolve(cachedAccess);
  if (pendingRequest) return pendingRequest;

  pendingRequest = new api.Client(api.CookieAuth())
    .getAccessControlMe()
    .then((data) => {
      cachedAccess = data;
      return data;
    })
    .catch(() => null)
    .finally(() => {
      pendingRequest = null;
    });

  return pendingRequest;
};

/** The current user's resolved access, or null while loading / on error. */
export const useAccess = (): { access: api.CurrentUserAccessInfo | null } => {
  const [access, setAccess] = useState<api.CurrentUserAccessInfo | null>(
    cachedAccess ?? null,
  );

  useEffect(() => {
    let cancelled = false;
    fetchAccess().then((data) => {
      if (!cancelled && data) setAccess(data);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  return { access };
};
