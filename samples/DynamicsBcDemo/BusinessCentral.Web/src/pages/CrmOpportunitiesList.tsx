import { Button, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { ArrowSync20Regular, DocumentAdd16Regular, Open16Regular } from '@fluentui/react-icons';
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, errorMessage, type CrmOpportunity } from '../api';
import { OpportunityStatusBadge, ProspectTag, QuoteStatusBadge } from '../components/Badges';
import { BcTable, type Column } from '../components/BcTable';
import { ListToolbar, PageBody, Panel } from '../components/Layout';
import { FilterChip, RecordCount, SearchInput } from '../components/ListControls';
import { PageHeader } from '../components/PageHeader';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, Notice, StaleNotice, type NoticeState } from '../components/States';
import { d365OpportunityUrl } from '../config';
import { formatDate, formatMoney } from '../format';
import { useCreateQuote } from '../hooks/useCreateQuote';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  aside: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  number: {
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  inline: {
    display: 'inline-flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  none: {
    color: tokens.colorNeutralForeground4,
  },
  more: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalXS,
  },
});

/** Open, and no sales quote linked yet: what's left for a BC user to quote. */
const isWithoutQuote = (o: CrmOpportunity) => o.status === 'Open' && o.quoteCount === 0;

function matches(o: CrmOpportunity, search: string): boolean {
  return [o.number, o.name, o.accountName, o.salespersonCode, o.productGroupName, o.productGroupCode, o.status, o.quoteNumber]
    .filter(Boolean)
    .join(' ')
    .toLowerCase()
    .includes(search.toLowerCase());
}

/** The latest sales quote linked to the opportunity, or a dash. */
function LatestQuote({ opportunity: o }: { opportunity: CrmOpportunity }) {
  const styles = useStyles();
  if (!o.quoteId || !o.quoteNumber) return <span className={styles.none}>—</span>;
  const older = o.quoteCount - 1;
  return (
    <span className={styles.inline}>
      <RouterLink to={`/quotes/${o.quoteId}`}>{o.quoteNumber}</RouterLink>
      {o.quoteStatus && <QuoteStatusBadge status={o.quoteStatus} size="small" />}
      {older > 0 && (
        <span className={styles.more} title={`${o.quoteCount} quotes are linked; this is the latest.`}>
          +{older}
        </span>
      )}
    </span>
  );
}

/**
 * CRM Opportunities: the AL extension table Dynamics 365 fills with every opportunity, so a BC user
 * can create the sales quote for one and link the two.
 */
export default function CrmOpportunitiesList() {
  const styles = useStyles();
  const [params, setParams] = useSearchParams();
  const unquotedOnly = params.get('unquoted') === '1';
  const [search, setSearch] = useState('');
  const [creatingId, setCreatingId] = useState<string | null>(null);
  const [notice, setNotice] = useState<NoticeState | null>(null);
  const { data, error, refresh } = usePolling((signal) => api.crmOpportunities(signal), 3000);
  const createQuote = useCreateQuote();

  const rows = useMemo(
    () => (data ?? []).filter((o) => (!unquotedOnly || isWithoutQuote(o)) && (!search || matches(o, search))),
    [data, unquotedOnly, search],
  );

  // On success the new quote's card opens; on failure the reason shows here.
  const create = async (opportunity: CrmOpportunity) => {
    setCreatingId(opportunity.id);
    setNotice(null);
    try {
      await createQuote(opportunity.id);
    } catch (e) {
      setNotice({ intent: 'error', title: 'Business Central', message: errorMessage(e) });
      setCreatingId(null);
      void refresh();
    }
  };

  const columns: Column<CrmOpportunity>[] = [
    { key: 'number', header: 'No.', width: '100px', render: (o) => <span className={styles.number}>{o.number}</span> },
    { key: 'name', header: 'Name', render: (o) => o.name },
    {
      key: 'account',
      header: 'Account',
      render: (o) => (
        <span className={styles.inline}>
          {o.accountName}
          {o.accountType === 'Prospect' && <ProspectTag />}
        </span>
      ),
    },
    { key: 'salesperson', header: 'Salesperson', width: '100px', render: (o) => o.salespersonCode },
    {
      key: 'productGroup',
      header: 'Product Group',
      width: '140px',
      render: (o) => o.productGroupName ?? o.productGroupCode ?? <span className={styles.none}>—</span>,
    },
    { key: 'value', header: 'Est. Value', width: '110px', align: 'end', render: (o) => formatMoney(o.estimatedValue, o.currencyCode) },
    { key: 'closeDate', header: 'Close Date', width: '110px', render: (o) => formatDate(o.estimatedCloseDate) },
    { key: 'status', header: 'Status', width: '80px', render: (o) => <OpportunityStatusBadge status={o.status} /> },
    { key: 'quote', header: 'Quote', width: '160px', render: (o) => <LatestQuote opportunity={o} /> },
    {
      key: 'actions',
      header: '',
      width: '190px',
      render: (o) => (
        <span className={styles.actions}>
          {o.status === 'Open' && (
            <Button
              size="small"
              icon={creatingId === o.id ? <Spinner size="tiny" /> : <DocumentAdd16Regular />}
              disabled={creatingId !== null}
              onClick={() => void create(o)}
              data-testid="create-quote"
              data-opportunity={o.number}
            >
              Create sales quote
            </Button>
          )}
          <Button
            as="a"
            size="small"
            appearance="subtle"
            icon={<Open16Regular />}
            href={d365OpportunityUrl(o.id)}
            target="_blank"
            rel="noopener noreferrer"
            title="Open in Dynamics 365"
            aria-label={`Open ${o.number} in Dynamics 365`}
            data-testid="open-in-d365"
            data-opportunity={o.number}
          />
        </span>
      ),
    },
  ];

  return (
    <>
      <PageHeader
        caption="CRM integration (AL extension)"
        title="CRM Opportunities"
        aside={
          <span className={styles.aside}>
            <ArrowSync20Regular aria-hidden /> Dynamics 365 sends every opportunity here through NimBus.
          </span>
        }
      />
      <PageBody>
        <Notice notice={notice} onDismiss={() => setNotice(null)} />
        {!data ? (
          error ? <LoadError error={error} onRetry={refresh} /> : <Loading />
        ) : (
          <>
            <StaleNotice error={error} />
            <Panel>
              <ListToolbar>
                <SearchInput value={search} onChange={setSearch} placeholder="Search CRM opportunities" />
                {unquotedOnly && <FilterChip label="Without quote" onClear={() => setParams({})} />}
                <RecordCount shown={rows.length} total={data.length} />
              </ListToolbar>
              <BcTable
                aria-label="CRM opportunities"
                data-testid="crm-opportunities-list"
                columns={columns}
                rows={rows}
                rowKey={(o) => o.id}
                empty={
                  data.length === 0
                    ? 'No CRM opportunities yet. Dynamics 365 sends every opportunity here when a seller creates or changes it.'
                    : 'No CRM opportunities match the filter.'
                }
              />
            </Panel>
          </>
        )}
      </PageBody>
    </>
  );
}
