import { makeStyles } from '@fluentui/react-components';
import { Money20Regular } from '@fluentui/react-icons';
import { useNavigate } from 'react-router-dom';
import { api, type OpportunityRow } from '../api';
import { ListView } from '../components/ListView';
import type { GridColumn } from '../components/RecordGrid';
import { OpportunityStatusBadge, QuoteStatusBadge } from '../components/StatusBadges';
import { RecordLink } from '../components/ui';
import { formatMoney, orDash } from '../format';
import { usePolling } from '../hooks/usePolling';
import { opportunityStatusLabel, stageLabel } from '../model';

const useStyles = makeStyles({
  quote: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '8px',
    whiteSpace: 'nowrap',
  },
});

function BcQuoteCell({ row }: { row: OpportunityRow }) {
  const styles = useStyles();
  const { csBcQuoteNumber: number, csBcQuoteStatus: status } = row.opportunity;
  if (!number && !status) return <>—</>;
  return (
    <span className={styles.quote}>
      {number}
      {status && <QuoteStatusBadge status={status} />}
    </span>
  );
}

// Fixed widths fit their content, so the topic keeps a usable share of narrower screens.
const columns: GridColumn<OpportunityRow>[] = [
  { id: 'number', header: 'Opportunity no.', width: '118px', render: (row) => row.opportunity.csNumber },
  {
    id: 'topic',
    header: 'Topic',
    title: (row) => row.opportunity.name,
    render: (row) => (
      <RecordLink to={`/opportunities/${row.opportunity.opportunityId}`}>{row.opportunity.name}</RecordLink>
    ),
  },
  {
    id: 'account',
    header: 'Account',
    width: '13%',
    title: (row) => row.account,
    render: (row) =>
      row.account ? <RecordLink to={`/accounts/${row.opportunity.customerId}`}>{row.account}</RecordLink> : '—',
  },
  {
    id: 'productGroup',
    header: 'Product group',
    width: '11%',
    title: (row) => row.productGroup,
    render: (row) => orDash(row.productGroup),
  },
  { id: 'stage', header: 'Stage', width: '80px', render: (row) => stageLabel(row.opportunity.stepName) },
  {
    id: 'value',
    header: 'Est. revenue',
    numeric: true,
    width: '104px',
    render: (row) => formatMoney(row.opportunity.estimatedValue),
  },
  { id: 'quote', header: 'BC quote', width: '150px', render: (row) => <BcQuoteCell row={row} /> },
  { id: 'owner', header: 'Owner', width: '110px', title: (row) => row.owner, render: (row) => orDash(row.owner) },
  {
    id: 'status',
    header: 'Status',
    width: '72px',
    render: (row) => <OpportunityStatusBadge stateCode={row.opportunity.stateCode} />,
  },
];

export function OpportunitiesList() {
  const navigate = useNavigate();
  const poll = usePolling('opportunities', (signal) => api.opportunities(signal), 3000);

  return (
    <ListView
      title="All opportunities"
      entityPlural="opportunities"
      poll={poll}
      columns={columns}
      rowKey={(row) => row.opportunity.opportunityId}
      onOpen={(row) => navigate(`/opportunities/${row.opportunity.opportunityId}`)}
      searchText={(row) =>
        [
          row.opportunity.csNumber,
          row.opportunity.name,
          row.account,
          row.productGroup,
          stageLabel(row.opportunity.stepName),
          row.opportunity.csBcQuoteNumber,
          row.opportunity.csBcQuoteStatus,
          row.owner,
          opportunityStatusLabel(row.opportunity.stateCode),
        ].join(' ')
      }
      emptyIcon={<Money20Regular />}
      emptyText="Qualify a lead to create one."
      testId="opportunities-grid"
    />
  );
}
