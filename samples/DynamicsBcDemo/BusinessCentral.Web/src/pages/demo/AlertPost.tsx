import { Avatar, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { DismissCircle16Filled, ErrorCircle16Filled, Info16Filled, Warning16Filled } from '@fluentui/react-icons';
import type { ReactElement } from 'react';
import type { Alert } from '../../api';
import { formatTimestamp } from '../../format';
import { teams } from '../../theme';

const useStyles = makeStyles({
  post: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
  },
  fresh: {
    animationName: {
      from: { opacity: 0, transform: 'translateY(-10px)' },
      to: { opacity: 1, transform: 'translateY(0)' },
    },
    animationDuration: '450ms',
    animationTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },
  avatar: {
    flexShrink: 0,
    marginTop: '2px',
  },
  card: {
    flexGrow: 1,
    minWidth: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow2,
    borderLeft: '4px solid transparent',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL} ${tokens.spacingVerticalM}`,
  },
  critical: { borderLeftColor: teams.critical },
  error: { borderLeftColor: teams.error },
  warning: { borderLeftColor: teams.warning },
  information: { borderLeftColor: teams.information },
  meta: {
    display: 'flex',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalS,
    marginBottom: tokens.spacingVerticalXS,
  },
  author: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
  },
  appTag: {
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusSmall,
    padding: '0 4px',
    lineHeight: '14px',
  },
  time: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontVariantNumeric: 'tabular-nums',
  },
  severity: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  criticalText: { color: teams.critical },
  errorText: { color: teams.error },
  warningText: { color: '#9A6C00' },
  informationText: { color: teams.information },
  title: {
    marginTop: tokens.spacingVerticalXXS,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
    overflowWrap: 'anywhere',
  },
  message: {
    marginTop: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground2,
    overflowWrap: 'anywhere',
    whiteSpace: 'pre-line',
  },
  chips: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalS,
  },
  chip: {
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusCircular,
    padding: `2px ${tokens.spacingHorizontalS}`,
    color: tokens.colorNeutralForeground2,
  },
});

type Tone = 'critical' | 'error' | 'warning' | 'information';

function toneOf(severity: string): Tone {
  switch (severity) {
    case 'Critical':
      return 'critical';
    case 'Error':
      return 'error';
    case 'Warning':
      return 'warning';
    default:
      return 'information';
  }
}

const icons: Record<Tone, ReactElement> = {
  critical: <DismissCircle16Filled aria-hidden />,
  error: <ErrorCircle16Filled aria-hidden />,
  warning: <Warning16Filled aria-hidden />,
  information: <Info16Filled aria-hidden />,
};

/** One notification from the NimBus bot, styled as a Teams channel post. */
export function AlertPost({ alert, fresh }: { alert: Alert; fresh: boolean }) {
  const styles = useStyles();
  const tone = toneOf(alert.severity);
  const textClass = { critical: styles.criticalText, error: styles.errorText, warning: styles.warningText, information: styles.informationText }[tone];

  return (
    <article className={mergeClasses(styles.post, fresh && styles.fresh)} data-testid="alert-post" data-severity={alert.severity}>
      <Avatar className={styles.avatar} name="NimBus" initials="NB" color="brand" size={32} shape="square" />
      <div className={mergeClasses(styles.card, styles[tone])}>
        <div className={styles.meta}>
          <span className={styles.author}>NimBus</span>
          <span className={styles.appTag}>APP</span>
          <span className={styles.time}>{formatTimestamp(alert.receivedAt)}</span>
        </div>
        <span className={mergeClasses(styles.severity, textClass)}>
          {icons[tone]}
          {alert.severity}
        </span>
        <div className={styles.title}>{alert.title}</div>
        {alert.message && <div className={styles.message}>{alert.message}</div>}
        {alert.eventTypeId && (
          <div className={styles.chips}>
            <span className={styles.chip} title="Event type">
              {alert.eventTypeId}
            </span>
          </div>
        )}
      </div>
    </article>
  );
}
