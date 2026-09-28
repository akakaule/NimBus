import { makeStyles, tokens } from '@fluentui/react-components';
import { DocumentAdd20Regular } from '@fluentui/react-icons';
import { useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, type QuoteSummary } from '../api';
import { ProspectTag, QuoteStatusBadge } from '../components/Badges';
import { BcTable, type Column } from '../components/BcTable';
import { ListToolbar, PageBody, Panel } from '../components/Layout';
import { FilterChip, RecordCount, SearchInput } from '../components/ListControls';
import { NewQuoteDialog } from '../components/NewQuoteDialog';
import { ActionButton, PageHeader } from '../components/PageHeader';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, StaleNotice } from '../components/States';
import { formatDate, formatMoney } from '../format';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  sellTo: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: `${tokens.spacingVerticalXXS} 0`,
  },
  secondary: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

function SellTo({ quote }: { quote: QuoteSummary }) {
  const styles = useStyles();
  return (
    <div className={styles.sellTo}>
      <span>{quote.sellToName}</span>
      <span className={styles.secondary}>
        {quote.sellToType} {quote.sellToNumber}
        {quote.sellToType === 'Contact' && <ProspectTag />}
      </span>
    </div>
  );
}

const columns: Column<QuoteSummary>[] = [
  { key: 'number', header: 'No.', width: '110px', render: (q) => <RouterLink to={`/quotes/${q.id}`}>{q.number}</RouterLink> },
  { key: 'sellTo', header: 'Sell-to', render: (q) => <SellTo quote={q} /> },
  { key: 'external', header: 'External Document No.', width: '160px', render: (q) => q.externalDocumentNumber },
  { key: 'salesperson', header: 'Salesperson', width: '110px', render: (q) => q.salespersonCode },
  { key: 'status', header: 'Status', width: '110px', render: (q) => <QuoteStatusBadge status={q.status} /> },
  {
    key: 'amount',
    header: 'Amount Excl. Tax',
    width: '150px',
    align: 'end',
    render: (q) => formatMoney(q.totalAmountExcludingTax, q.currencyCode),
  },
  { key: 'validUntil', header: 'Valid Until', width: '130px', render: (q) => formatDate(q.validUntilDate) },
  { key: 'order', header: 'Order No.', width: '120px', render: (q) => q.orderNumber },
];

function matches(quote: QuoteSummary, search: string): boolean {
  const text = [
    quote.number,
    quote.sellToName,
    quote.sellToNumber,
    quote.externalDocumentNumber,
    quote.salespersonCode,
    quote.description,
    quote.orderNumber,
  ]
    .filter(Boolean)
    .join(' ')
    .toLowerCase();
  return text.includes(search.toLowerCase());
}

/** Sales Quotes list, with New (a quote for a CRM opportunity). */
export default function QuotesList() {
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const status = params.get('status');
  const [search, setSearch] = useState('');
  const [dialogOpen, setDialogOpen] = useState(false);
  const { data, error, refresh } = usePolling((signal) => api.quotes(signal), 3000);

  const rows = useMemo(
    () => (data ?? []).filter((q) => (!status || q.status === status) && (!search || matches(q, search))),
    [data, status, search],
  );

  return (
    <>
      <PageHeader
        title="Sales Quotes"
        actions={
          <ActionButton icon={<DocumentAdd20Regular />} onClick={() => setDialogOpen(true)} data-testid="new-quote">
            New
          </ActionButton>
        }
      />
      <PageBody>
        {!data ? (
          error ? <LoadError error={error} onRetry={refresh} /> : <Loading />
        ) : (
          <>
            <StaleNotice error={error} />
            <Panel>
              <ListToolbar>
                <SearchInput value={search} onChange={setSearch} placeholder="Search sales quotes" />
                {status && <FilterChip label={`Status: ${status}`} onClear={() => setParams({})} />}
                <RecordCount shown={rows.length} total={data.length} />
              </ListToolbar>
              <BcTable
                aria-label="Sales quotes"
                data-testid="quotes-list"
                columns={columns}
                rows={rows}
                rowKey={(q) => q.id}
                onRowClick={(q) => navigate(`/quotes/${q.id}`)}
                empty={
                  data.length === 0 ? 'No sales quotes yet. Create one from a CRM opportunity.' : 'No sales quotes match the filter.'
                }
              />
            </Panel>
          </>
        )}
      </PageBody>
      {/* Mounted only while open, like the Salespeople dialog: a closed Fluent Dialog that goes
          through StrictMode's double mount can make keyborg log "disposed incorrectly". */}
      {dialogOpen && <NewQuoteDialog open onClose={() => setDialogOpen(false)} />}
    </>
  );
}
