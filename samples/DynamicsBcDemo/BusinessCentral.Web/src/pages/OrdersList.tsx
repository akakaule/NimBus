import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, type SalesOrder } from '../api';
import { OrderStatusBadge } from '../components/Badges';
import { BcTable, type Column } from '../components/BcTable';
import { ListToolbar, PageBody, Panel } from '../components/Layout';
import { RecordCount, SearchInput } from '../components/ListControls';
import { PageHeader } from '../components/PageHeader';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, StaleNotice } from '../components/States';
import { formatDate, formatMoney } from '../format';
import { usePolling } from '../hooks/usePolling';

const columns: Column<SalesOrder>[] = [
  { key: 'number', header: 'No.', width: '110px', render: (o) => <RouterLink to={`/orders/${o.id}`}>{o.number}</RouterLink> },
  {
    key: 'customerNumber',
    header: 'Customer No.',
    width: '120px',
    render: (o) => <RouterLink to={`/customers/${o.customerId}`}>{o.customerNumber}</RouterLink>,
  },
  { key: 'customerName', header: 'Customer Name', render: (o) => o.customerName },
  { key: 'quote', header: 'Quote No.', width: '110px', render: (o) => o.quoteNumber },
  { key: 'external', header: 'External Document No.', width: '170px', render: (o) => o.externalDocumentNumber },
  { key: 'salesperson', header: 'Salesperson', width: '110px', render: (o) => o.salespersonCode },
  { key: 'date', header: 'Order Date', width: '130px', render: (o) => formatDate(o.orderDate) },
  { key: 'amount', header: 'Amount Excl. Tax', width: '150px', align: 'end', render: (o) => formatMoney(o.totalAmountExcludingTax, o.currencyCode) },
  { key: 'status', header: 'Status', width: '100px', render: (o) => <OrderStatusBadge status={o.status} /> },
];

function matches(order: SalesOrder, search: string): boolean {
  return [order.number, order.customerNumber, order.customerName, order.quoteNumber, order.externalDocumentNumber, order.salespersonCode]
    .filter(Boolean)
    .join(' ')
    .toLowerCase()
    .includes(search.toLowerCase());
}

/** Sales Orders list. */
export default function OrdersList() {
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const { data, error, refresh } = usePolling((signal) => api.orders(signal), 3000);
  const rows = useMemo(() => (data ?? []).filter((o) => !search || matches(o, search)), [data, search]);

  return (
    <>
      <PageHeader title="Sales Orders" />
      <PageBody>
        {!data ? (
          error ? <LoadError error={error} onRetry={refresh} /> : <Loading />
        ) : (
          <>
            <StaleNotice error={error} />
            <Panel>
              <ListToolbar>
                <SearchInput value={search} onChange={setSearch} placeholder="Search sales orders" />
                <RecordCount shown={rows.length} total={data.length} />
              </ListToolbar>
              <BcTable
                aria-label="Sales orders"
                data-testid="orders-list"
                columns={columns}
                rows={rows}
                rowKey={(o) => o.id}
                onRowClick={(o) => navigate(`/orders/${o.id}`)}
                empty={data.length === 0 ? 'No sales orders yet. Make Order on a sales quote creates one.' : 'No sales orders match the search.'}
              />
            </Panel>
          </>
        )}
      </PageBody>
    </>
  );
}
