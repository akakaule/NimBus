import { makeStyles, tokens } from '@fluentui/react-components';
import { useNavigate } from 'react-router-dom';
import { api, type RecentDocument } from '../api';
import { OrderStatusBadge, QuoteStatusBadge } from '../components/Badges';
import { BcTable, type Column } from '../components/BcTable';
import { Cue, CueGroup } from '../components/Cue';
import { PageBody, Panel } from '../components/Layout';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, StaleNotice } from '../components/States';
import { formatDateTime, formatMoney } from '../format';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  headline: {
    padding: `${tokens.spacingVerticalXL} ${tokens.spacingHorizontalXXL} ${tokens.spacingVerticalL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  role: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  greeting: {
    margin: `${tokens.spacingVerticalXS} 0`,
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
  },
  insight: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    color: tokens.colorNeutralForeground2,
  },
  sectionTitle: {
    margin: `0 0 ${tokens.spacingVerticalM}`,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  activities: {
    display: 'flex',
    flexWrap: 'wrap',
    columnGap: '40px',
    rowGap: tokens.spacingVerticalL,
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXL} ${tokens.spacingVerticalXL}`,
  },
  recent: {
    marginTop: tokens.spacingVerticalL,
  },
  recentHeader: {
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXL} 0`,
  },
  recentTable: {
    padding: `0 ${tokens.spacingHorizontalS} ${tokens.spacingVerticalS}`,
  },
});

function greeting(): string {
  const hour = new Date().getHours();
  if (hour < 12) return 'Good morning!';
  if (hour < 18) return 'Good afternoon!';
  return 'Good evening!';
}

const cardPath = (document: RecentDocument) =>
  document.type === 'Sales Order' ? `/orders/${document.id}` : `/quotes/${document.id}`;

const recentColumns: Column<RecentDocument>[] = [
  { key: 'type', header: 'Type', width: '120px', render: (d) => d.type },
  { key: 'number', header: 'No.', width: '120px', render: (d) => <RouterLink to={cardPath(d)}>{d.number}</RouterLink> },
  { key: 'name', header: 'Name', render: (d) => d.name },
  {
    key: 'status',
    header: 'Status',
    width: '120px',
    render: (d) => (d.type === 'Sales Order' ? <OrderStatusBadge status={d.status} /> : <QuoteStatusBadge status={d.status} />),
  },
  { key: 'amount', header: 'Amount Excl. Tax', width: '150px', align: 'end', render: (d) => formatMoney(d.amount, d.currencyCode) },
  { key: 'modified', header: 'Last Modified', width: '180px', render: (d) => formatDateTime(d.lastModified) },
];

/** Home: the Sales Order Processor Role Center. */
export default function RoleCenter() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { data, error, refresh } = usePolling((signal) => api.roleCenter(signal), 3000);

  if (!data) {
    return <PageBody>{error ? <LoadError error={error} onRetry={refresh} /> : <Loading />}</PageBody>;
  }

  const openQuoteCount = data.openQuotes + data.sentQuotes;

  return (
    <div data-testid="role-center">
      <section className={styles.headline}>
        <div className={styles.role}>Sales Order Processor</div>
        <h1 className={styles.greeting}>{greeting()}</h1>
        <p className={styles.insight}>
          {openQuoteCount === 1 ? '1 open sales quote' : `${openQuoteCount} open sales quotes`} worth{' '}
          {formatMoney(data.openQuotesValue, 'EUR')} {openQuoteCount === 1 ? 'is' : 'are'} waiting to become orders.{' '}
          {data.prospects > 0 &&
            (data.prospects === 1
              ? '1 prospect is quoted as a contact, not a customer, until it orders.'
              : `${data.prospects} prospects are quoted as contacts, not customers, until they order.`)}
        </p>
      </section>

      <PageBody>
        <StaleNotice error={error} />

        <Panel>
          <div className={styles.activities}>
            <CueGroup title="Sales Quotes">
              <Cue title="Sales Quotes – Open" value={data.openQuotes} to="/quotes?status=Draft" data-testid="cue-open-quotes" />
              <Cue title="Sales Quotes – Sent" value={data.sentQuotes} to="/quotes?status=Sent" data-testid="cue-sent-quotes" />
              <Cue
                title="Open quote value"
                value={formatMoney(data.openQuotesValue, 'EUR')}
                small
                to="/quotes"
                data-testid="cue-open-quote-value"
              />
            </CueGroup>
            <CueGroup title="Sales Orders">
              <Cue
                title="Sales Orders this month"
                value={data.ordersThisMonth}
                sub={formatMoney(data.ordersValueThisMonth, 'EUR')}
                to="/orders"
                data-testid="cue-orders-this-month"
              />
            </CueGroup>
            <CueGroup title="Customers">
              <Cue title="Customers" value={data.customers} to="/customers" data-testid="cue-customers" />
              <Cue
                title="Customers blocked"
                value={data.customersBlocked}
                unfavorable={data.customersBlocked > 0}
                to="/customers?blocked=1"
                data-testid="cue-customers-blocked"
              />
              <Cue
                title="Prospects (contacts not yet customers)"
                value={data.prospects}
                to="/contacts?prospects=1"
                data-testid="cue-prospects"
              />
            </CueGroup>
          </div>
        </Panel>

        <Panel className={styles.recent}>
          <div className={styles.recentHeader}>
            <h2 className={styles.sectionTitle}>Recent documents</h2>
          </div>
          <div className={styles.recentTable}>
            <BcTable
              aria-label="Recent documents"
              data-testid="recent-documents"
              columns={recentColumns}
              rows={data.recentDocuments}
              rowKey={(d) => `${d.type}:${d.id}`}
              onRowClick={(d) => navigate(cardPath(d))}
              empty="No sales documents yet. Quotes appear here when Dynamics 365 requests them."
            />
          </div>
        </Panel>
      </PageBody>
    </div>
  );
}
