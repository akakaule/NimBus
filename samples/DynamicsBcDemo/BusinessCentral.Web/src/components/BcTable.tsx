import {
  makeStyles,
  mergeClasses,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  tokens,
} from '@fluentui/react-components';
import type { KeyboardEvent, MouseEvent, ReactNode } from 'react';

const useStyles = makeStyles({
  scroller: {
    overflowX: 'auto',
  },
  table: {
    minWidth: '720px',
  },
  headerCell: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
    borderBottom: `1px solid ${tokens.colorNeutralStroke1}`,
  },
  headerEnd: {
    '& .fui-TableHeaderCell__button': {
      justifyContent: 'flex-end',
    },
  },
  cell: {
    fontSize: tokens.fontSizeBase300,
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXS,
    overflowWrap: 'anywhere',
  },
  end: {
    textAlign: 'right',
    whiteSpace: 'nowrap',
    fontVariantNumeric: 'tabular-nums',
  },
  clickable: {
    cursor: 'pointer',
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '-2px',
    },
  },
  empty: {
    padding: `${tokens.spacingVerticalXL} ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground3,
    textAlign: 'center',
  },
});

export interface Column<T> {
  key: string;
  header: string;
  /** CSS width of the column, e.g. "120px" or "14%". */
  width?: string;
  align?: 'start' | 'end';
  render: (row: T) => ReactNode;
}

interface BcTableProps<T> {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  /** Opens the row's card; clicks on links and buttons inside the row are left alone. */
  onRowClick?: (row: T) => void;
  rowClassName?: (row: T) => string | undefined;
  empty?: ReactNode;
  'aria-label': string;
  'data-testid'?: string;
}

function fromInteractive(target: EventTarget): boolean {
  return target instanceof Element && target.closest('a, button, input, [role="option"]') !== null;
}

/** A dense Business Central list. */
export function BcTable<T>({
  columns,
  rows,
  rowKey,
  onRowClick,
  rowClassName,
  empty,
  'aria-label': ariaLabel,
  'data-testid': testId,
}: BcTableProps<T>) {
  const styles = useStyles();

  const onClick = (row: T) => (event: MouseEvent) => {
    if (onRowClick && !fromInteractive(event.target)) onRowClick(row);
  };
  const onKeyDown = (row: T) => (event: KeyboardEvent) => {
    if (onRowClick && event.key === 'Enter' && !fromInteractive(event.target)) onRowClick(row);
  };

  return (
    <div className={styles.scroller} data-testid={testId}>
      <Table size="small" aria-label={ariaLabel} className={styles.table}>
        <TableHeader>
          <TableRow>
            {columns.map((column) => (
              <TableHeaderCell
                key={column.key}
                className={mergeClasses(styles.headerCell, column.align === 'end' && styles.headerEnd)}
                style={column.width ? { width: column.width } : undefined}
              >
                {column.header}
              </TableHeaderCell>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow
              key={rowKey(row)}
              className={mergeClasses(onRowClick && styles.clickable, rowClassName?.(row))}
              onClick={onRowClick ? onClick(row) : undefined}
              onKeyDown={onRowClick ? onKeyDown(row) : undefined}
              tabIndex={onRowClick ? 0 : undefined}
            >
              {columns.map((column) => (
                <TableCell key={column.key} className={mergeClasses(styles.cell, column.align === 'end' && styles.end)}>
                  {column.render(row)}
                </TableCell>
              ))}
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {rows.length === 0 && <div className={styles.empty}>{empty ?? 'There is nothing to show in this view.'}</div>}
    </div>
  );
}
