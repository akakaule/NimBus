import { useState, type ReactElement, type ReactNode } from 'react';
import { Input, makeStyles, tokens, ToolbarButton } from '@fluentui/react-components';
import { ArrowClockwise20Regular, Search20Regular } from '@fluentui/react-icons';
import type { PollResult } from '../hooks/usePolling';
import { useNewItems } from '../hooks/useHighlight';
import { CommandBar } from './CommandBar';
import { EmptyState, ErrorState, LoadingState, StaleDataWarning } from './PageStates';
import { RecordGrid, type GridColumn } from './RecordGrid';
import { Card } from './ui';

const useStyles = makeStyles({
  viewHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    flexWrap: 'wrap',
    gap: '12px',
    padding: '14px 20px 10px',
  },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  filter: {
    width: '260px',
  },
  grid: {
    padding: '0 12px',
  },
  footer: {
    padding: '10px 20px 12px',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    borderTop: `1px solid ${tokens.colorNeutralStroke3}`,
  },
});

interface ListViewProps<T> {
  /** The view name, e.g. "All opportunities". */
  title: string;
  /** Plural record name for messages, e.g. "opportunities". */
  entityPlural: string;
  poll: PollResult<T[]>;
  columns: GridColumn<T>[];
  rowKey: (row: T) => string;
  onOpen: (row: T) => void;
  /** The text the keyword filter searches. */
  searchText: (row: T) => string;
  emptyIcon: ReactElement;
  emptyText?: ReactNode;
  /** Commands after Refresh. */
  commands?: ReactNode;
  testId?: string;
}

/** A model-driven list page: command bar, a view with a keyword filter, the grid and a row count. */
export function ListView<T>({
  title,
  entityPlural,
  poll,
  columns,
  rowKey,
  onOpen,
  searchText,
  emptyIcon,
  emptyText,
  commands,
  testId,
}: ListViewProps<T>) {
  const styles = useStyles();
  const [keyword, setKeyword] = useState('');
  const rows = poll.data ?? [];
  const needle = keyword.trim().toLowerCase();
  const visible = needle ? rows.filter((row) => searchText(row).toLowerCase().includes(needle)) : rows;
  // Records that arrive while the view is open (e.g. from the pilot-office burst) flash briefly.
  const fresh = useNewItems(rows.map(rowKey), poll.data !== undefined);

  let body: ReactNode;
  if (poll.loading) {
    body = <LoadingState label={`Loading ${entityPlural}…`} />;
  } else if (!poll.data) {
    body = (
      <div className={styles.grid}>
        <ErrorState title={`The ${entityPlural} could not be loaded`} message={poll.error ?? ''} onRetry={() => void poll.refresh()} />
      </div>
    );
  } else {
    body = (
      <>
        <div className={styles.grid}>
          <RecordGrid
            ariaLabel={title}
            columns={columns}
            rows={visible}
            rowKey={rowKey}
            onOpen={onOpen}
            fresh={fresh}
            testId={testId}
            empty={
              needle ? (
                <EmptyState icon={<Search20Regular />} title={`No ${entityPlural} match “${keyword.trim()}”`} />
              ) : (
                <EmptyState icon={emptyIcon} title={`No ${entityPlural} yet`}>
                  {emptyText}
                </EmptyState>
              )
            }
          />
        </div>
        <div className={styles.footer}>
          {needle ? `Rows: ${visible.length} of ${rows.length}` : `Rows: ${rows.length}`}
        </div>
      </>
    );
  }

  return (
    <>
      <CommandBar label={`${title} commands`}>
        <ToolbarButton icon={<ArrowClockwise20Regular />} onClick={() => void poll.refresh()}>
          Refresh
        </ToolbarButton>
        {commands}
      </CommandBar>
      <StaleDataWarning error={poll.data ? poll.error : undefined} />
      <Card>
        <div className={styles.viewHeader}>
          <h1 className={styles.title}>{title}</h1>
          <Input
            className={styles.filter}
            contentBefore={<Search20Regular />}
            placeholder="Filter by keyword"
            aria-label={`Filter ${entityPlural} by keyword`}
            value={keyword}
            onChange={(_, data) => setKeyword(data.value)}
          />
        </div>
        {body}
      </Card>
    </>
  );
}
