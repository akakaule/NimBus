import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  // Always two columns (a lone column keeps its half), stacked on narrow screens.
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    columnGap: '56px',
    rowGap: 0,
    alignItems: 'start',
    '@media (max-width: 900px)': {
      gridTemplateColumns: 'minmax(0, 1fr)',
    },
  },
  column: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 44%) minmax(0, 56%)',
    alignItems: 'start',
    minHeight: '34px',
  },
  label: {
    display: 'flex',
    alignItems: 'center',
    minHeight: '34px',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    minWidth: 0,
  },
  labelText: {
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  leader: {
    flexGrow: 1,
    minWidth: '12px',
    height: 0,
    marginTop: '8px',
    marginLeft: tokens.spacingHorizontalS,
    marginRight: tokens.spacingHorizontalS,
    borderBottom: `1px dotted ${tokens.colorNeutralStroke1}`,
  },
  value: {
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'center',
    minHeight: '34px',
    minWidth: 0,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    overflowWrap: 'anywhere',
  },
  hint: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    paddingTop: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalXS,
  },
  mono: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
  empty: {
    color: tokens.colorNeutralForeground4,
  },
});

/** Lays field columns out side by side, and stacked on narrow screens. */
export function FieldGrid({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.grid}>{children}</div>;
}

/** One column of fields inside a FieldGrid. */
export function FieldColumn({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.column}>{children}</div>;
}

interface FieldRowProps {
  label: string;
  children: ReactNode;
  /** Helper text under the value. */
  hint?: ReactNode;
  /** The id of the input the label belongs to, in edit mode. */
  htmlFor?: string;
  'data-testid'?: string;
}

/** "Caption ········ value", the Business Central card field layout. */
export function FieldRow({ label, children, hint, htmlFor, 'data-testid': testId }: FieldRowProps) {
  const styles = useStyles();
  const caption = (
    <>
      <span className={styles.labelText} title={label}>
        {label}
      </span>
      <span className={styles.leader} aria-hidden />
    </>
  );
  return (
    <div className={styles.row} data-testid={testId}>
      {htmlFor ? (
        <label className={styles.label} htmlFor={htmlFor}>
          {caption}
        </label>
      ) : (
        <div className={styles.label}>{caption}</div>
      )}
      <div className={styles.value}>
        <div>{children}</div>
        {hint && <div className={styles.hint}>{hint}</div>}
      </div>
    </div>
  );
}

/** A read-only value; empty values render as BC does, blank. */
export function Value({ children, mono }: { children: ReactNode; mono?: boolean }) {
  const styles = useStyles();
  const isEmpty = children === null || children === undefined || children === '';
  return <span className={mergeClasses(mono && styles.mono, isEmpty && styles.empty)}>{isEmpty ? '' : children}</span>;
}
