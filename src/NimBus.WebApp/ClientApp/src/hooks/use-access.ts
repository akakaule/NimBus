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

/**
 * True when a role value means Owner.
 *
 * The API spec declares these enums lowercase ("owner") and the generated
 * client mirrors that, but the server sends the CLR name ("Owner"):
 * System.Text.Json's JsonStringEnumConverter ignores the [EnumMember] values
 * NSwag puts on the generated enums. Compare case-insensitively so the check
 * holds whichever side of that mismatch is corrected first.
 */
export const isOwnerRole = (role?: string): boolean =>
  (role ?? "").toLowerCase() === "owner";

const ROLE_RANK: Record<string, number> = {
  reader: 1,
  contributor: 2,
  owner: 3,
};

const roleRank = (role?: string): number =>
  ROLE_RANK[(role ?? "").toLowerCase()] ?? 0;

/**
 * True when the user holds at least Contributor on the endpoint — the bar the
 * server applies to operator actions (resubmit, skip, Monitor ACK). The
 * effective role is the higher of the site role and the endpoint grant; ids
 * compare case-insensitively like the server's ACL lookup.
 */
export const canContributeTo = (
  access: api.CurrentUserAccessInfo | null,
  endpointId: string,
): boolean => {
  if (!access) return false;
  if (roleRank(access.siteRole) >= ROLE_RANK.contributor) return true;
  const id = endpointId.toLowerCase();
  return (access.endpointRoles ?? []).some(
    (grant) =>
      grant.endpointId?.toLowerCase() === id &&
      roleRank(grant.role) >= ROLE_RANK.contributor,
  );
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
