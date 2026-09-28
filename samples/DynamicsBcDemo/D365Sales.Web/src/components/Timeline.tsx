import type { ReactElement } from 'react';
import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { ArrowSync16Regular, History20Regular, Person16Regular, Settings16Regular } from '@fluentui/react-icons';
import type { TimelineEntry, TimelineSource } from '../api';
import { formatDateTime, parseTimestamp } from '../format';
import { useNewItems } from '../hooks/useHighlight';
import { EmptyState } from './PageStates';

const useStyles = makeStyles({
  list: {
    listStyleType: 'none',
    margin: 0,
    padding: 0,
    display: 'flex',
    flexDirection: 'column',
  },
  item: {
    display: 'grid',
    gridTemplateColumns: '32px minmax(0, 1fr)',
    columnGap: '12px',
    padding: '10px 8px',
    borderRadius: tokens.borderRadiusMedium,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': {
      borderBottomStyle: 'none',
    },
  },
  fresh: {
    animationName: {
      from: { backgroundColor: tokens.colorBrandBackground2 },
      to: { backgroundColor: 'transparent' },
    },
    animationDuration: '2.6s',
    animationTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },
  icon: {
    width: '32px',
    height: '32px',
    borderRadius: tokens.borderRadiusCircular,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  User: {
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
  },
  Integration: {
    backgroundColor: tokens.colorPaletteTealBackground2,
    color: tokens.colorPaletteTealForeground2,
  },
  System: {
    backgroundColor: tokens.colorNeutralBackground5,
    color: tokens.colorNeutralForeground3,
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '8px',
  },
  title: {
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  bcTag: {
    display: 'inline-flex',
    alignItems: 'center',
    height: '18px',
    padding: '0 6px',
    borderRadius: tokens.borderRadiusMedium,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    backgroundColor: tokens.colorPaletteTealBackground2,
    color: tokens.colorPaletteTealForeground2,
    whiteSpace: 'nowrap',
  },
  detail: {
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground2,
    overflowWrap: 'anywhere',
  },
  meta: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
  },
});

const ICONS: Record<TimelineSource, ReactElement> = {
  User: <Person16Regular />,
  Integration: <ArrowSync16Regular />,
  System: <Settings16Regular />,
};

const sourceOf = (source: string): TimelineSource =>
  source === 'Integration' || source === 'System' ? source : 'User';

/** A small "Business Central" tag for things that came from the integration. */
export function BusinessCentralTag() {
  const styles = useStyles();
  return <span className={styles.bcTag}>Business Central</span>;
}

interface TimelineProps {
  entries: TimelineEntry[];
  /** Show only the newest n entries. */
  limit?: number;
  emptyText?: string;
  testId?: string;
}

/** The activity wall: newest first; integration posts are tagged Business Central. */
export function Timeline({ entries, limit, emptyText = 'No activity yet.', testId = 'timeline' }: TimelineProps) {
  const styles = useStyles();
  const sorted = [...entries].sort(
    (a, b) => parseTimestamp(b.createdOn).getTime() - parseTimestamp(a.createdOn).getTime(),
  );
  const shown = limit ? sorted.slice(0, limit) : sorted;
  const fresh = useNewItems(sorted.map((entry) => entry.id));

  return (
    <div data-testid={testId}>
      {shown.length === 0 ? (
        <EmptyState icon={<History20Regular />} title={emptyText} />
      ) : (
        <ol className={styles.list}>
          {shown.map((entry) => {
            const source = sourceOf(entry.source);
            return (
              <li key={entry.id} className={mergeClasses(styles.item, fresh.has(entry.id) && styles.fresh)}>
                <span className={mergeClasses(styles.icon, styles[source])} aria-hidden>
                  {ICONS[source]}
                </span>
                <div className={styles.body}>
                  <div className={styles.head}>
                    <span className={styles.title}>{entry.title}</span>
                    {source === 'Integration' && <BusinessCentralTag />}
                  </div>
                  {entry.detail && <span className={styles.detail}>{entry.detail}</span>}
                  <span className={styles.meta}>
                    {source} · {formatDateTime(entry.createdOn)}
                  </span>
                </div>
              </li>
            );
          })}
        </ol>
      )}
    </div>
  );
}
