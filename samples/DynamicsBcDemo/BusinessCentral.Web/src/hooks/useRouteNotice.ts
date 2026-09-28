import { useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import type { NoticeState } from '../components/States';

/**
 * A notice for the page a navigation opens, e.g. "Sales quote created" on the new quote's card. It
 * travels in the history entry's state, which must be cloneable: text only, no JSX.
 */
export interface RouteNotice {
  intent: NoticeState['intent'];
  title?: string;
  message?: string;
}

interface NoticeLocationState {
  notice?: RouteNotice;
}

/** The router state for navigate(to, { state }) that shows `notice` on the page it opens. */
export const noticeState = (notice: RouteNotice): NoticeLocationState => ({ notice });

/**
 * The notice the previous page passed with navigate(), captured on the first render. It is then
 * removed from the history entry, so a reload or Back doesn't show it again.
 */
export function useRouteNotice(): NoticeState | null {
  const location = useLocation();
  const navigate = useNavigate();
  const passed = (location.state as NoticeLocationState | null)?.notice ?? null;
  const [notice] = useState(passed);

  useEffect(() => {
    if (passed) {
      navigate({ pathname: location.pathname, search: location.search, hash: location.hash }, { replace: true, state: null });
    }
  }, [passed, location, navigate]);

  return notice;
}
