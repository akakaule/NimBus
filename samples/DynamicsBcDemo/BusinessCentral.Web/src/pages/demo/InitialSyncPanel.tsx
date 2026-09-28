import { Button, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { DatabaseArrowRight20Regular } from '@fluentui/react-icons';
import { useState } from 'react';
import { demoApi, errorMessage, type InitialSyncResult } from '../../api';
import { formatTime } from '../../format';

const useStyles = makeStyles({
  description: {
    margin: `0 0 ${tokens.spacingVerticalM}`,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  result: {
    marginTop: tokens.spacingVerticalM,
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
  },
  error: {
    marginTop: tokens.spacingVerticalS,
  },
});

const count = (n: number, noun: string) => `${n} ${n === 1 ? noun : `${noun}s`}`;

/** Go-live: Business Central's customers, their contacts and the item categories go to Dynamics 365. */
export function InitialSyncPanel() {
  const styles = useStyles();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<{ sync: InitialSyncResult; at: string } | null>(null);

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      const sync = await demoApi.initialSync();
      setResult({ sync, at: new Date().toISOString() });
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      <p className={styles.description}>
        Loads Business Central's customers, their contacts and the item categories into Dynamics 365 through NimBus —
        the initial sync at go-live.
      </p>
      <Button
        appearance="primary"
        icon={busy ? <Spinner size="tiny" /> : <DatabaseArrowRight20Regular />}
        onClick={run}
        disabled={busy}
        data-testid="initial-sync"
      >
        Run the initial sync
      </Button>
      {error && (
        <MessageBar intent="error" className={styles.error}>
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {result && (
        <div className={styles.result} data-testid="initial-sync-result">
          Sent {count(result.sync.itemCategories, 'product group')}, {count(result.sync.customers, 'customer')} and{' '}
          {count(result.sync.contacts, 'contact')} at {formatTime(result.at)}.
        </div>
      )}
    </div>
  );
}
