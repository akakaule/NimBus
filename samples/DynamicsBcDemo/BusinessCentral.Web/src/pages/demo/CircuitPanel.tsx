import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { CircuitState } from '../../api';
import { formatTime } from '../../format';

const useStyles = makeStyles({
  top: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
  },
  badge: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    minWidth: '150px',
    height: '52px',
    padding: `0 ${tokens.spacingHorizontalXL}`,
    borderRadius: tokens.borderRadiusXLarge,
    fontSize: '22px',
    fontWeight: tokens.fontWeightBold,
    letterSpacing: '0.02em',
    flexShrink: 0,
  },
  closed: {
    backgroundColor: tokens.colorStatusSuccessBackground3,
    color: tokens.colorNeutralForegroundOnBrand,
  },
  open: {
    backgroundColor: tokens.colorStatusDangerBackground3,
    color: tokens.colorNeutralForegroundOnBrand,
  },
  halfOpen: {
    backgroundColor: tokens.colorPaletteMarigoldBackground3,
    color: tokens.colorNeutralForeground1,
  },
  unknown: {
    backgroundColor: tokens.colorNeutralBackground5,
    color: tokens.colorNeutralForeground1,
  },
  reason: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
  },
  since: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  explain: {
    margin: `${tokens.spacingVerticalM} 0`,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  historyTitle: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    marginBottom: tokens.spacingVerticalXS,
  },
  history: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: tokens.fontSizeBase200,
    '& td': {
      padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS} ${tokens.spacingVerticalXS} 0`,
      borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
      verticalAlign: 'top',
    },
  },
  time: {
    whiteSpace: 'nowrap',
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground3,
    width: '72px',
  },
  transition: {
    whiteSpace: 'nowrap',
    fontWeight: tokens.fontWeightSemibold,
    width: '150px',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

const labels: Record<string, string> = { Closed: 'Closed', Open: 'Open', HalfOpen: 'Half-open' };

const explanations: Record<string, string> = {
  Closed: 'Messages flow to Business Central at full speed.',
  Open: 'Business Central keeps failing, so NimBus pauses deliveries to it. Waiting messages stay queued — nothing is lost.',
  HalfOpen: 'NimBus probes Business Central with one session at a time before resuming full speed.',
};

/** The adapter's circuit breaker: current state and recent transitions. */
export function CircuitPanel({ circuit }: { circuit: CircuitState | undefined }) {
  const styles = useStyles();
  if (!circuit) return <div className={styles.empty}>Waiting for the circuit state…</div>;

  const stateClass =
    circuit.state === 'Closed'
      ? styles.closed
      : circuit.state === 'Open'
        ? styles.open
        : circuit.state === 'HalfOpen'
          ? styles.halfOpen
          : styles.unknown;

  return (
    <div>
      <div className={styles.top}>
        <span className={mergeClasses(styles.badge, stateClass)} data-testid="circuit-state" data-state={circuit.state}>
          {labels[circuit.state] ?? circuit.state}
        </span>
        <div>
          <div className={styles.reason}>{circuit.reason}</div>
          <div className={styles.since}>since {formatTime(circuit.changedAt)}</div>
        </div>
      </div>
      <p className={styles.explain}>{explanations[circuit.state] ?? ''}</p>

      <div className={styles.historyTitle}>Transitions</div>
      {circuit.history.length === 0 ? (
        <div className={styles.empty}>No transitions yet.</div>
      ) : (
        <table className={styles.history} data-testid="circuit-history">
          <tbody>
            {circuit.history.slice(0, 8).map((t) => (
              <tr key={`${t.timestamp}-${t.from}-${t.to}`}>
                <td className={styles.time}>{formatTime(t.timestamp)}</td>
                <td className={styles.transition}>
                  {labels[t.from] ?? t.from} → {labels[t.to] ?? t.to}
                </td>
                <td>{t.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
