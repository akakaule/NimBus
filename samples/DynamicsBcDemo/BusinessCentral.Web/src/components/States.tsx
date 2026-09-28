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
import { Dismiss20Regular } from '@fluentui/react-icons';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  loading: {
    display: 'flex',
    justifyContent: 'center',
    padding: `${tokens.spacingVerticalXXXL} 0`,
  },
  bar: {
    marginBottom: tokens.spacingVerticalM,
  },
});

/** Centred spinner for a page or part that has no data yet. */
export function Loading({ label = 'Loading…' }: { label?: string }) {
  const styles = useStyles();
  return (
    <div className={styles.loading}>
      <Spinner size="small" label={label} />
    </div>
  );
}

/** The first load failed: nothing to show but the reason and a retry. */
export function LoadError({ error, onRetry }: { error: Error; onRetry?: () => void }) {
  const styles = useStyles();
  return (
    <MessageBar intent="error" className={styles.bar} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Couldn't load the page</MessageBarTitle>
        {error.message}
      </MessageBarBody>
      {onRetry && (
        <MessageBarActions>
          <Button size="small" onClick={onRetry}>
            Try again
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}

/** A refresh failed but earlier data is still on screen; polling keeps retrying. */
export function StaleNotice({ error }: { error: Error | undefined }) {
  const styles = useStyles();
  if (!error) return null;
  return (
    <MessageBar intent="warning" className={styles.bar}>
      <MessageBarBody>
        <MessageBarTitle>Connection problem</MessageBarTitle>
        Showing the last loaded data and retrying. ({error.message})
      </MessageBarBody>
    </MessageBar>
  );
}

export interface NoticeState {
  intent: 'success' | 'error' | 'warning' | 'info';
  title?: string;
  message?: ReactNode;
}

/** The outcome of an action the user took, dismissable. */
export function Notice({ notice, onDismiss }: { notice: NoticeState | null; onDismiss: () => void }) {
  const styles = useStyles();
  if (!notice) return null;
  return (
    <MessageBar intent={notice.intent} className={styles.bar} layout="multiline" data-testid={`notice-${notice.intent}`}>
      <MessageBarBody>
        {notice.title && <MessageBarTitle>{notice.title}</MessageBarTitle>}
        {notice.message}
      </MessageBarBody>
      <MessageBarActions
        containerAction={
          <Button appearance="transparent" size="small" aria-label="Dismiss" icon={<Dismiss20Regular />} onClick={onDismiss} />
        }
      />
    </MessageBar>
  );
}
