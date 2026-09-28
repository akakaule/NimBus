import { useEffect, useRef, useState, type ReactNode } from 'react';
import { makeStyles, tokens, Toaster } from '@fluentui/react-components';
import { useLocation } from 'react-router-dom';
import { TOASTER_ID } from '../hooks/useNotify';
import { SiteMap } from './SiteMap';
import { TOP_BAR_HEIGHT, TopBar } from './TopBar';

const COLLAPSED_KEY = 'd365sales.siteMapCollapsed';

function readCollapsed(): boolean {
  try {
    return window.localStorage.getItem(COLLAPSED_KEY) === 'true';
  } catch {
    return false;
  }
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    backgroundColor: tokens.colorNeutralBackground3,
  },
  body: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
  },
  main: {
    flex: 1,
    minWidth: 0,
    overflowY: 'auto',
    padding: '12px 16px 24px 8px',
  },
});

export function Shell({ children }: { children: ReactNode }) {
  const styles = useStyles();
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const main = useRef<HTMLElement>(null);
  const { pathname } = useLocation();

  // The content pane scrolls, not the window: start each page at the top.
  useEffect(() => {
    main.current?.scrollTo({ top: 0 });
  }, [pathname]);

  const toggle = () => {
    const next = !collapsed;
    setCollapsed(next);
    try {
      window.localStorage.setItem(COLLAPSED_KEY, String(next));
    } catch {
      // Not persisted when storage is blocked.
    }
  };

  return (
    <div className={styles.root}>
      <TopBar />
      <div className={styles.body}>
        <SiteMap collapsed={collapsed} onToggle={toggle} />
        <main ref={main} className={styles.main}>
          {children}
        </main>
      </div>
      <Toaster
        toasterId={TOASTER_ID}
        position="top-end"
        offset={{ vertical: TOP_BAR_HEIGHT + 8 }}
        pauseOnHover
        pauseOnWindowBlur
      />
    </div>
  );
}
