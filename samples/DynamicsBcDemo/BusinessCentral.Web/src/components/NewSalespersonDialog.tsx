import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { useState, type FormEvent } from 'react';
import { api, errorMessage, type Salesperson } from '../api';

const useStyles = makeStyles({
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  code: {
    maxWidth: '160px',
  },
});

interface NewSalespersonDialogProps {
  open: boolean;
  onClose: () => void;
  onCreated: (salesperson: Salesperson) => void;
}

/** New Salesperson/Purchaser card, as a dialog. */
export function NewSalespersonDialog({ open, onClose, onCreated }: NewSalespersonDialogProps) {
  return (
    <Dialog
      open={open}
      onOpenChange={(_, data) => {
        if (!data.open) onClose();
      }}
    >
      <DialogSurface>
        {/* Mounts with the surface: every opening starts with an empty form. */}
        <NewSalespersonForm onCancel={onClose} onCreated={onCreated} />
      </DialogSurface>
    </Dialog>
  );
}

function NewSalespersonForm({ onCancel, onCreated }: { onCancel: () => void; onCreated: (salesperson: Salesperson) => void }) {
  const styles = useStyles();
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const complete = code.trim() !== '' && name.trim() !== '' && email.includes('@');

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!complete) {
      setError('Code (max 20 characters), name and e-mail are required.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      onCreated(await api.createSalesperson({ code: code.trim(), displayName: name.trim(), email: email.trim() }));
    } catch (e) {
      setError(errorMessage(e));
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} data-testid="new-salesperson-dialog">
      <DialogBody>
        <DialogTitle>New Salesperson</DialogTitle>
        <DialogContent className={styles.content}>
          <Field label="Code" required hint="Up to 20 characters, e.g. the seller's initials.">
            <Input
              className={styles.code}
              value={code}
              maxLength={20}
              autoFocus
              disabled={busy}
              onChange={(_, data) => setCode(data.value.toUpperCase())}
              data-testid="salesperson-code"
            />
          </Field>
          <Field label="Name" required>
            <Input value={name} disabled={busy} onChange={(_, data) => setName(data.value)} data-testid="salesperson-name" />
          </Field>
          <Field label="E-mail" required hint="Dynamics 365 sellers are matched to salespeople by e-mail.">
            <Input
              type="email"
              value={email}
              disabled={busy}
              onChange={(_, data) => setEmail(data.value)}
              data-testid="salesperson-email"
            />
          </Field>
          {error && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Business Central</MessageBarTitle>
                {error}
              </MessageBarBody>
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
            disabled={busy}
            icon={busy ? <Spinner size="tiny" /> : undefined}
            data-testid="salesperson-save"
          >
            Create
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
