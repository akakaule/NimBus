import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Spinner, Tab, TabList, ToolbarButton, ToolbarDivider } from '@fluentui/react-components';
import { ArrowClockwise20Regular, ArrowLeft20Regular, DocumentArrowRight20Regular } from '@fluentui/react-icons';
import { api, errorMessage, type OpportunityDetail } from '../api';
import { BusinessProcessFlow } from '../components/BusinessProcessFlow';
import { CommandBar } from '../components/CommandBar';
import { FormHeader } from '../components/FormHeader';
import { IntegrationTrailMenu } from '../components/IntegrationTrailMenu';
import { ErrorState, LoadingState, StaleDataWarning } from '../components/PageStates';
import { OpportunityStatusBadge } from '../components/StatusBadges';
import { Timeline } from '../components/Timeline';
import { RecordLink, Section } from '../components/ui';
import { formatDate, formatMoney, orDash } from '../format';
import { useBack } from '../hooks/useBack';
import { useNotify } from '../hooks/useNotify';
import { usePolling } from '../hooks/usePolling';
import { isOpen } from '../model';
import { useCurrentUser } from '../user';
import { LinesTab } from './opportunity/LinesTab';
import { QuoteStatusStrip } from './opportunity/QuoteStatusStrip';
import { QuotesTab } from './opportunity/QuotesTab';
import { SummaryTab } from './opportunity/SummaryTab';

const TABS = ['summary', 'lines', 'quotes', 'timeline'] as const;
type TabValue = (typeof TABS)[number];
const toTab = (value: unknown): TabValue => (TABS as readonly unknown[]).includes(value) ? (value as TabValue) : 'summary';

interface BodyProps {
  detail: OpportunityDetail;
  tab: TabValue;
  onTab: (tab: TabValue) => void;
  refresh: () => Promise<void>;
}

function OpportunityBody({ detail, tab, onTab, refresh }: BodyProps) {
  const { opportunity: o, account, owner } = detail;

  return (
    <>
      <FormHeader
        title={o.name}
        subtitle={`Opportunity · ${o.csNumber}`}
        fields={[
          {
            label: 'Account',
            value: account ? <RecordLink to={`/accounts/${account.accountId}`}>{account.name}</RecordLink> : '—',
          },
          { label: 'Est. revenue', value: formatMoney(o.estimatedValue), testId: 'estimated-revenue' },
          { label: 'Est. close date', value: formatDate(o.estimatedCloseDate) },
          { label: 'Status', value: <OpportunityStatusBadge stateCode={o.stateCode} />, testId: 'opportunity-status' },
          { label: 'Owner', value: orDash(owner?.fullName) },
        ]}
      >
        <BusinessProcessFlow stepName={o.stepName} stateCode={o.stateCode} />
        <TabList selectedValue={tab} onTabSelect={(_, data) => onTab(toTab(data.value))}>
          <Tab value="summary">Summary</Tab>
          <Tab value="lines" data-testid="lines-tab">
            Product lines ({o.lines.length})
          </Tab>
          <Tab value="quotes" data-testid="quotes-tab">
            Quotes (Business Central)
          </Tab>
          <Tab value="timeline" data-testid="timeline-tab">
            Timeline
          </Tab>
        </TabList>
      </FormHeader>

      <QuoteStatusStrip opportunity={o} />

      {tab === 'summary' && <SummaryTab detail={detail} refresh={refresh} onShowTimeline={() => onTab('timeline')} />}
      {tab === 'lines' && <LinesTab opportunity={o} refresh={refresh} />}
      {tab === 'quotes' && <QuotesTab opportunity={o} quotes={detail.bcQuotes} />}
      {tab === 'timeline' && (
        <Section title="Timeline" caption="Seller actions and updates from Business Central, newest first">
          <Timeline entries={detail.timeline} emptyText="No activity on this opportunity yet" />
        </Section>
      )}
    </>
  );
}

export function OpportunityForm({ id }: { id: string }) {
  const poll = usePolling(`opportunity:${id}`, (signal) => api.opportunity(id, signal), 3000);
  const [searchParams, setSearchParams] = useSearchParams();
  const tab = toTab(searchParams.get('tab'));
  const back = useBack('/opportunities');
  const notify = useNotify();
  const { userId } = useCurrentUser();
  const [requestsInFlight, setRequestsInFlight] = useState(0);
  const opportunity = poll.data?.opportunity;

  const selectTab = (next: TabValue) =>
    setSearchParams(next === 'summary' ? {} : { tab: next }, { replace: true });

  const requestQuote = async () => {
    if (!opportunity) return;
    setRequestsInFlight((n) => n + 1);
    try {
      const result = await api.requestQuote(opportunity.opportunityId, userId);
      if (result.repeat) {
        notify.info(
          'Quote requested again',
          `Revision ${result.revision} was requested before, so the message id is the same (${result.messageId}). Business Central's inbox recognises it as a duplicate.`,
        );
      } else {
        notify.success(
          'Quote requested — Business Central will create it',
          `Product lines revision ${result.revision} · message ${result.messageId}`,
        );
      }
      await poll.refresh();
    } catch (error) {
      notify.error('The quote was not requested', errorMessage(error));
    } finally {
      setRequestsInFlight((n) => n - 1);
    }
  };

  const canRequest = opportunity !== undefined && isOpen(opportunity) && opportunity.lines.length > 0;

  return (
    <>
      <CommandBar label="Opportunity commands">
        <ToolbarButton icon={<ArrowLeft20Regular />} onClick={back} aria-label="Back" />
        <ToolbarButton icon={<ArrowClockwise20Regular />} onClick={() => void poll.refresh()}>
          Refresh
        </ToolbarButton>
        <ToolbarDivider />
        {/* Deliberately not disabled while a request is in flight: the talk track double-clicks to show
            the Business Central adapter's inbox discarding the duplicate (same MessageId). */}
        <ToolbarButton
          appearance="primary"
          icon={
            requestsInFlight > 0 ? <Spinner size="extra-tiny" appearance="inverted" /> : <DocumentArrowRight20Regular />
          }
          disabled={!canRequest}
          onClick={() => void requestQuote()}
          data-testid="request-quote"
        >
          Request quote in Business Central
        </ToolbarButton>
        {opportunity && <IntegrationTrailMenu accountId={opportunity.customerId} />}
      </CommandBar>
      {poll.loading ? (
        <LoadingState label="Loading the opportunity…" />
      ) : !poll.data ? (
        <ErrorState
          title="The opportunity could not be loaded"
          message={poll.error ?? ''}
          onRetry={() => void poll.refresh()}
        />
      ) : (
        <>
          <StaleDataWarning error={poll.error} />
          <OpportunityBody detail={poll.data} tab={tab} onTab={selectTab} refresh={poll.refresh} />
        </>
      )}
    </>
  );
}
