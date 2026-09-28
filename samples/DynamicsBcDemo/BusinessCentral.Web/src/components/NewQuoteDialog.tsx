import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Dropdown,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Option,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { useState, type FormEvent } from 'react';
import { api, errorMessage, type CrmOpportunity } from '../api';
import { useCreateQuote } from '../hooks/useCreateQuote';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  text: {
    margin: 0,
    lineHeight: tokens.lineHeightBase400,
  },
  dropdown: {
    width: '100%',
  },
  note: {
    margin: 0,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

const label = (o: CrmOpportunity) => `${o.number} · ${o.name} · ${o.accountName}`;

interface NewQuoteDialogProps {
  open: boolean;
  onClose: () => void;
}

/** New Sales Quote, as a dialog: a quote is always made for an open CRM opportunity. */
export function NewQuoteDialog({ open, onClose }: NewQuoteDialogProps) {
  return (
    <Dialog
      open={open}
      onOpenChange={(_, data) => {
        if (!data.open) onClose();
      }}
    >
      <DialogSurface>
        {/* Mounts with the surface: every opening loads the opportunities afresh. */}
        <NewQuoteForm onCancel={onClose} />
      </DialogSurface>
    </Dialog>
  );
}

function NewQuoteForm({ onCancel }: { onCancel: () => void }) {
  const styles = useStyles();
  const createQuote = useCreateQuote();
  const opportunities = usePolling((signal) => api.crmOpportunities(signal), null);
  const [selectedId, setSelectedId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const open = (opportunities.data ?? []).filter((o) => o.status === 'Open');
  const selected = open.find((o) => o.id === selectedId) ?? null;

  // On success the new quote's card opens, which closes the dialog with the Sales Quotes page.
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!selected) {
      setError('Choose a CRM opportunity.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await createQuote(selected.id);
    } catch (e) {
      setError(errorMessage(e));
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} data-testid="new-quote-dialog">
      <DialogBody>
        <DialogTitle>New Sales Quote</DialogTitle>
        <DialogContent className={styles.content}>
          <p className={styles.text}>
            Choose the CRM opportunity the quote is for. Business Central links the two and takes the customer or
            prospect and the salesperson from the opportunity.
          </p>
          <Field label="CRM opportunity" required>
            <Dropdown
              className={styles.dropdown}
              value={selected ? label(selected) : ''}
              selectedOptions={selected ? [selected.id] : []}
              placeholder={opportunities.loading ? 'Loading opportunities…' : 'Choose an opportunity'}
              onOptionSelect={(_, data) => setSelectedId(data.optionValue ?? '')}
              disabled={busy || open.length === 0}
              data-testid="new-quote-opportunity"
            >
              {open.map((o) => (
                <Option key={o.id} value={o.id} text={label(o)}>
                  {label(o)}
                </Option>
              ))}
            </Dropdown>
          </Field>
          {opportunities.data && open.length === 0 && (
            <p className={styles.note} data-testid="new-quote-none">
              There are no open CRM opportunities. Dynamics 365 sends them here when a seller creates or changes one.
            </p>
          )}

          {error && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Business Central</MessageBarTitle>
                {error}
              </MessageBarBody>
            </MessageBar>
          )}
          {opportunities.error && (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load the CRM opportunities: {opportunities.error.message}</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button appearance="secondary" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
          <Button
            appearance="primary"
            type="submit"
            disabled={busy || !selected}
            icon={busy ? <Spinner size="tiny" /> : undefined}
            data-testid="new-quote-create"
          >
            Create
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
