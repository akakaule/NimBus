import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { chrome } from '../theme';

const useStyles = makeStyles({
  group: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  groupTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  tiles: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  cue: {
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'space-between',
    width: '164px',
    height: '104px',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM} ${tokens.spacingVerticalM}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: chrome.cue,
    color: '#FFFFFF',
    textDecorationLine: 'none',
    boxShadow: tokens.shadow4,
    transitionProperty: 'background-color, transform',
    transitionDuration: tokens.durationFast,
    ':hover': {
      backgroundColor: chrome.cueHover,
      transform: 'translateY(-1px)',
    },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '2px',
    },
  },
  unfavorable: {
    backgroundColor: chrome.cueUnfavorable,
    ':hover': {
      backgroundColor: chrome.cueUnfavorableHover,
    },
  },
  title: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    opacity: 0.92,
  },
  value: {
    fontSize: '30px',
    lineHeight: '34px',
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
  smallValue: {
    fontSize: '22px',
    lineHeight: '28px',
  },
  sub: {
    fontSize: tokens.fontSizeBase200,
    opacity: 0.9,
    fontVariantNumeric: 'tabular-nums',
  },
});

/** A titled row of cues, e.g. "Sales Quotes". */
export function CueGroup({ title, children }: { title: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.group}>
      <h3 className={styles.groupTitle}>{title}</h3>
      <div className={styles.tiles}>{children}</div>
    </div>
  );
}

interface CueProps {
  title: string;
  value: ReactNode;
  /** A second figure under the value, e.g. the orders' amount. */
  sub?: ReactNode;
  to: string;
  /** Red, like a BC cue whose threshold is exceeded. */
  unfavorable?: boolean;
  /** Smaller figure for amounts that would not fit. */
  small?: boolean;
  'data-testid'?: string;
}

/** A Role Center cue: a tile with a count that opens the matching list. */
export function Cue({ title, value, sub, to, unfavorable, small, 'data-testid': testId }: CueProps) {
  const styles = useStyles();
  return (
    <Link to={to} className={mergeClasses(styles.cue, unfavorable && styles.unfavorable)} data-testid={testId}>
      <span className={styles.title}>{title}</span>
      <span>
        <span className={mergeClasses(styles.value, small && styles.smallValue)}>{value}</span>
        {sub && <div className={styles.sub}>{sub}</div>}
      </span>
    </Link>
  );
}
