import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, type Guid, type SystemUser } from './api';

/** Alex Rivera, the seller the talk track signs in as. */
export const DEFAULT_USER_ID = '5e11e700-0000-4000-8000-000000000001';
const STORAGE_KEY = 'd365sales.signedInUserId';

function readStoredUserId(): Guid {
  try {
    return window.localStorage.getItem(STORAGE_KEY) || DEFAULT_USER_ID;
  } catch {
    return DEFAULT_USER_ID;
  }
}

function storeUserId(userId: Guid) {
  try {
    window.localStorage.setItem(STORAGE_KEY, userId);
  } catch {
    // Storage blocked (private window): the choice lasts for this page load only.
  }
}

interface CurrentUserValue {
  users: SystemUser[];
  /** Sent as userId with every seller action. */
  userId: Guid;
  user: SystemUser | undefined;
  setUserId: (userId: Guid) => void;
  nameOf: (userId: Guid | null | undefined) => string | undefined;
}

const CurrentUserContext = createContext<CurrentUserValue | undefined>(undefined);

export function CurrentUserProvider({ children }: { children: ReactNode }) {
  const [users, setUsers] = useState<SystemUser[]>([]);
  const [selectedId, setSelectedId] = useState<Guid>(readStoredUserId);

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    // The API may still be starting when the page opens: keep trying until the sellers load.
    const load = () => {
      api
        .users(controller.signal)
        .then(setUsers)
        .catch(() => {
          if (!controller.signal.aborted) timer = setTimeout(load, 3000);
        });
    };
    load();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, []);

  const setUserId = useCallback((userId: Guid) => {
    setSelectedId(userId);
    storeUserId(userId);
  }, []);

  const value = useMemo<CurrentUserValue>(() => {
    // A stored id that no longer exists falls back to the default seller.
    const known = users.length === 0 || users.some((u) => u.systemUserId === selectedId);
    const userId = known ? selectedId : DEFAULT_USER_ID;
    const names = new Map(users.map((u) => [u.systemUserId, u.fullName]));
    return {
      users,
      userId,
      user: users.find((u) => u.systemUserId === userId),
      setUserId,
      nameOf: (id) => (id ? names.get(id) : undefined),
    };
  }, [users, selectedId, setUserId]);

  return <CurrentUserContext.Provider value={value}>{children}</CurrentUserContext.Provider>;
}

export function useCurrentUser(): CurrentUserValue {
  const value = useContext(CurrentUserContext);
  if (!value) throw new Error('useCurrentUser must be used inside CurrentUserProvider.');
  return value;
}
