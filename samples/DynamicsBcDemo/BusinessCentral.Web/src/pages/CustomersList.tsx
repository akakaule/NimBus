import { Badge, makeStyles, tokens } from '@fluentui/react-components';
import { Link16Regular } from '@fluentui/react-icons';
import { useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, type Customer } from '../api';
import { BlockedBadge } from '../components/Badges';
import { BcTable, type Column } from '../components/BcTable';
import { ListToolbar, PageBody, Panel } from '../components/Layout';
import { FilterChip, RecordCount, SearchInput } from '../components/ListControls';
import { PageHeader } from '../components/PageHeader';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, StaleNotice } from '../components/States';
import { formatMoney } from '../format';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  name: {
    display: 'inline-flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  linked: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorBrandForeground1,
  },
  notLinked: {
    color: tokens.colorNeutralForeground4,
  },
});

function CustomerName({ customer }: { customer: Customer }) {
  const styles = useStyles();
  return (
    <span className={styles.name}>
      {customer.displayName}
      {customer.origin === 1 && (
        <Badge appearance="outline" shape="rounded" size="small" color="informative" title="Converted from a CRM prospect by Make Order">
          From prospect
        </Badge>
      )}
    </span>
  );
}

function CrmLinkIndicator({ customer }: { customer: Customer }) {
  const styles = useStyles();
  return customer.crmAccountId ? (
    <span className={styles.linked} title={`CRM Account ID ${customer.crmAccountId}`}>
      <Link16Regular aria-hidden /> Linked
    </span>
  ) : (
    <span className={styles.notLinked}>Not linked</span>
  );
}

const columns: Column<Customer>[] = [
  { key: 'number', header: 'No.', width: '100px', render: (c) => <RouterLink to={`/customers/${c.id}`}>{c.number}</RouterLink> },
  { key: 'name', header: 'Name', render: (c) => <CustomerName customer={c} /> },
  { key: 'city', header: 'City', width: '130px', render: (c) => c.city },
  { key: 'country', header: 'Country/Region', width: '120px', render: (c) => c.countryCode },
  { key: 'salesperson', header: 'Salesperson', width: '110px', render: (c) => c.salespersonCode },
  { key: 'creditLimit', header: 'Credit Limit', width: '130px', align: 'end', render: (c) => formatMoney(c.creditLimit, c.currencyCode) },
  { key: 'balance', header: 'Balance Due', width: '130px', align: 'end', render: (c) => formatMoney(c.balanceDue, c.currencyCode) },
  { key: 'blocked', header: 'Blocked', width: '130px', render: (c) => <BlockedBadge blocked={c.blocked} /> },
  { key: 'crm', header: 'Linked to CRM', width: '120px', render: (c) => <CrmLinkIndicator customer={c} /> },
];

function matches(customer: Customer, search: string): boolean {
  return [customer.number, customer.displayName, customer.city, customer.countryCode, customer.salespersonCode]
    .filter(Boolean)
    .join(' ')
    .toLowerCase()
    .includes(search.toLowerCase());
}

/** Customers list. */
export default function CustomersList() {
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const blockedOnly = params.get('blocked') === '1';
  const [search, setSearch] = useState('');
  const { data, error, refresh } = usePolling((signal) => api.customers(signal), 3000);

  const rows = useMemo(
    () => (data ?? []).filter((c) => (!blockedOnly || c.blocked !== '') && (!search || matches(c, search))),
    [data, blockedOnly, search],
  );

  return (
    <>
      <PageHeader title="Customers" />
      <PageBody>
        {!data ? (
          error ? <LoadError error={error} onRetry={refresh} /> : <Loading />
        ) : (
          <>
            <StaleNotice error={error} />
            <Panel>
              <ListToolbar>
                <SearchInput value={search} onChange={setSearch} placeholder="Search customers" />
                {blockedOnly && <FilterChip label="Blocked customers" onClear={() => setParams({})} />}
                <RecordCount shown={rows.length} total={data.length} />
              </ListToolbar>
              <BcTable
                aria-label="Customers"
                data-testid="customers-list"
                columns={columns}
                rows={rows}
                rowKey={(c) => c.id}
                onRowClick={(c) => navigate(`/customers/${c.id}`)}
                empty={data.length === 0 ? 'No customers yet.' : 'No customers match the filter.'}
              />
            </Panel>
          </>
        )}
      </PageBody>
    </>
  );
}
