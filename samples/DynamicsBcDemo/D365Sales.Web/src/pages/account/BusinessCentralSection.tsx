import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { Account } from '../../api';
import { BlockedBadge } from '../../components/StatusBadges';
import { FieldRow, Section } from '../../components/ui';
import { formatDateTime, formatMoney, orDash } from '../../format';
import { useChangeHighlight } from '../../hooks/useHighlight';

const MIRRORED = 'Maintained in Business Central and mirrored here by the integration.';

const useStyles = makeStyles({
  highlight: {
    animationName: {
      from: { backgroundColor: tokens.colorBrandBackground2 },
      to: { backgroundColor: tokens.colorNeutralBackground1 },
    },
    animationDuration: '2.6s',
    animationTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },
  prospect: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground2,
  },
  contact: {
    color: tokens.colorNeutralForeground1,
  },
  strong: {
    fontWeight: tokens.fontWeightSemibold,
  },
});

/** What Business Central knows about the account: its customer data, or that it is only a prospect. */
export function BusinessCentralSection({ account }: { account: Account }) {
  const styles = useStyles();
  const highlight = useChangeHighlight(account.csBcLastSyncedOn ?? account.csBcContactNumber);
  const isCustomer = account.csBcCustomerId !== null;

  return (
    <Section
      title="Business Central"
      caption={isCustomer ? 'Customer data mirrored from Business Central — read-only' : undefined}
      className={mergeClasses(highlight && styles.highlight)}
      testId="business-central-section"
    >
      {isCustomer ? (
        <>
          <FieldRow label="Customer no." lockedReason={MIRRORED}>
            {orDash(account.accountNumber)}
          </FieldRow>
          <FieldRow label="Credit limit" lockedReason={MIRRORED}>
            {formatMoney(account.creditLimit)}
          </FieldRow>
          <FieldRow label="Balance due" lockedReason={MIRRORED}>
            {formatMoney(account.csBcBalanceDue)}
          </FieldRow>
          <FieldRow label="Blocked" lockedReason={MIRRORED}>
            <BlockedBadge blocked={account.csBcBlocked} />
          </FieldRow>
          <FieldRow label="Payment terms" lockedReason={MIRRORED}>
            {orDash(account.csBcPaymentTerms)}
          </FieldRow>
          <FieldRow label="Last synced" lockedReason={MIRRORED}>
            {formatDateTime(account.csBcLastSyncedOn)}
          </FieldRow>
          {account.csBcContactNumber && (
            <FieldRow label="Converted from" lockedReason={MIRRORED}>
              Prospect contact {account.csBcContactNumber}
            </FieldRow>
          )}
        </>
      ) : (
        <div className={styles.prospect} data-testid="bc-prospect-note">
          <span>
            Not a Business Central customer yet — it becomes one when its first quote turns into an order in Business
            Central.
          </span>
          {account.csBcContactNumber ? (
            <span className={styles.contact}>
              Known to Business Central as prospect contact{' '}
              <span className={styles.strong}>{account.csBcContactNumber}</span>
            </span>
          ) : (
            <span>Business Central has not heard of this prospect; it will with the first quote request.</span>
          )}
        </div>
      )}
    </Section>
  );
}
