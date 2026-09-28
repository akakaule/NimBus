import { Button, Input, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { Rocket20Regular } from '@fluentui/react-icons';
import { useState } from 'react';
import { demoApi, errorMessage, type BurstResult } from '../../api';
import { formatTime } from '../../format';

const useStyles = makeStyles({
  description: {
    margin: `0 0 ${tokens.spacingVerticalM}`,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  count: {
    width: '176px',
  },
  result: {
    marginTop: tokens.spacingVerticalM,
  },
  resultTitle: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    marginBottom: tokens.spacingVerticalXS,
  },
  list: {
    margin: 0,
    padding: 0,
    listStyleType: 'none',
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(360px, 1fr))',
    gap: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalL}`,
    fontSize: tokens.fontSizeBase200,
  },
  item: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXXS} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  number: {
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  seller: {
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  error: {
    marginTop: tokens.spacingVerticalS,
  },
});

const MIN = 1;
const MAX = 24;

/** The pilot sales office: many sellers create prospects and opportunities at the same moment. */
export function BurstPanel() {
  const styles = useStyles();
  const [count, setCount] = useState('6');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<{ burst: BurstResult; at: string } | null>(null);

  const parsed = Number.parseInt(count, 10);
  const valid = Number.isFinite(parsed) && parsed >= MIN && parsed <= MAX;

  const burst = async () => {
    setBusy(true);
    setError(null);
    try {
      const response = await demoApi.burst(parsed);
      setResult({ burst: response, at: new Date().toISOString() });
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      <p className={styles.description}>
        Each seller creates a new prospect with an opportunity in Dynamics 365, and both go to Business Central at once.
        Every prospect is its own session, so one failure never holds up the others.
      </p>
      <div className={styles.actions}>
        <Input
          className={styles.count}
          type="number"
          min={MIN}
          max={MAX}
          value={count}
          contentAfter="opportunities"
          aria-label="Number of opportunities (1–24)"
          onChange={(_, data) => setCount(data.value)}
        />
        <Button
          appearance="primary"
          icon={busy ? <Spinner size="tiny" /> : <Rocket20Regular />}
          onClick={burst}
          disabled={busy || !valid}
          data-testid="burst"
        >
          Create opportunities now
        </Button>
      </div>
      {error && (
        <MessageBar intent="error" className={styles.error}>
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {result && (
        <div className={styles.result} data-testid="burst-result">
          <div className={styles.resultTitle}>
            {result.burst.count} {result.burst.count === 1 ? 'opportunity' : 'opportunities'} created at{' '}
            {formatTime(result.at)}
          </div>
          <ul className={styles.list}>
            {result.burst.created.map((prospect) => (
              <li key={prospect.opportunityId} className={styles.item}>
                <span>
                  <span className={styles.number}>{prospect.number}</span> · {prospect.name}
                </span>
                <span className={styles.seller}>{prospect.seller}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
