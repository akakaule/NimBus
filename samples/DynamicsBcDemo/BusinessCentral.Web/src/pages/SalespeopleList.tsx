import { makeStyles, tokens } from '@fluentui/react-components';
import { PersonAdd20Regular } from '@fluentui/react-icons';
import { useMemo, useState } from 'react';
import { api, type Salesperson } from '../api';
import { BcTable, type Column } from '../components/BcTable';
import { ListToolbar, PageBody, Panel } from '../components/Layout';
import { RecordCount, SearchInput } from '../components/ListControls';
import { NewSalespersonDialog } from '../components/NewSalespersonDialog';
import { ActionButton, PageHeader } from '../components/PageHeader';
import { LoadError, Loading, Notice, StaleNotice, type NoticeState } from '../components/States';
import { chrome } from '../theme';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  created: {
    backgroundColor: chrome.brandTint,
    ':hover': {
      backgroundColor: chrome.brandTintStrong,
    },
  },
  code: {
    fontWeight: tokens.fontWeightSemibold,
  },
});

/** Salespeople (Salesperson/Purchaser list), with New. */
export default function SalespeopleList() {
  const styles = useStyles();
  const [search, setSearch] = useState('');
  const [dialogOpen, setDialogOpen] = useState(false);
  const [createdCode, setCreatedCode] = useState<string | null>(null);
  const [notice, setNotice] = useState<NoticeState | null>(null);
  const { data, error, refresh, replace } = usePolling((signal) => api.salespeople(signal), 3000);

  const columns: Column<Salesperson>[] = [
    { key: 'code', header: 'Code', width: '120px', render: (s) => <span className={styles.code}>{s.code}</span> },
    { key: 'name', header: 'Name', render: (s) => s.displayName },
    { key: 'email', header: 'E-mail', render: (s) => s.email },
  ];

  const rows = useMemo(
    () =>
      (data ?? []).filter(
        (s) => !search || `${s.code} ${s.displayName} ${s.email}`.toLowerCase().includes(search.toLowerCase()),
      ),
    [data, search],
  );

  const created = (salesperson: Salesperson) => {
    setDialogOpen(false);
    setCreatedCode(salesperson.code);
    setNotice({
      intent: 'success',
      title: `Salesperson ${salesperson.code} created`,
      message: `${salesperson.displayName} (${salesperson.email}) can now be the salesperson on quotes Dynamics 365 requests.`,
    });
    if (data) {
      replace([...data.filter((s) => s.code !== salesperson.code), salesperson].sort((a, b) => a.code.localeCompare(b.code)));
    }
    void refresh();
  };

  return (
    <>
      <PageHeader
        title="Salespeople"
        actions={
          <ActionButton
            icon={<PersonAdd20Regular />}
            onClick={() => {
              setNotice(null);
              setDialogOpen(true);
            }}
            data-testid="new-salesperson"
          >
            New
          </ActionButton>
        }
      />
      <PageBody>
        <Notice notice={notice} onDismiss={() => setNotice(null)} />
        {!data ? (
          error ? <LoadError error={error} onRetry={refresh} /> : <Loading />
        ) : (
          <>
            <StaleNotice error={error} />
            <Panel>
              <ListToolbar>
                <SearchInput value={search} onChange={setSearch} placeholder="Search salespeople" />
                <RecordCount shown={rows.length} total={data.length} />
              </ListToolbar>
              <BcTable
                aria-label="Salespeople"
                data-testid="salespeople-list"
                columns={columns}
                rows={rows}
                rowKey={(s) => s.code}
                rowClassName={(s) => (s.code === createdCode ? styles.created : undefined)}
                empty={data.length === 0 ? 'No salespeople yet.' : 'No salespeople match the search.'}
              />
            </Panel>
          </>
        )}
      </PageBody>
      {/* Mounted only while open: a closed Dialog in the first (StrictMode) render makes Fluent's
          keyboard tracker log "Keyborg instance … disposed incorrectly" when the page unloads. */}
      {dialogOpen && <NewSalespersonDialog open onClose={() => setDialogOpen(false)} onCreated={created} />}
    </>
  );
}
