import { Button, makeStyles, mergeClasses, tokens, type ButtonProps } from '@fluentui/react-components';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  root: {
    backgroundColor: tokens.colorNeutralBackground1,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalXXL} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  caption: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalM,
    minHeight: '36px',
  },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  actionBar: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXXS,
    minHeight: '44px',
    padding: `${tokens.spacingVerticalXS} 0`,
    marginLeft: `calc(${tokens.spacingHorizontalS} * -1)`,
  },
  aside: {
    marginLeft: 'auto',
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  noActions: {
    paddingBottom: tokens.spacingVerticalM,
  },
  actionButton: {
    '& .fui-Button__icon': {
      color: tokens.colorBrandForeground1,
    },
    ':disabled .fui-Button__icon, &[aria-disabled="true"] .fui-Button__icon': {
      color: tokens.colorNeutralForegroundDisabled,
    },
  },
});

interface PageHeaderProps {
  /** Small caption above the title, e.g. "Sales Quote". */
  caption?: string;
  title: ReactNode;
  /** Shown next to the title, e.g. the status badge. */
  badges?: ReactNode;
  /** The action bar. */
  actions?: ReactNode;
  /** Right-hand side of the action bar. */
  aside?: ReactNode;
}

/** Title area and action bar of a Business Central list or card page. */
export function PageHeader({ caption, title, badges, actions, aside }: PageHeaderProps) {
  const styles = useStyles();
  const hasBar = Boolean(actions || aside);
  return (
    <div className={mergeClasses(styles.root, !hasBar && styles.noActions)}>
      {caption && <div className={styles.caption}>{caption}</div>}
      <div className={styles.titleRow}>
        <h1 className={styles.title}>{title}</h1>
        {badges}
      </div>
      {hasBar && (
        <div className={styles.actionBar} role="toolbar" aria-label="Actions">
          {actions}
          {aside && <div className={styles.aside}>{aside}</div>}
        </div>
      )}
    </div>
  );
}

/** A flat action-bar button with a brand-coloured icon, like the BC action bar. */
export function ActionButton({ className, ...props }: ButtonProps) {
  const styles = useStyles();
  return <Button appearance="subtle" {...props} className={mergeClasses(styles.actionButton, className)} />;
}
