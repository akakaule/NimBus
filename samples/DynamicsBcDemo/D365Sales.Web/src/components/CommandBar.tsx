import type { ReactNode } from 'react';
import { makeStyles, tokens, Toolbar } from '@fluentui/react-components';

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    minHeight: '44px',
    padding: '2px 8px',
    marginBottom: '12px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow2,
  },
  toolbar: {
    flexWrap: 'wrap',
    minWidth: 0,
    padding: 0,
  },
  aside: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexShrink: 0,
    paddingRight: '4px',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

interface CommandBarProps {
  label: string;
  children: ReactNode;
  /** Right-aligned content (e.g. a keyword filter). */
  aside?: ReactNode;
}

/** The command bar above every grid and form. */
export function CommandBar({ label, children, aside }: CommandBarProps) {
  const styles = useStyles();
  return (
    <div className={styles.bar}>
      <Toolbar aria-label={label} className={styles.toolbar}>
        {children}
      </Toolbar>
      {aside && <div className={styles.aside}>{aside}</div>}
    </div>
  );
}
