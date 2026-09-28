import { makeStyles, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { demoApi } from '../api';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  bar: {
    paddingLeft: '24px',
    paddingRight: '24px',
  },
  seconds: {
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
});

/**
 * The only trace of the demo's failure modes in the BC chrome: the notice a BC user would see
 * during an update window or while the integration APIs are throttled.
 */
export function EnvironmentBanner() {
  const styles = useStyles();
  const { data } = usePolling((signal) => demoApi.state(signal), 2000);
  const faults = data?.faults;
  if (!faults) return null;

  return (
    <>
      {faults.maintenance.active && (
        <MessageBar intent="warning" shape="square" className={styles.bar} data-testid="banner-maintenance">
          <MessageBarBody>
            Environment update in progress (simulated) — integrations are temporarily unavailable ·{' '}
            <span className={styles.seconds}>{faults.maintenance.remainingSeconds} s remaining</span>
          </MessageBarBody>
        </MessageBar>
      )}
      {faults.throttling.active && (
        <MessageBar intent="warning" shape="square" className={styles.bar} data-testid="banner-throttling">
          <MessageBarBody>
            API throttling in effect (simulated) ·{' '}
            <span className={styles.seconds}>{faults.throttling.remainingSeconds} s remaining</span>
          </MessageBarBody>
        </MessageBar>
      )}
    </>
  );
}
