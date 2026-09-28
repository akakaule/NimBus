import type { ReactNode } from 'react';
import { Label, Link, makeStyles, mergeClasses, tokens, Tooltip } from '@fluentui/react-components';
import { LockClosed16Regular, Open16Regular } from '@fluentui/react-icons';
import { Link as RouterLink } from 'react-router-dom';

const useStyles = makeStyles({
  card: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow2,
    minWidth: 0,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    padding: '14px 20px 18px',
  },
  sectionHead: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    minHeight: '32px',
    marginBottom: '6px',
  },
  sectionTitles: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  sectionTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  sectionCaption: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
  },
  sectionActions: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexShrink: 0,
  },
  fieldRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(120px, 36%) minmax(0, 1fr)',
    alignItems: 'center',
    columnGap: '12px',
    minHeight: '36px',
    paddingTop: '2px',
    paddingBottom: '2px',
  },
  fieldLabel: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    minWidth: 0,
    color: tokens.colorNeutralForeground2,
  },
  lock: {
    display: 'inline-flex',
    flexShrink: 0,
    color: tokens.colorNeutralForeground3,
  },
  fieldValue: {
    minWidth: 0,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground1,
    overflowWrap: 'anywhere',
  },
  routerLink: {
    color: tokens.colorBrandForegroundLink,
    textDecorationLine: 'none',
    ':hover': {
      color: tokens.colorBrandForegroundLinkHover,
      textDecorationLine: 'underline',
    },
    ':focus-visible': {
      outlineStyle: 'solid',
      outlineWidth: '2px',
      outlineColor: tokens.colorStrokeFocus2,
      borderRadius: tokens.borderRadiusSmall,
    },
  },
  externalIcon: {
    marginLeft: '4px',
    verticalAlign: 'text-bottom',
  },
});

export function Card({ children, className, testId }: { children: ReactNode; className?: string; testId?: string }) {
  const styles = useStyles();
  return (
    <div className={mergeClasses(styles.card, className)} data-testid={testId}>
      {children}
    </div>
  );
}

interface SectionProps {
  title: ReactNode;
  caption?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
  testId?: string;
}

/** A white form section, like the sections on a model-driven form tab. */
export function Section({ title, caption, actions, children, className, testId }: SectionProps) {
  const styles = useStyles();
  return (
    <section className={mergeClasses(styles.card, styles.section, className)} data-testid={testId}>
      <div className={styles.sectionHead}>
        <div className={styles.sectionTitles}>
          <h2 className={styles.sectionTitle}>{title}</h2>
          {caption && <span className={styles.sectionCaption}>{caption}</span>}
        </div>
        {actions && <div className={styles.sectionActions}>{actions}</div>}
      </div>
      {children}
    </section>
  );
}

interface FieldRowProps {
  label: string;
  /** The id of the input the label belongs to. */
  htmlFor?: string;
  /** Shows a lock icon with this explanation: the value is owned elsewhere. */
  lockedReason?: string;
  children: ReactNode;
}

/** Label on the left, value or input on the right. */
export function FieldRow({ label, htmlFor, lockedReason, children }: FieldRowProps) {
  const styles = useStyles();
  return (
    <div className={styles.fieldRow}>
      <div className={styles.fieldLabel}>
        {lockedReason && (
          <Tooltip content={lockedReason} relationship="description" withArrow>
            <span className={styles.lock} role="img" tabIndex={0} aria-label="Read-only" data-testid="locked-field">
              <LockClosed16Regular />
            </span>
          </Tooltip>
        )}
        <Label htmlFor={htmlFor} weight="regular">
          {label}
        </Label>
      </div>
      <div className={styles.fieldValue}>{children}</div>
    </div>
  );
}

export function RecordLink({ to, children }: { to: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <RouterLink to={to} className={styles.routerLink}>
      {children}
    </RouterLink>
  );
}

/** A link that opens another app (Business Central, nimbus-ops) in a new tab. */
export function ExternalLink({ href, children, testId }: { href: string; children: ReactNode; testId?: string }) {
  const styles = useStyles();
  return (
    <Link href={href} target="_blank" rel="noopener noreferrer" data-testid={testId}>
      {children}
      <Open16Regular className={styles.externalIcon} aria-label="(opens in a new tab)" />
    </Link>
  );
}
