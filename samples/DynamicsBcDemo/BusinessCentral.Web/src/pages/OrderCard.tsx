import { makeStyles, tokens } from '@fluentui/react-components';
import { Open20Regular } from '@fluentui/react-icons';
import { useParams } from 'react-router-dom';
import { api, type SalesOrder, type SalesOrderLine } from '../api';
import { OrderStatusBadge } from '../components/Badges';
import { BcTable, type Column } from '../components/BcTable';
import { FastTab } from '../components/FastTab';
import { FieldColumn, FieldGrid, FieldRow, Value } from '../components/Fields';
import { PageBody, Panel } from '../components/Layout';
import { ActionButton, PageHeader } from '../components/PageHeader';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, StaleNotice } from '../components/States';
import { d365OpportunityUrl } from '../config';
import { formatDate, formatMoney, formatNumber } from '../format';
import { useIdByNumber } from '../hooks/useIdByNumber';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  total: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
});

/** Sales Order card (read-only). Keyed by id, so another order starts fresh. */
export default function OrderCardPage() {
  const { id = '' } = useParams();
  return <OrderCard key={id} id={id} />;
}

function OrderCard({ id }: { id: string }) {
  const order = usePolling((signal) => api.order(id, signal), 3000, id);
  if (!order.data) {
    return (
      <>
        <PageHeader caption="Sales Order" title="Sales Order" />
        <PageBody>{order.error ? <LoadError error={order.error} onRetry={order.refresh} /> : <Loading />}</PageBody>
      </>
    );
  }
  return <OrderCardView order={order.data} error={order.error} />;
}

function OrderCardView({ order, error }: { order: SalesOrder; error: Error | undefined }) {
  const styles = useStyles();
  const quoteId = useIdByNumber('quote', order.quoteNumber);
  const currency = order.currencyCode;

  const lineColumns: Column<SalesOrderLine>[] = [
    { key: 'item', header: 'Item No.', width: '130px', render: (l) => l.itemNumber },
    { key: 'description', header: 'Description', render: (l) => l.description },
    { key: 'quantity', header: 'Quantity', width: '100px', align: 'end', render: (l) => formatNumber(l.quantity) },
    { key: 'price', header: 'Unit Price Excl. Tax', width: '160px', align: 'end', render: (l) => formatMoney(l.unitPrice, currency, 2) },
    { key: 'discount', header: 'Line Discount %', width: '130px', align: 'end', render: (l) => formatNumber(l.discountPercent) },
    { key: 'amount', header: 'Line Amount Excl. Tax', width: '170px', align: 'end', render: (l) => formatMoney(l.amountExcludingTax, currency, 2) },
  ];

  return (
    <>
      <PageHeader
        caption="Sales Order"
        title={`${order.number} · ${order.customerName}`}
        badges={<OrderStatusBadge status={order.status} />}
        actions={
          order.crmOpportunityId ? (
            <ActionButton
              as="a"
              href={d365OpportunityUrl(order.crmOpportunityId)}
              target="_blank"
              rel="noopener noreferrer"
              icon={<Open20Regular />}
              data-testid="open-in-d365"
            >
              Open in Dynamics 365
            </ActionButton>
          ) : undefined
        }
      />
      <PageBody>
        <StaleNotice error={error} />
        <Panel padded>
          <FastTab title="General" summary={`${order.customerName} · ${formatDate(order.orderDate)} · ${order.status}`}>
            <FieldGrid>
              <FieldColumn>
                <FieldRow label="No.">{order.number}</FieldRow>
                <FieldRow label="Customer No.">
                  <RouterLink to={`/customers/${order.customerId}`}>{order.customerNumber}</RouterLink>
                </FieldRow>
                <FieldRow label="Customer Name">{order.customerName}</FieldRow>
                <FieldRow label="Salesperson Code">
                  <Value>{order.salespersonCode}</Value>
                </FieldRow>
                <FieldRow label="Status">
                  <OrderStatusBadge status={order.status} />
                </FieldRow>
              </FieldColumn>
              <FieldColumn>
                <FieldRow label="Order Date">{formatDate(order.orderDate)}</FieldRow>
                <FieldRow label="Quote No.">
                  {order.quoteNumber && quoteId ? (
                    <RouterLink to={`/quotes/${quoteId}`}>{order.quoteNumber}</RouterLink>
                  ) : (
                    <Value>{order.quoteNumber}</Value>
                  )}
                </FieldRow>
                <FieldRow label="External Document No." hint="CRM opportunity">
                  <Value>{order.externalDocumentNumber}</Value>
                </FieldRow>
                <FieldRow label="Currency Code">{currency}</FieldRow>
              </FieldColumn>
            </FieldGrid>
          </FastTab>

          <FastTab title="CRM link" caption="AL extension fields" summary={order.crmOpportunityId ? 'Linked to a Dynamics 365 opportunity' : 'Not linked'}>
            <FieldGrid>
              <FieldColumn>
                <FieldRow label="CRM Opportunity ID">
                  <Value mono>{order.crmOpportunityId}</Value>
                </FieldRow>
                <FieldRow label="CRM Account ID">
                  <Value mono>{order.crmAccountId}</Value>
                </FieldRow>
              </FieldColumn>
            </FieldGrid>
          </FastTab>

          <FastTab title="Lines" summary={`${order.lines.length} ${order.lines.length === 1 ? 'line' : 'lines'}`} data-testid="order-lines">
            <BcTable aria-label="Order lines" columns={lineColumns} rows={order.lines} rowKey={(l) => l.id} empty="This order has no lines." />
          </FastTab>

          <FastTab title="Totals" summary={formatMoney(order.totalAmountExcludingTax, currency, 2)}>
            <FieldGrid>
              <FieldColumn>
                <FieldRow label="Lines">{order.lines.length}</FieldRow>
              </FieldColumn>
              <FieldColumn>
                <FieldRow label={`Total Excl. Tax (${currency})`}>
                  <span className={styles.total}>{formatMoney(order.totalAmountExcludingTax, currency, 2)}</span>
                </FieldRow>
              </FieldColumn>
            </FieldGrid>
          </FastTab>
        </Panel>
      </PageBody>
    </>
  );
}
