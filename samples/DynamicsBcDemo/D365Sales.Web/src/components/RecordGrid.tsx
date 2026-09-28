import type { MouseEvent, ReactNode } from 'react';
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

const useStyles = makeStyles({
  wrapper: {
    overflowX: 'auto',
  },
  table: {
    minWidth: '100%',
  },
  headerCell: {
    // Column widths include the cell padding, so the free column gets what is really left.
    boxSizing: 'border-box',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'nowrap',
  },
  alignEnd: {
    justifyContent: 'flex-end',
    textAlign: 'end',
  },
  cell: {
    fontSize: tokens.fontSizeBase300,
  },
  // One line per row, like a model-driven grid; the full text is in the cell's tooltip.
  clip: {
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  numeric: {
    textAlign: 'end',
    whiteSpace: 'nowrap',
    fontVariantNumeric: 'tabular-nums',
  },
  clickable: {
    cursor: 'pointer',
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
  emptyCell: {
    padding: 0,
  },
});

export interface GridColumn<T> {
  id: string;
  header: string;
  render: (row: T) => ReactNode;
  /** The column width, e.g. "140px" or "18%". Columns without one share the rest of the row. */
  width?: string;
  /** Right-aligned, tabular figures (amounts, quantities). */
  numeric?: boolean;
  /** The full text, shown as a tooltip when the cell is cut short. */
  title?: (row: T) => string | null | undefined;
  /** Inputs and buttons need room for their focus outline, so their cells are not clipped. */
  interactive?: boolean;
}

interface RecordGridProps<T> {
  ariaLabel: string;
  columns: GridColumn<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  /** Opens the record when the row is clicked outside its links and buttons. */
  onOpen?: (row: T) => void;
  empty?: ReactNode;
  /** Extra rows after the data, e.g. a total. */
  footer?: ReactNode;
  /** Keys of rows to flash briefly (just arrived). */
  fresh?: ReadonlySet<string>;
  size?: 'extra-small' | 'small' | 'medium';
  testId?: string;
}

const INTERACTIVE = 'a, button, input, select, textarea, [role="button"], [role="menuitem"]';

/** A read-only list grid in the style of a model-driven view. */
export function RecordGrid<T>({
  ariaLabel,
  columns,
  rows,
  rowKey,
  onOpen,
  empty,
  footer,
  fresh,
  size = 'small',
  testId,
}: RecordGridProps<T>) {
  const styles = useStyles();

  const onRowClick = (row: T) => (event: MouseEvent<HTMLElement>) => {
    if (!onOpen || (event.target as HTMLElement).closest(INTERACTIVE)) return;
    onOpen(row);
  };

  return (
    <div className={styles.wrapper} data-testid={testId}>
      <Table aria-label={ariaLabel} size={size} className={styles.table}>
        <TableHeader>
          <TableRow>
            {columns.map((column) => (
              <TableHeaderCell
                key={column.id}
                className={styles.headerCell}
                style={column.width ? { width: column.width } : undefined}
                button={column.numeric ? { className: styles.alignEnd } : undefined}
              >
                {column.header}
              </TableHeaderCell>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.length === 0 && empty ? (
            <TableRow>
              <TableCell colSpan={columns.length} className={styles.emptyCell}>
                {empty}
              </TableCell>
            </TableRow>
          ) : (
            rows.map((row) => {
              const key = rowKey(row);
              return (
                <TableRow
                  key={key}
                  className={mergeClasses(onOpen && styles.clickable, fresh?.has(key) && styles.fresh)}
                  onClick={onOpen ? onRowClick(row) : undefined}
                >
                  {columns.map((column) => (
                    <TableCell
                      key={column.id}
                      className={mergeClasses(
                        styles.cell,
                        !column.interactive && styles.clip,
                        column.numeric && styles.numeric,
                      )}
                      title={column.title?.(row) ?? undefined}
                    >
                      {column.render(row)}
                    </TableCell>
                  ))}
                </TableRow>
              );
            })
          )}
          {footer}
        </TableBody>
      </Table>
    </div>
  );
}
