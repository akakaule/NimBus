import { makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { DocumentArrowRight20Regular, Open20Regular, Send20Regular } from '@fluentui/react-icons';
import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { api, errorMessage, type MakeOrderResult, type QuoteDetail } from '../api';
import { ProspectTag, QuoteStatusBadge } from '../components/Badges';
import { FastTab } from '../components/FastTab';
import { FieldColumn, FieldGrid, FieldRow, Value } from '../components/Fields';
import { PageBody, Panel } from '../components/Layout';
import { MakeOrderDialog } from '../components/MakeOrderDialog';
import { ActionButton, PageHeader } from '../components/PageHeader';
import { QuoteLinesPart } from '../components/QuoteLinesPart';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, Notice, StaleNotice, type NoticeState } from '../components/States';
import { d365OpportunityUrl } from '../config';
import { formatDate, formatDateTime, formatMoney } from '../format';
import { useIdByNumber } from '../hooks/useIdByNumber';
import { usePolling } from '../hooks/usePolling';
import { useRouteNotice } from '../hooks/useRouteNotice';

const useStyles = makeStyles({
  sellTo: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  explain: {
    margin: 0,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '520px',
    paddingTop: tokens.spacingVerticalS,
  },
  total: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  noticeLinks: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalL,
    marginTop: tokens.spacingVerticalXS,
  },
});

/** Sales Quote card. Keyed by id, so another quote starts with fresh state. */
export default function QuoteCardPage() {
  const { id = '' } = useParams();
  return <QuoteCard key={id} id={id} />;
}

function QuoteCard({ id }: { id: string }) {
  const quote = usePolling((signal) => api.quote(id, signal), 3000, id);

  if (!quote.data) {
    return (
      <>
        <PageHeader caption="Sales Quote" title="Sales Quote" />
        <PageBody>{quote.error ? <LoadError error={quote.error} onRetry={quote.refresh} /> : <Loading />}</PageBody>
      </>
    );
  }
  return <QuoteCardView quote={quote.data} error={quote.error} onChanged={quote.replace} onRefresh={quote.refresh} />;
}

interface QuoteCardViewProps {
  quote: QuoteDetail;
  error: Error | undefined;
  onChanged: (quote: QuoteDetail) => void;
  onRefresh: () => Promise<void>;
}

function QuoteCardView({ quote, error, onChanged, onRefresh }: QuoteCardViewProps) {
  const styles = useStyles();
  // e.g. "Sales quote … created", passed by Create sales quote.
  const arrivalNotice = useRouteNotice();
  const [notice, setNotice] = useState<NoticeState | null>(arrivalNotice);
  const [sending, setSending] = useState(false);
  const [editingLines, setEditingLines] = useState(false);
  const [makeOrderOpen, setMakeOrderOpen] = useState(false);
  const { data: salespeople } = usePolling((signal) => api.salespeople(signal), null);
  const orderId = useIdByNumber('order', quote.orderNumber);

  const editable = quote.status === 'Draft' || quote.status === 'Sent';
  const actionsDisabled = !editable || sending || editingLines;
  // A new quote starts without lines; Business Central won't send it or make an order from it yet.
  const needsLines = editable && quote.lines.length === 0;
  const salesperson = salespeople?.find((s) => s.code === quote.salespersonCode);
  const isContact = quote.sellToType === 'Contact';
  const sellToNumber = isContact ? quote.contact?.number : quote.customer?.number;
  const opportunity = quote.crmOpportunity;
  const linked = Boolean(quote.crmOpportunityId);

  const send = async () => {
    setSending(true);
    setNotice(null);
    try {
      const updated = await api.sendQuote(quote.id);
      onChanged(updated);
      setNotice({
        intent: 'success',
        title: `Sales quote ${updated.number} sent`,
        message: linked
          ? `Its status is now Sent. NimBus passes the change on to ${opportunity ? `opportunity ${opportunity.number}` : 'the opportunity'} in Dynamics 365.`
          : 'Its status is now Sent.',
      });
    } catch (e) {
      setNotice({ intent: 'error', title: 'Business Central', message: errorMessage(e) });
    } finally {
      setSending(false);
    }
  };

  const orderMade = (result: MakeOrderResult) => {
    setMakeOrderOpen(false);
    setNotice({
      intent: 'success',
      title: `Sales order ${result.orderNumber} created`,
      message: (
        <>
          {result.customerCreated && (
            <div>
              Customer {result.customerNumber} created from contact {quote.contact?.number ?? ''}.
            </div>
          )}
          {linked && (
            <div>
              Quote {quote.number} is accepted: Dynamics 365 closes{' '}
              {opportunity ? `opportunity ${opportunity.number}` : 'the opportunity'} as won.
            </div>
          )}
          <div className={styles.noticeLinks}>
            <RouterLink to={`/orders/${result.orderId}`} data-testid="open-created-order">
              Open sales order {result.orderNumber}
            </RouterLink>
            <RouterLink to={`/customers/${result.customerId}`}>Open customer {result.customerNumber}</RouterLink>
          </div>
        </>
      ),
    });
    void onRefresh();
  };

  return (
    <>
      <PageHeader
        caption="Sales Quote"
        title={`${quote.number} · ${quote.sellToName}`}
        badges={<QuoteStatusBadge status={quote.status} size="large" data-testid="quote-status" />}
        actions={
          <>
            {/* Focusable while waiting for lines, so the "Add lines first" tooltip still shows. */}
            <ActionButton
              icon={sending ? <Spinner size="tiny" /> : <Send20Regular />}
              onClick={send}
              disabled={actionsDisabled}
              disabledFocusable={needsLines}
              title={needsLines ? 'Add lines first' : undefined}
              data-testid="quote-send"
            >
              Send
            </ActionButton>
            <ActionButton
              icon={<DocumentArrowRight20Regular />}
              onClick={() => setMakeOrderOpen(true)}
              disabled={actionsDisabled}
              disabledFocusable={needsLines}
              title={needsLines ? 'Add lines first' : undefined}
              data-testid="make-order"
            >
              Make Order
            </ActionButton>
            {quote.crmOpportunityId && (
              <ActionButton
                as="a"
                href={d365OpportunityUrl(quote.crmOpportunityId)}
                target="_blank"
                rel="noopener noreferrer"
                icon={<Open20Regular />}
                data-testid="open-in-d365"
              >
                Open in Dynamics 365
              </ActionButton>
            )}
          </>
        }
        aside={
          editingLines
            ? 'Save or cancel the line changes to use the actions.'
            : needsLines
              ? 'Add lines to send the quote or make an order.'
              : undefined
        }
      />

      <PageBody>
        <StaleNotice error={error} />
        <Notice notice={notice} onDismiss={() => setNotice(null)} />

        <Panel padded>
          <FastTab
            title="General"
            summary={`${quote.sellToName} · ${formatDate(quote.documentDate)} · ${quote.status}`}
            data-testid="fasttab-general"
          >
            <FieldGrid>
              <FieldColumn>
                <FieldRow label="No.">{quote.number}</FieldRow>
                <FieldRow label="Sell-to Type">
                  <span className={styles.sellTo}>
                    {quote.sellToType}
                    {isContact && <ProspectTag />}
                  </span>
                </FieldRow>
                <FieldRow label={isContact ? 'Sell-to Contact No.' : 'Sell-to Customer No.'} data-testid="sell-to-number">
                  {!isContact && quote.customer ? (
                    <RouterLink to={`/customers/${quote.customer.id}`}>{quote.customer.number}</RouterLink>
                  ) : (
                    <Value>{sellToNumber}</Value>
                  )}
                </FieldRow>
                <FieldRow label="Sell-to Name">{quote.sellToName}</FieldRow>
                <FieldRow label="Salesperson">
                  <Value>
                    {quote.salespersonCode && (salesperson ? `${quote.salespersonCode} · ${salesperson.displayName}` : quote.salespersonCode)}
                  </Value>
                </FieldRow>
                <FieldRow label="Status">
                  <QuoteStatusBadge status={quote.status} />
                </FieldRow>
              </FieldColumn>
              <FieldColumn>
                <FieldRow label="Document Date">{formatDate(quote.documentDate)}</FieldRow>
                <FieldRow label="Quote Valid Until Date">
                  <Value>{formatDate(quote.validUntilDate)}</Value>
                </FieldRow>
                <FieldRow label="External Document No." hint="CRM opportunity">
                  <Value>{quote.externalDocumentNumber}</Value>
                </FieldRow>
                <FieldRow label="Order No." data-testid="order-number">
                  {quote.orderNumber && orderId ? (
                    <RouterLink to={`/orders/${orderId}`}>{quote.orderNumber}</RouterLink>
                  ) : (
                    <Value>{quote.orderNumber}</Value>
                  )}
                </FieldRow>
                {quote.sentDate && <FieldRow label="Sent">{formatDateTime(quote.sentDate)}</FieldRow>}
                {quote.description && <FieldRow label="Description">{quote.description}</FieldRow>}
              </FieldColumn>
            </FieldGrid>
          </FastTab>

          <FastTab
            title="CRM link"
            caption="AL extension fields"
            summary={
              opportunity
                ? `Linked to CRM opportunity ${opportunity.number}`
                : linked
                  ? 'Linked to a Dynamics 365 opportunity'
                  : 'Not linked to Dynamics 365'
            }
            data-testid="fasttab-crm"
          >
            <FieldGrid>
              <FieldColumn>
                <FieldRow label="CRM Opportunity" data-testid="crm-opportunity">
                  <Value>{opportunity && `${opportunity.number} · ${opportunity.name} (${opportunity.status})`}</Value>
                </FieldRow>
                <FieldRow label="CRM Opportunity ID">
                  <Value mono>{quote.crmOpportunityId}</Value>
                </FieldRow>
                <FieldRow label="CRM Account ID">
                  <Value mono>{quote.crmAccountId}</Value>
                </FieldRow>
              </FieldColumn>
              <FieldColumn>
                <p className={styles.explain}>
                  {linked
                    ? 'Standard Business Central has no fields for CRM references; a small AL table extension adds them. This quote was made for the CRM opportunity, so NimBus sends its status back to that opportunity in Dynamics 365.'
                    : 'This quote is not linked to a CRM opportunity, so it stays in Business Central. Quotes created from a CRM opportunity are linked through a small AL table extension.'}
                </p>
              </FieldColumn>
            </FieldGrid>
          </FastTab>

          <FastTab
            title="Lines"
            summary={quote.lines.length === 0 ? 'No lines yet' : `${quote.lines.length} ${quote.lines.length === 1 ? 'line' : 'lines'}`}
            data-testid="fasttab-lines"
          >
            <QuoteLinesPart
              quote={quote}
              editable={editable}
              onEditingChange={setEditingLines}
              onSaved={(updated) => {
                onChanged(updated);
                const total = formatMoney(updated.totalAmountExcludingTax, updated.currencyCode);
                setNotice({
                  intent: 'success',
                  title: `Lines saved on ${updated.number}`,
                  message: linked ? `The new total, ${total}, is on its way to Dynamics 365 through NimBus.` : `The new total is ${total}.`,
                });
              }}
            />
          </FastTab>

          <FastTab title="Totals" summary={formatMoney(quote.totalAmountExcludingTax, quote.currencyCode, 2)} data-testid="fasttab-totals">
            <FieldGrid>
              <FieldColumn>
                <FieldRow label="Lines">{quote.lines.length}</FieldRow>
              </FieldColumn>
              <FieldColumn>
                <FieldRow label={`Total Excl. Tax (${quote.currencyCode})`} data-testid="quote-total">
                  <span className={styles.total}>{formatMoney(quote.totalAmountExcludingTax, quote.currencyCode, 2)}</span>
                </FieldRow>
              </FieldColumn>
            </FieldGrid>
          </FastTab>
        </Panel>
      </PageBody>

      {/* Mounted only while open, like the Salespeople dialog: a closed Fluent Dialog that goes
          through StrictMode's double mount can make keyborg log "disposed incorrectly". */}
      {makeOrderOpen && <MakeOrderDialog open quote={quote} onClose={() => setMakeOrderOpen(false)} onDone={orderMade} />}
    </>
  );
}
