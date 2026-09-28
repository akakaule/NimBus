import { DocumentText20Regular } from '@fluentui/react-icons';
import type { BcQuote, Opportunity } from '../../api';
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
  opportunity: Opportunity;
  quotes: BcQuote[];
}

/** The read-only mirror of the Business Central quotes for this opportunity. */
export function QuotesTab({ opportunity, quotes }: QuotesTabProps) {
  const fresh = useNewItems(quotes.map((quote) => quote.bcQuoteId));
  const waiting = opportunity.csBcQuoteStatus === 'Requested';

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
          <EmptyState
            icon={<DocumentText20Regular />}
            title={waiting ? 'Business Central is creating the quote…' : 'No Business Central quote yet'}
          >
            {waiting
              ? 'It appears here as soon as Business Central reports it.'
              : 'Use Request quote in Business Central in the command bar.'}
          </EmptyState>
        }
      />
    </Section>
  );
}
