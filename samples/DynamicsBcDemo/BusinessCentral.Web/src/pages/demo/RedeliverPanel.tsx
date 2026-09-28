import { Button, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { ArrowRedo20Regular } from '@fluentui/react-icons';
import { useState } from 'react';
import { ApiError, demoApi, errorMessage, type RedeliverResult } from '../../api';
import { formatTime } from '../../format';

const useStyles = makeStyles({
  description: {
    margin: `0 0 ${tokens.spacingVerticalM}`,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  result: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    marginTop: tokens.spacingVerticalM,
    fontSize: tokens.fontSizeBase300,
  },
  summary: {
    fontWeight: tokens.fontWeightSemibold,
  },
  messageId: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    overflowWrap: 'anywhere',
  },
  mono: {
    fontFamily: tokens.fontFamilyMonospace,
  },
  message: {
    marginTop: tokens.spacingVerticalS,
  },
});

// 'nothing' is the 404 for "Dynamics 365 hasn't sent an opportunity change yet".
type Outcome =
  | { kind: 'delivered'; result: RedeliverResult; at: string }
  | { kind: 'nothing'; message: string }
  | { kind: 'failed'; message: string };

/** Dynamics 365 delivers its last opportunity change again; NimBus skips the duplicate. */
export function RedeliverPanel() {
  const styles = useStyles();
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome | null>(null);

  const redeliver = async () => {
    setBusy(true);
    setOutcome(null);
    try {
      const result = await demoApi.redeliver();
      setOutcome({ kind: 'delivered', result, at: new Date().toISOString() });
    } catch (e) {
      const nothingSent = e instanceof ApiError && e.status === 404;
      setOutcome({ kind: nothingSent ? 'nothing' : 'failed', message: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      <p className={styles.description}>
        Dynamics 365 sends its last opportunity change again, with the same MessageId — the way a source system's retry
        can. NimBus recognises it and skips it as DuplicateDetected.
      </p>
      <Button
        appearance="primary"
        icon={busy ? <Spinner size="tiny" /> : <ArrowRedo20Regular />}
        onClick={redeliver}
        disabled={busy}
        data-testid="redeliver"
      >
        Deliver the last opportunity change again
      </Button>
      {outcome?.kind === 'delivered' && (
        <div className={styles.result} data-testid="redeliver-result">
          <span className={styles.summary}>
            Delivered {outcome.result.summary} again at {formatTime(outcome.at)}.
          </span>
          <span className={styles.messageId}>
            MessageId <span className={styles.mono}>{outcome.result.messageId}</span>
          </span>
        </div>
      )}
      {outcome?.kind === 'nothing' && (
        <MessageBar intent="warning" className={styles.message} data-testid="redeliver-result">
          <MessageBarBody>{outcome.message}</MessageBarBody>
        </MessageBar>
      )}
      {outcome?.kind === 'failed' && (
        <MessageBar intent="error" className={styles.message}>
          <MessageBarBody>{outcome.message}</MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}
