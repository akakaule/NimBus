import { useRef, useState } from 'react';
import {
  makeStyles,
  MessageBar,
  MessageBarBody,
  ToolbarButton,
  ToolbarDivider,
} from '@fluentui/react-components';
import {
  ArrowClockwise20Regular,
  ArrowLeft20Regular,
  LockClosed20Regular,
  Money20Regular,
  People20Regular,
  ShieldCheckmark20Regular,
} from '@fluentui/react-icons';
import { api, ApiError, errorMessage, type AccountDetail, type ContactSummary, type Opportunity } from '../api';
import { CommandBar } from '../components/CommandBar';
import { FormHeader } from '../components/FormHeader';
import { IntegrationTrailMenu } from '../components/IntegrationTrailMenu';
import { EmptyState, ErrorState, LoadingState, StaleDataWarning } from '../components/PageStates';
import { RecordGrid, type GridColumn } from '../components/RecordGrid';
import { OpportunityStatusBadge, QuoteStatusBadge, RelationshipBadge } from '../components/StatusBadges';
import { Timeline } from '../components/Timeline';
import { RecordLink, Section } from '../components/ui';
import { formatMoney, orDash } from '../format';
import { useBack } from '../hooks/useBack';
import { useNewItems } from '../hooks/useHighlight';
import { usePolling } from '../hooks/usePolling';
import { isBcManagedProspect, isBcOwned, stageLabel } from '../model';
import { useCurrentUser } from '../user';
import { AccountInfoSection } from './account/AccountInfoSection';
import { BusinessCentralSection } from './account/BusinessCentralSection';
import { CreditCheckDialog, type CreditCheck } from './account/CreditCheckDialog';

const useStyles = makeStyles({
  banner: {
    marginBottom: '12px',
  },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.25fr) minmax(0, 1fr)',
    gap: '12px',
    alignItems: 'start',
    '@media (max-width: 1100px)': {
      gridTemplateColumns: 'minmax(0, 1fr)',
    },
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    minWidth: 0,
  },
  timeline: {
    maxHeight: '520px',
    overflowY: 'auto',
  },
});

const opportunityColumns: GridColumn<Opportunity>[] = [
  {
    id: 'topic',
    header: 'Opportunity',
    title: (o) => `${o.csNumber} · ${o.name}`,
    render: (o) => (
      <RecordLink to={`/opportunities/${o.opportunityId}`}>
        {o.csNumber} · {o.name}
      </RecordLink>
    ),
  },
  { id: 'stage', header: 'Stage', width: '80px', render: (o) => stageLabel(o.stepName) },
  { id: 'value', header: 'Est. revenue', numeric: true, width: '112px', render: (o) => formatMoney(o.estimatedValue) },
  {
    id: 'quote',
    header: 'BC quote',
    width: '96px',
    render: (o) => (o.csBcQuoteStatus ? <QuoteStatusBadge status={o.csBcQuoteStatus} /> : '—'),
  },
  { id: 'status', header: 'Status', width: '72px', render: (o) => <OpportunityStatusBadge stateCode={o.stateCode} /> },
];

const contactColumns: GridColumn<ContactSummary>[] = [
  { id: 'name', header: 'Full name', width: '26%', title: (c) => c.fullName, render: (c) => c.fullName },
  { id: 'title', header: 'Job title', title: (c) => c.jobTitle, render: (c) => orDash(c.jobTitle) },
  { id: 'email', header: 'Email', width: '34%', title: (c) => c.emailAddress1, render: (c) => orDash(c.emailAddress1) },
  { id: 'phone', header: 'Phone', width: '132px', render: (c) => orDash(c.telephone1) },
];

function AccountBody({ detail, refresh }: { detail: AccountDetail; refresh: () => Promise<void> }) {
  const styles = useStyles();
  const { account, owner } = detail;
  const bcOwned = isBcOwned(account);
  const freshOpportunities = useNewItems(detail.opportunities.map((o) => o.opportunityId));

  return (
    <>
      <FormHeader
        title={account.name}
        subtitle={`Account · ${account.accountNumber ?? 'no customer number yet'}`}
        badges={<RelationshipBadge code={account.customerTypeCode} testId="relationship-type" />}
        fields={[
          { label: 'Account number', value: orDash(account.accountNumber), testId: 'account-number' },
          { label: 'Master data', value: bcOwned ? 'Business Central' : 'Dynamics 365', testId: 'master-data-owner' },
          { label: 'Credit limit', value: formatMoney(account.creditLimit) },
          { label: 'Owner', value: orDash(owner?.fullName) },
        ]}
      />
      {bcOwned && (
        <MessageBar intent="info" icon={<LockClosed20Regular />} className={styles.banner} data-testid="owned-by-bc-banner">
          <MessageBarBody>
            {isBcManagedProspect(account)
              ? "Business Central manages this account's master data since its first quote."
              : "Business Central owns this customer's master data."}{' '}
            These fields are read-only here — change them in Business Central.
          </MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.columns}>
        <div className={styles.stack}>
          <AccountInfoSection account={account} refresh={refresh} />
          <Section title="Opportunities" caption={`${detail.opportunities.length} on this account`}>
            <RecordGrid
              ariaLabel="Opportunities"
              columns={opportunityColumns}
              rows={detail.opportunities}
              rowKey={(o) => o.opportunityId}
              fresh={freshOpportunities}
              empty={<EmptyState icon={<Money20Regular />} title="No opportunities" />}
              testId="account-opportunities"
            />
          </Section>
          <Section title="Contacts" caption="Read-only">
            <RecordGrid
              ariaLabel="Contacts"
              columns={contactColumns}
              rows={detail.contacts}
              rowKey={(c) => c.contactId}
              empty={<EmptyState icon={<People20Regular />} title="No contacts" />}
              testId="account-contacts"
            />
          </Section>
        </div>
        <div className={styles.stack}>
          <BusinessCentralSection account={account} />
          <Section title="Timeline">
            <div className={styles.timeline}>
              <Timeline entries={detail.timeline} emptyText="No activity on this account yet" />
            </div>
          </Section>
        </div>
      </div>
    </>
  );
}

export function AccountForm({ id }: { id: string }) {
  const poll = usePolling(`account:${id}`, (signal) => api.account(id, signal), 3000);
  const back = useBack('/accounts');
  const { userId } = useCurrentUser();
  const [creditOpen, setCreditOpen] = useState(false);
  const [creditCheck, setCreditCheck] = useState<CreditCheck | null>(null);
  const latestCheck = useRef(0);
  const account = poll.data?.account;

  const checkCredit = async () => {
    if (!account) return;
    const attempt = ++latestCheck.current;
    setCreditCheck({ phase: 'asking' });
    setCreditOpen(true);
    const started = performance.now();
    const elapsed = () => Math.round(performance.now() - started);
    try {
      const status = await api.checkCredit(account.accountId, userId);
      if (attempt === latestCheck.current) setCreditCheck({ phase: 'answered', status, roundTripMs: elapsed() });
    } catch (error) {
      if (attempt === latestCheck.current) {
        setCreditCheck({
          phase: 'failed',
          message: errorMessage(error),
          httpStatus: error instanceof ApiError ? error.status : 0,
          roundTripMs: elapsed(),
        });
      }
    }
  };

  return (
    <>
      <CommandBar label="Account commands">
        <ToolbarButton icon={<ArrowLeft20Regular />} onClick={back} aria-label="Back" />
        <ToolbarButton icon={<ArrowClockwise20Regular />} onClick={() => void poll.refresh()}>
          Refresh
        </ToolbarButton>
        <ToolbarDivider />
        <ToolbarButton
          appearance="primary"
          icon={<ShieldCheckmark20Regular />}
          disabled={!account?.csBcCustomerId}
          onClick={() => void checkCredit()}
          data-testid="check-credit"
        >
          Check credit in Business Central
        </ToolbarButton>
        {account && <IntegrationTrailMenu accountId={account.accountId} />}
      </CommandBar>
      {poll.loading ? (
        <LoadingState label="Loading the account…" />
      ) : !poll.data ? (
        <ErrorState title="The account could not be loaded" message={poll.error ?? ''} onRetry={() => void poll.refresh()} />
      ) : (
        <>
          <StaleDataWarning error={poll.error} />
          <AccountBody detail={poll.data} refresh={poll.refresh} />
        </>
      )}
      <CreditCheckDialog
        open={creditOpen}
        accountName={account?.name ?? ''}
        check={creditCheck}
        onClose={() => {
          latestCheck.current += 1;
          setCreditOpen(false);
        }}
        onCheckAgain={() => void checkCredit()}
      />
    </>
  );
}
