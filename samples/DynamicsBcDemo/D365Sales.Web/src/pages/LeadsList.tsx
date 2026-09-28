import { useNavigate } from 'react-router-dom';
import { PersonCall20Regular } from '@fluentui/react-icons';
import { api, type LeadRow } from '../api';
import { ListView } from '../components/ListView';
import type { GridColumn } from '../components/RecordGrid';
import { LeadStatusBadge } from '../components/StatusBadges';
import { RecordLink } from '../components/ui';
import { formatMoney, orDash } from '../format';
import { usePolling } from '../hooks/usePolling';

const contactName = (row: LeadRow) => `${row.lead.firstName} ${row.lead.lastName}`.trim();

const columns: GridColumn<LeadRow>[] = [
  {
    id: 'topic',
    header: 'Topic',
    title: (row) => row.lead.subject,
    render: (row) => <RecordLink to={`/leads/${row.lead.leadId}`}>{row.lead.subject}</RecordLink>,
  },
  {
    id: 'company',
    header: 'Company',
    width: '210px',
    title: (row) => row.lead.companyName,
    render: (row) => row.lead.companyName,
  },
  { id: 'contact', header: 'Contact name', width: '150px', render: (row) => orDash(contactName(row)) },
  { id: 'country', header: 'Country', width: '80px', render: (row) => orDash(row.lead.address1Country) },
  { id: 'value', header: 'Est. value', numeric: true, width: '120px', render: (row) => formatMoney(row.lead.estimatedValue) },
  { id: 'owner', header: 'Owner', width: '140px', render: (row) => orDash(row.owner) },
  { id: 'status', header: 'Status', width: '110px', render: (row) => <LeadStatusBadge stateCode={row.lead.stateCode} /> },
];

export function LeadsList() {
  const navigate = useNavigate();
  const poll = usePolling('leads', (signal) => api.leads(signal));

  return (
    <ListView
      title="All leads"
      entityPlural="leads"
      poll={poll}
      columns={columns}
      rowKey={(row) => row.lead.leadId}
      onOpen={(row) => navigate(`/leads/${row.lead.leadId}`)}
      searchText={(row) =>
        [row.lead.subject, row.lead.companyName, contactName(row), row.lead.address1Country, row.owner].join(' ')
      }
      emptyIcon={<PersonCall20Regular />}
      testId="leads-grid"
    />
  );
}
