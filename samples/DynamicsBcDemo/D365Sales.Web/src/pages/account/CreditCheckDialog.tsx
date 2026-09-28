import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise20Regular } from '@fluentui/react-icons';
import type { BcCreditStatus } from '../../api';
import { BlockedBadge } from '../../components/StatusBadges';
import { FieldRow } from '../../components/ui';
import { formatDateTime, formatMoney, orDash } from '../../format';

export type CreditCheck =
  | { phase: 'asking' }
  | { phase: 'answered'; status: BcCreditStatus; roundTripMs: number }
  | { phase: 'failed'; message: string; httpStatus: number; roundTripMs: number };

const useStyles = makeStyles({
  surface: {
    maxWidth: '520px',
  },
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  asking: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '10px',
    padding: '24px 0',
    textAlign: 'center',
  },
  askingNote: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    maxWidth: '360px',
  },
  figures: {
    padding: '4px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  roundTrip: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  strong: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
});

function RoundTrip({ ms }: { ms: number }) {
  const styles = useStyles();
  return (
    <span className={styles.roundTrip} data-testid="credit-round-trip">
      Round trip <span className={styles.strong}>{ms} ms</span> · Sales Hub → NimBus request/reply → Business Central
      and back
    </span>
  );
}

function Answer({ status, roundTripMs }: { status: BcCreditStatus; roundTripMs: number }) {
  const styles = useStyles();

  if (status.status === 'Unavailable') {
    return (
      <>
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Business Central is unavailable</MessageBarTitle>
            {status.reason ?? 'Business Central could not answer right now. Try again in a moment.'}
          </MessageBarBody>
        </MessageBar>
        <RoundTrip ms={roundTripMs} />
      </>
    );
  }

  if (status.status === 'NotFound') {
    return (
      <>
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              Business Central has no customer {status.customerNumber ?? 'with this number'}
            </MessageBarTitle>
            {status.reason ?? 'The customer may have been deleted or renumbered in Business Central.'}
          </MessageBarBody>
        </MessageBar>
        <RoundTrip ms={roundTripMs} />
      </>
    );
  }

  return (
    <>
      <MessageBar intent="success" layout="multiline">
        <MessageBarBody>
          Answered live by Business Central at {formatDateTime(status.checkedAt, true)}
        </MessageBarBody>
      </MessageBar>
      <div className={styles.figures}>
        <FieldRow label="Customer no.">{orDash(status.customerNumber)}</FieldRow>
        <FieldRow label="Credit limit">{formatMoney(status.creditLimit)}</FieldRow>
        <FieldRow label="Balance due">{formatMoney(status.balanceDue)}</FieldRow>
        <FieldRow label="Available credit">
          <span className={styles.strong}>{formatMoney(status.availableCredit)}</span>
        </FieldRow>
        <FieldRow label="Overdue">{formatMoney(status.overdueAmount)}</FieldRow>
        <FieldRow label="Blocked">
          <BlockedBadge blocked={status.blocked} />
        </FieldRow>
      </div>
      <RoundTrip ms={roundTripMs} />
    </>
  );
}

interface CreditCheckDialogProps {
  open: boolean;
  accountName: string;
  check: CreditCheck | null;
  onClose: () => void;
  onCheckAgain: () => void;
}

/** Business Central's live answer to "may we sell to this customer?". */
export function CreditCheckDialog({ open, accountName, check, onClose, onCheckAgain }: CreditCheckDialogProps) {
  const styles = useStyles();
  const asking = check?.phase === 'asking';

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface className={styles.surface} data-testid="credit-check-dialog">
        <DialogBody>
          <DialogTitle>Credit check · {accountName}</DialogTitle>
          <DialogContent className={styles.content} data-testid="credit-check-result" data-phase={check?.phase}>
            {(!check || check.phase === 'asking') && (
              <div className={styles.asking}>
                <Spinner size="small" label="Asking Business Central…" />
                <span className={styles.askingNote}>
                  A live request/reply over NimBus. Nothing is cached in Dynamics 365.
                </span>
              </div>
            )}
            {check?.phase === 'answered' && <Answer status={check.status} roundTripMs={check.roundTripMs} />}
            {check?.phase === 'failed' && (
              <>
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>
                      {check.httpStatus === 504 ? 'No answer from Business Central' : 'The credit check failed'}
                    </MessageBarTitle>
                    {check.message}
                  </MessageBarBody>
                </MessageBar>
                <RoundTrip ms={check.roundTripMs} />
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button icon={<ArrowClockwise20Regular />} disabled={asking} onClick={onCheckAgain}>
              Check again
            </Button>
            <Button appearance="primary" onClick={onClose}>
              Close
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
