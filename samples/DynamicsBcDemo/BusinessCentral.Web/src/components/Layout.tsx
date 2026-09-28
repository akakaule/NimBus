import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  body: {
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXXL} ${tokens.spacingVerticalXXXL}`,
  },
  panel: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow2,
  },
  padded: {
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalXL}`,
  },
  toolbar: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

/** The content area under the page header. */
export function PageBody({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.body}>{children}</div>;
}

/** A white surface for a list or a card's FastTabs. */
export function Panel({ children, padded, className }: { children: ReactNode; padded?: boolean; className?: string }) {
  const styles = useStyles();
  return <div className={mergeClasses(styles.panel, padded && styles.padded, className)}>{children}</div>;
}

/** Search box and filter chips above a list. */
export function ListToolbar({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.toolbar}>{children}</div>;
}
