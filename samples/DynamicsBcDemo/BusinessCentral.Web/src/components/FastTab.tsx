import { makeStyles, tokens } from '@fluentui/react-components';
import { ChevronDown20Regular, ChevronRight20Regular } from '@fluentui/react-icons';
import { useId, useState, type ReactNode } from 'react';

const useStyles = makeStyles({
  root: {
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    width: '100%',
    minHeight: '48px',
    padding: `${tokens.spacingVerticalS} 0`,
    border: 'none',
    background: 'none',
    cursor: 'pointer',
    textAlign: 'left',
    color: tokens.colorNeutralForeground1,
    fontFamily: tokens.fontFamilyBase,
    ':hover': {
      color: tokens.colorBrandForeground1,
    },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '-2px',
    },
  },
  chevron: {
    color: tokens.colorBrandForeground1,
    flexShrink: 0,
  },
  title: {
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  caption: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: `0 ${tokens.spacingHorizontalS}`,
    lineHeight: '20px',
  },
  summary: {
    marginLeft: 'auto',
    paddingLeft: tokens.spacingHorizontalL,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase300,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    minWidth: 0,
  },
  body: {
    padding: `${tokens.spacingVerticalXS} 0 ${tokens.spacingVerticalXL} 28px`,
  },
});

interface FastTabProps {
  title: string;
  /** Small tag next to the title, e.g. "AL extension fields". */
  caption?: string;
  /** Key values shown next to the title while the FastTab is collapsed. */
  summary?: ReactNode;
  defaultOpen?: boolean;
  children: ReactNode;
  'data-testid'?: string;
}

/** A collapsible section of a Business Central card page. */
export function FastTab({ title, caption, summary, defaultOpen = true, children, 'data-testid': testId }: FastTabProps) {
  const styles = useStyles();
  const [open, setOpen] = useState(defaultOpen);
  const bodyId = useId();

  return (
    <section className={styles.root} data-testid={testId}>
      <button
        type="button"
        className={styles.header}
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={() => setOpen((value) => !value)}
      >
        {open ? (
          <ChevronDown20Regular className={styles.chevron} aria-hidden />
        ) : (
          <ChevronRight20Regular className={styles.chevron} aria-hidden />
        )}
        <span className={styles.title}>{title}</span>
        {caption && <span className={styles.caption}>{caption}</span>}
        {!open && summary && <span className={styles.summary}>{summary}</span>}
      </button>
      {open && (
        <div id={bodyId} className={styles.body}>
          {children}
        </div>
      )}
    </section>
  );
}
