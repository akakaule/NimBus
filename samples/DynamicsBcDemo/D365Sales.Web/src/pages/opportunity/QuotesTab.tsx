import { DocumentText20Regular } from '@fluentui/react-icons';
import type { BcQuote } from '../../api';
import { EmptyState } from '../../components/PageStates';
import { RecordGrid, type GridColumn } from '../../components/RecordGrid';
import { QuoteStatusBadge } from '../../components/StatusBadges';
import { ExternalLink, Section } from '../../components/ui';
import { bcQuoteUrl } from '../../config';
import { formatDate, formatDateTime, formatMoney } from '../../format';
import { useNewItems } from '../../hooks/useHighlight';

const columns: GridColumn<BcQuote>[] = [
  {
    id: 'number',
    header: 'Quote no.',
    width: '140px',
    render: (quote) => (
      <ExternalLink href={bcQuoteUrl(quote.bcQuoteId)} testId="bc-quote-link">
        {quote.quoteNumber}
      </ExternalLink>
    ),
  },
  { id: 'status', header: 'Status', width: '110px', render: (quote) => <QuoteStatusBadge status={quote.status} /> },
  {
    id: 'total',
    header: 'Total',
    numeric: true,
    width: '130px',
    render: (quote) => formatMoney(quote.totalAmount, quote.currencyCode),
  },
  { id: 'validUntil', header: 'Valid until', render: (quote) => formatDate(quote.validUntil) },
  { id: 'sentOn', header: 'Sent on', render: (quote) => formatDateTime(quote.sentOn) },
  { id: 'lastSynced', header: 'Last synced', render: (quote) => formatDateTime(quote.lastSyncedOn) },
];

interface QuotesTabProps {
  quotes: BcQuote[];
}

/** The read-only mirror of the Business Central quotes linked to this opportunity. */
export function QuotesTab({ quotes }: QuotesTabProps) {
  const fresh = useNewItems(quotes.map((quote) => quote.bcQuoteId));

  return (
    <Section
      title="Quotes (Business Central)"
      caption="Read-only — quotes are made in Business Central"
      testId="quotes-panel"
    >
      <RecordGrid
        ariaLabel="Business Central quotes"
        columns={columns}
        rows={quotes}
        rowKey={(quote) => quote.bcQuoteId}
        fresh={fresh}
        empty={
          <EmptyState icon={<DocumentText20Regular />} title="No Business Central quote yet">
            A Business Central user creates the quote in Business Central from this opportunity.
          </EmptyState>
        }
      />
    </Section>
  );
}
