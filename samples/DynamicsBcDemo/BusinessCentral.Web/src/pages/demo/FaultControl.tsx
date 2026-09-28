import { Badge, Button, Input, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { useState } from 'react';
import { errorMessage, type FaultSnapshot, type FaultWindow } from '../../api';
import { formatTime } from '../../format';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalM} 0`,
    ':not(:last-child)': {
      borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    },
  },
  top: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalL,
  },
  text: {
    flexGrow: 1,
    minWidth: 0,
  },
  title: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  description: {
    marginTop: tokens.spacingVerticalXXS,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  status: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: tokens.spacingVerticalXXS,
    flexShrink: 0,
  },
  countdown: {
    fontSize: '28px',
    lineHeight: '32px',
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorPaletteRedForeground1,
  },
  endsAt: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  seconds: {
    width: '96px',
  },
});

const MIN_SECONDS = 1;
const MAX_SECONDS = 600;

interface FaultControlProps {
  kind: 'maintenance' | 'throttling';
  title: string;
  description: string;
  window: FaultWindow | undefined;
  /** The start button's label; seconds is null while the input holds no valid duration. */
  startLabel: (seconds: number | null) => string;
  onSet: (seconds: number) => Promise<FaultSnapshot>;
  /** Called after a change so the cockpit reloads the state at once. */
  onChanged: () => void;
  'data-testid': string;
}

/** One time-boxed BC failure mode: start it for N seconds, or end it now. */
export function FaultControl({ kind, title, description, window, startLabel, onSet, onChanged, 'data-testid': testId }: FaultControlProps) {
  const styles = useStyles();
  const [seconds, setSeconds] = useState('20');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const parsed = Number.parseInt(seconds, 10);
  const valid = Number.isFinite(parsed) && parsed >= MIN_SECONDS && parsed <= MAX_SECONDS;
  const active = window?.active ?? false;

  const apply = async (duration: number) => {
    setBusy(true);
    setError(null);
    try {
      await onSet(duration);
      onChanged();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.root} data-testid={`fault-${kind}`}>
      <div className={styles.top}>
        <div className={styles.text}>
          <div className={styles.title}>{title}</div>
          <div className={styles.description}>{description}</div>
        </div>
        <div className={styles.status}>
          <Badge appearance="filled" color={active ? 'danger' : 'success'} data-testid={`${kind}-status`}>
            {active ? 'Active' : 'Inactive'}
          </Badge>
          {active && window && (
            <>
              <span className={styles.countdown} data-testid={`${kind}-remaining`}>
                {window.remainingSeconds} s
              </span>
              <span className={styles.endsAt}>ends {formatTime(window.endsAt)}</span>
            </>
          )}
        </div>
      </div>
      <div className={styles.actions}>
        <Input
          className={styles.seconds}
          type="number"
          min={MIN_SECONDS}
          max={MAX_SECONDS}
          value={seconds}
          contentAfter="s"
          aria-label={`${title} duration in seconds`}
          onChange={(_, data) => setSeconds(data.value)}
        />
        <Button
          appearance="primary"
          onClick={() => apply(parsed)}
          disabled={busy || !valid}
          icon={busy ? <Spinner size="tiny" /> : undefined}
          data-testid={testId}
        >
          {startLabel(valid ? parsed : null)}
        </Button>
        <Button onClick={() => apply(0)} disabled={busy || !active} data-testid={`end-${kind}`}>
          End now
        </Button>
      </div>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}
