import type { ReactNode } from 'react';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise20Regular } from '@fluentui/react-icons';

const useStyles = makeStyles({
  center: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '180px',
    padding: '24px',
  },
  empty: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '6px',
    padding: '28px 16px',
    textAlign: 'center',
    color: tokens.colorNeutralForeground3,
  },
  emptyIcon: {
    fontSize: '28px',
    color: tokens.colorNeutralForeground4,
    display: 'flex',
  },
  emptyTitle: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  emptyText: {
    fontSize: tokens.fontSizeBase200,
    maxWidth: '420px',
  },
  bar: {
    marginBottom: '12px',
  },
});

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  const styles = useStyles();
  return (
    <div className={styles.center}>
      <Spinner size="small" label={label} />
    </div>
  );
}

interface ErrorStateProps {
  title: string;
  message: string;
  onRetry?: () => void;
}

export function ErrorState({ title, message, onRetry }: ErrorStateProps) {
  const styles = useStyles();
  return (
    <MessageBar intent="error" layout="multiline" className={styles.bar}>
      <MessageBarBody>
        <MessageBarTitle>{title}</MessageBarTitle>
        {message}
      </MessageBarBody>
      {onRetry && (
        <MessageBarActions>
          <Button size="small" icon={<ArrowClockwise20Regular />} onClick={onRetry}>
            Try again
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}

interface EmptyStateProps {
  icon?: ReactNode;
  title: string;
  children?: ReactNode;
}

export function EmptyState({ icon, title, children }: EmptyStateProps) {
  const styles = useStyles();
  return (
    <div className={styles.empty}>
      {icon && <span className={styles.emptyIcon}>{icon}</span>}
      <span className={styles.emptyTitle}>{title}</span>
      {children && <span className={styles.emptyText}>{children}</span>}
    </div>
  );
}

/** Shown above data that is still on screen while the latest refresh failed. */
export function StaleDataWarning({ error }: { error: string | undefined }) {
  const styles = useStyles();
  if (!error) return null;
  return (
    <MessageBar intent="warning" className={styles.bar}>
      <MessageBarBody>
        <MessageBarTitle>Showing the last data received</MessageBarTitle>
        {error} Retrying automatically.
      </MessageBarBody>
    </MessageBar>
  );
}
