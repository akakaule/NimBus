import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  ToolbarButton,
  ToolbarDivider,
} from '@fluentui/react-components';
import { ArrowClockwise20Regular, ArrowLeft20Regular, CheckmarkCircle20Regular } from '@fluentui/react-icons';
import { api, errorMessage, type Lead, type LeadDetail } from '../api';
import { CommandBar } from '../components/CommandBar';
import { FormHeader } from '../components/FormHeader';
import { ErrorState, LoadingState, StaleDataWarning } from '../components/PageStates';
import { LeadStatusBadge } from '../components/StatusBadges';
import { Timeline } from '../components/Timeline';
import { FieldRow, RecordLink, Section } from '../components/ui';
import { formatDate, formatMoney, orDash } from '../format';
import { useBack } from '../hooks/useBack';
import { useNotify } from '../hooks/useNotify';
import { usePolling } from '../hooks/usePolling';
import { isOpen, StateCode } from '../model';
import { useCurrentUser } from '../user';

const useStyles = makeStyles({
  banner: {
    marginBottom: '12px',
  },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.2fr) minmax(0, 1fr)',
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
});

function QualificationBanner({ lead }: { lead: Lead }) {
  const styles = useStyles();
  if (lead.stateCode === StateCode.WonOrQualified) {
    return (
      <MessageBar intent="success" className={styles.banner} data-testid="lead-qualified-banner">
        <MessageBarBody>
          <MessageBarTitle>Qualified</MessageBarTitle>
          {lead.qualifiedOpportunityId ? (
            <>
              Continued as opportunity{' '}
              <RecordLink to={`/opportunities/${lead.qualifiedOpportunityId}`}>
                {lead.qualifiedOpportunityNumber ?? 'Open opportunity'}
              </RecordLink>
              {lead.qualifiedAccountId && (
                <>
                  {' '}
                  for account <RecordLink to={`/accounts/${lead.qualifiedAccountId}`}>{lead.companyName}</RecordLink>
                </>
              )}
              .
            </>
          ) : (
            'This lead was qualified into an account, a contact and an opportunity.'
          )}
        </MessageBarBody>
      </MessageBar>
    );
  }
  if (lead.stateCode === StateCode.LostOrDisqualified) {
    return (
      <MessageBar intent="warning" className={styles.banner}>
        <MessageBarBody>This lead was disqualified.</MessageBarBody>
      </MessageBar>
    );
  }
  return (
    <MessageBar intent="info" className={styles.banner}>
      <MessageBarBody>
        Qualifying creates a prospect account, a contact and an opportunity in Dynamics 365 only. Business Central
        first hears about the prospect when a quote is requested.
      </MessageBarBody>
    </MessageBar>
  );
}

function LeadBody({ detail }: { detail: LeadDetail }) {
  const styles = useStyles();
  const { nameOf } = useCurrentUser();
  const { lead } = detail;
  const contactName = `${lead.firstName} ${lead.lastName}`.trim();

  return (
    <>
      <FormHeader
        title={lead.subject}
        subtitle={`Lead · ${lead.companyName}`}
        fields={[
          { label: 'Est. value', value: formatMoney(lead.estimatedValue) },
          { label: 'Status', value: <LeadStatusBadge stateCode={lead.stateCode} /> },
          { label: 'Owner', value: orDash(nameOf(lead.ownerId)) },
          { label: 'Created on', value: formatDate(lead.createdOn) },
        ]}
      />
      <QualificationBanner lead={lead} />
      <div className={styles.columns}>
        <div className={styles.stack}>
          <Section title="Contact">
            <FieldRow label="Name">{orDash(contactName)}</FieldRow>
            <FieldRow label="Job title">{orDash(lead.jobTitle)}</FieldRow>
            <FieldRow label="Email">{orDash(lead.emailAddress1)}</FieldRow>
            <FieldRow label="Phone">{orDash(lead.telephone1)}</FieldRow>
          </Section>
          <Section title="Company">
            <FieldRow label="Company name">{lead.companyName}</FieldRow>
            <FieldRow label="Website">{orDash(lead.websiteUrl)}</FieldRow>
            <FieldRow label="VAT number">{orDash(lead.csVatNumber)}</FieldRow>
            <FieldRow label="Address">{orDash(lead.address1Line1)}</FieldRow>
            <FieldRow label="City">{orDash(lead.address1City)}</FieldRow>
            <FieldRow label="Postal code">{orDash(lead.address1PostalCode)}</FieldRow>
            <FieldRow label="Country/region">{orDash(lead.address1Country)}</FieldRow>
          </Section>
        </div>
        <Section title="Timeline">
          <Timeline entries={detail.timeline} emptyText="No activity on this lead yet" />
        </Section>
      </div>
    </>
  );
}

export function LeadForm({ id }: { id: string }) {
  const poll = usePolling(`lead:${id}`, (signal) => api.lead(id, signal));
  const back = useBack('/leads');
  const navigate = useNavigate();
  const notify = useNotify();
  const { userId } = useCurrentUser();
  const [qualifying, setQualifying] = useState(false);
  const lead = poll.data?.lead;

  const qualify = async () => {
    if (!lead) return;
    setQualifying(true);
    try {
      const result = await api.qualifyLead(lead.leadId, userId);
      notify.success(
        'Lead qualified',
        'Qualified: account (Prospect), contact and opportunity created — nothing was sent to Business Central.',
      );
      navigate(`/opportunities/${result.opportunityId}`);
    } catch (error) {
      notify.error('The lead was not qualified', errorMessage(error));
      setQualifying(false);
      void poll.refresh();
    }
  };

  return (
    <>
      <CommandBar label="Lead commands">
        <ToolbarButton icon={<ArrowLeft20Regular />} onClick={back} aria-label="Back" />
        <ToolbarButton icon={<ArrowClockwise20Regular />} onClick={() => void poll.refresh()}>
          Refresh
        </ToolbarButton>
        <ToolbarDivider />
        <ToolbarButton
          appearance="primary"
          icon={qualifying ? <Spinner size="extra-tiny" appearance="inverted" /> : <CheckmarkCircle20Regular />}
          disabled={!lead || !isOpen(lead) || qualifying}
          onClick={() => void qualify()}
          data-testid="qualify"
        >
          Qualify
        </ToolbarButton>
      </CommandBar>
      {poll.loading ? (
        <LoadingState label="Loading the lead…" />
      ) : !poll.data ? (
        <ErrorState title="The lead could not be loaded" message={poll.error ?? ''} onRetry={() => void poll.refresh()} />
      ) : (
        <>
          <StaleDataWarning error={poll.error} />
          <LeadBody detail={poll.data} />
        </>
      )}
    </>
  );
}
