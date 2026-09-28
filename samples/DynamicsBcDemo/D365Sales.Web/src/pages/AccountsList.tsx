import { Badge, makeStyles, tokens } from '@fluentui/react-components';
import { Building20Regular, LockClosed16Regular } from '@fluentui/react-icons';
import { useNavigate } from 'react-router-dom';
import { api, type AccountRow } from '../api';
import { ListView } from '../components/ListView';
import type { GridColumn } from '../components/RecordGrid';
import { RelationshipBadge } from '../components/StatusBadges';
import { RecordLink } from '../components/ui';
import { orDash } from '../format';
import { usePolling } from '../hooks/usePolling';
import { isBcOwned, relationshipLabel } from '../model';

const useStyles = makeStyles({
  owner: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    whiteSpace: 'nowrap',
  },
  lock: {
    display: 'inline-flex',
    color: tokens.colorNeutralForeground3,
  },
});

function MasterDataCell({ row }: { row: AccountRow }) {
  const styles = useStyles();
  if (!isBcOwned(row.account)) return <span className={styles.owner}>Dynamics 365</span>;
  return (
    <span className={styles.owner} title="Business Central manages this account's master data">
      <span className={styles.lock} data-testid="bc-owned-lock">
        <LockClosed16Regular aria-label="Locked" />
      </span>
      Business Central
    </span>
  );
}

const columns: GridColumn<AccountRow>[] = [
  {
    id: 'name',
    header: 'Account name',
    title: (row) => row.account.name,
    render: (row) => <RecordLink to={`/accounts/${row.account.accountId}`}>{row.account.name}</RecordLink>,
  },
  {
    id: 'relationship',
    header: 'Relationship type',
    width: '134px',
    render: (row) => <RelationshipBadge code={row.account.customerTypeCode} />,
  },
  { id: 'number', header: 'Account number', width: '124px', render: (row) => orDash(row.account.accountNumber) },
  { id: 'city', header: 'City', width: '112px', render: (row) => orDash(row.account.address1City) },
  { id: 'country', header: 'Country', width: '76px', render: (row) => orDash(row.account.address1Country) },
  { id: 'owner', header: 'Owner', width: '130px', title: (row) => row.owner, render: (row) => orDash(row.owner) },
  { id: 'masterData', header: 'Master data', width: '150px', render: (row) => <MasterDataCell row={row} /> },
  {
    id: 'credit',
    header: 'Credit hold',
    width: '100px',
    render: (row) =>
      row.account.creditOnHold ? (
        <Badge appearance="tint" color="warning" shape="rounded" data-testid="credit-hold">
          On hold
        </Badge>
      ) : (
        '—'
      ),
  },
];

export function AccountsList() {
  const navigate = useNavigate();
  const poll = usePolling('accounts', (signal) => api.accounts(signal), 3000);

  return (
    <ListView
      title="All accounts"
      entityPlural="accounts"
      poll={poll}
      columns={columns}
      rowKey={(row) => row.account.accountId}
      onOpen={(row) => navigate(`/accounts/${row.account.accountId}`)}
      searchText={(row) =>
        [
          row.account.name,
          relationshipLabel(row.account.customerTypeCode),
          row.account.accountNumber,
          row.account.address1City,
          row.account.address1Country,
          row.owner,
          isBcOwned(row.account) ? 'Business Central' : 'Dynamics 365',
        ].join(' ')
      }
      emptyIcon={<Building20Regular />}
      testId="accounts-grid"
    />
  );
}
