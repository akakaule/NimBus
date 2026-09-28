import { Button, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { Broom16Regular, CheckmarkCircle48Regular } from '@fluentui/react-icons';
import { useEffect, useMemo, useRef, useState } from 'react';
import { demoApi, errorMessage, type Alert } from '../../api';
import { useDocumentTitle } from '../../hooks/useDocumentTitle';
import { usePolling } from '../../hooks/usePolling';
import { teams } from '../../theme';
import { AlertPost } from './AlertPost';

const useStyles = makeStyles({
  page: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    backgroundColor: teams.page,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: teams.header,
    color: '#FFFFFF',
  },
  channelIcon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '40px',
    height: '40px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: 'rgba(255, 255, 255, 0.16)',
    fontSize: '22px',
    fontWeight: tokens.fontWeightBold,
  },
  channelName: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  teamName: {
    fontSize: tokens.fontSizeBase200,
    color: teams.headerMuted,
  },
  tabs: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    padding: `0 ${tokens.spacingHorizontalXXL}`,
    height: '44px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  tab: {
    display: 'flex',
    alignItems: 'center',
    height: '100%',
    fontWeight: tokens.fontWeightSemibold,
    borderBottom: `3px solid ${tokens.colorBrandStroke1}`,
    paddingTop: '3px',
  },
  count: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  clear: {
    marginLeft: 'auto',
  },
  feed: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    width: '100%',
    maxWidth: '980px',
    padding: `${tokens.spacingVerticalXL} ${tokens.spacingHorizontalXXL} ${tokens.spacingVerticalXXXL}`,
  },
  empty: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalXXXL} 0`,
    color: tokens.colorNeutralForeground3,
    textAlign: 'center',
  },
  emptyIcon: {
    color: tokens.colorStatusSuccessForeground1,
  },
  emptyTitle: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
});

/** Stable keys for alerts, which carry no id of their own. */
function keyed(alerts: Alert[]): { key: string; alert: Alert }[] {
  const seen = new Map<string, number>();
  return alerts.map((alert) => {
    const base = `${alert.receivedAt}|${alert.eventId}|${alert.title}`;
    const n = seen.get(base) ?? 0;
    seen.set(base, n + 1);
    return { key: n === 0 ? base : `${base}|${n}`, alert };
  });
}

/** Hidden audience page (/demo/alerts): NimBus notifications as a Teams-style channel. */
export default function AlertsChannel() {
  const styles = useStyles();
  useDocumentTitle('# integration-alerts · Contoso Subsea IT (simulated)');
  const alerts = usePolling((signal) => demoApi.alerts(signal), 2000);
  const [clearing, setClearing] = useState(false);
  const [clearError, setClearError] = useState<string | null>(null);
  // Keys already on screen; posts that arrive later slide in.
  const shownKeys = useRef<Set<string> | null>(null);

  const posts = useMemo(() => keyed(alerts.data ?? []), [alerts.data]);

  useEffect(() => {
    if (!alerts.data) return;
    shownKeys.current = new Set(posts.map((p) => p.key));
  }, [alerts.data, posts]);

  const clear = async () => {
    setClearing(true);
    setClearError(null);
    try {
      await demoApi.clearAlerts();
      alerts.replace([]);
    } catch (e) {
      setClearError(errorMessage(e));
    } finally {
      setClearing(false);
    }
  };

  const known = shownKeys.current;

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <div className={styles.channelIcon} aria-hidden>
          #
        </div>
        <div>
          <h1 className={styles.channelName}># integration-alerts</h1>
          <div className={styles.teamName}>Contoso Subsea IT (simulated Teams channel)</div>
        </div>
      </header>

      <div className={styles.tabs}>
        <span className={styles.tab}>Posts</span>
        {alerts.data && (
          <span className={styles.count}>
            {alerts.data.length} {alerts.data.length === 1 ? 'post' : 'posts'}
          </span>
        )}
        <Button
          className={styles.clear}
          size="small"
          appearance="subtle"
          icon={<Broom16Regular />}
          onClick={clear}
          disabled={clearing || !alerts.data?.length}
          data-testid="alerts-clear"
        >
          Clear
        </Button>
      </div>

      <main className={styles.feed} data-testid="alerts-list">
        {clearError && (
          <MessageBar intent="error">
            <MessageBarBody>{clearError}</MessageBarBody>
          </MessageBar>
        )}
        {alerts.error && (
          <MessageBar intent="warning">
            <MessageBarBody>Can't load the channel right now: {alerts.error.message}</MessageBarBody>
          </MessageBar>
        )}
        {!alerts.data ? (
          !alerts.error && <Spinner size="small" label="Loading posts…" />
        ) : posts.length === 0 ? (
          <div className={styles.empty} data-testid="alerts-empty">
            <CheckmarkCircle48Regular className={styles.emptyIcon} aria-hidden />
            <div className={styles.emptyTitle}>No alerts — all integrations healthy</div>
            <div>NimBus posts here when a message fails or a circuit breaker opens.</div>
          </div>
        ) : (
          posts.map(({ key, alert }) => <AlertPost key={key} alert={alert} fresh={known !== null && !known.has(key)} />)
        )}
      </main>
    </div>
  );
}
