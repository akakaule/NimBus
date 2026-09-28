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
import { api, errorMessage, type MakeOrderResult, type QuoteDetail } from '../api';
import { formatMoney } from '../format';
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
  facts: {
    display: 'grid',
    gridTemplateColumns: 'max-content 1fr',
    columnGap: tokens.spacingHorizontalXL,
    rowGap: tokens.spacingVerticalXS,
    margin: 0,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
  },
  factLabel: {
    color: tokens.colorNeutralForeground3,
  },
  factValue: {
    margin: 0,
    fontWeight: tokens.fontWeightSemibold,
  },
  note: {
    margin: 0,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

interface MakeOrderDialogProps {
  open: boolean;
  quote: QuoteDetail;
  onClose: () => void;
  onDone: (result: MakeOrderResult) => void;
}

/**
 * Make Order: converts the quote, and a prospect contact into a customer first. The order stays in
 * Business Central; Dynamics 365 hears that the quote was accepted and wins the opportunity.
 */
export function MakeOrderDialog({ open, quote, onClose, onDone }: MakeOrderDialogProps) {
  return (
    <Dialog
      open={open}
      onOpenChange={(_, data) => {
        if (!data.open) onClose();
      }}
    >
      <DialogSurface>
        {/* The form mounts with the surface, so every opening starts from the proposed template. */}
        <MakeOrderForm quote={quote} onCancel={onClose} onDone={onDone} />
      </DialogSurface>
    </Dialog>
  );
}

function MakeOrderForm({ quote, onCancel, onDone }: { quote: QuoteDetail; onCancel: () => void; onDone: (result: MakeOrderResult) => void }) {
  const styles = useStyles();
  const templates = usePolling((signal) => api.templates(signal), null);
  const [templateCode, setTemplateCode] = useState(quote.proposedTemplate?.code ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const contact = quote.contact;
  const isProspect = quote.sellToType === 'Contact' && !contact?.customerId;
  const opportunity = quote.crmOpportunity;
  const wonNote = quote.crmOpportunityId
    ? `Dynamics 365 closes ${opportunity ? `opportunity ${opportunity.number}` : 'the opportunity'} as won.`
    : null;
  const template =
    templates.data?.find((t) => t.code === templateCode) ??
    (templateCode === quote.proposedTemplate?.code ? quote.proposedTemplate : null) ??
    templates.data?.[0] ??
    null;

  const confirm = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onDone(await api.makeOrder(quote.id, isProspect ? (template?.code ?? null) : null));
    } catch (e) {
      setError(errorMessage(e));
      setBusy(false);
    }
  };

  return (
    <form onSubmit={confirm} data-testid="make-order-dialog">
      <DialogBody>
        <DialogTitle>Make Order</DialogTitle>
        <DialogContent className={styles.content}>
          {isProspect && contact ? (
            <>
              <p className={styles.text}>
                Sell-to contact <strong>{contact.number}</strong> ({contact.displayName}) is not a customer yet. Business
                Central will create a customer from the contact using the customer template below, then create the sales
                order.
              </p>
              <Field label="Customer template" required>
                <Dropdown
                  value={template ? `${template.code} – ${template.description}` : ''}
                  selectedOptions={template ? [template.code] : []}
                  placeholder={templates.loading ? 'Loading templates…' : 'Choose a template'}
                  onOptionSelect={(_, data) => setTemplateCode(data.optionValue ?? '')}
                  disabled={busy}
                  data-testid="customer-template"
                >
                  {(templates.data ?? []).map((t) => (
                    <Option key={t.code} value={t.code} text={`${t.code} – ${t.description}`}>
                      {`${t.code} – ${t.description}`}
                    </Option>
                  ))}
                </Dropdown>
              </Field>
              {template && (
                <dl className={styles.facts}>
                  <dt className={styles.factLabel}>Credit limit</dt>
                  <dd className={styles.factValue}>{formatMoney(template.creditLimit, quote.currencyCode)}</dd>
                  <dt className={styles.factLabel}>Payment terms</dt>
                  <dd className={styles.factValue}>{template.paymentTermsCode}</dd>
                </dl>
              )}
              <p className={styles.note} data-testid="make-order-note">
                The prospect becomes a buying customer in Business Central, which owns its master data from then on.
                {wonNote && ` ${wonNote}`}
              </p>
            </>
          ) : (
            <>
              {contact?.customerId ? (
                <p className={styles.text}>
                  Contact <strong>{contact.number}</strong> ({contact.displayName}) is already customer{' '}
                  <strong>{contact.customerNumber}</strong>. Convert quote <strong>{quote.number}</strong> to a sales order
                  for that customer?
                </p>
              ) : (
                <p className={styles.text}>
                  Convert quote <strong>{quote.number}</strong> to a sales order?
                </p>
              )}
              {wonNote && (
                <p className={styles.note} data-testid="make-order-note">
                  {wonNote}
                </p>
              )}
            </>
          )}

          {error && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Business Central</MessageBarTitle>
                {error}
              </MessageBarBody>
            </MessageBar>
          )}
          {templates.error && isProspect && (
            <MessageBar intent="warning">
              <MessageBarBody>Couldn't load the customer templates: {templates.error.message}</MessageBarBody>
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
            disabled={busy || (isProspect && !template)}
            icon={busy ? <Spinner size="tiny" /> : undefined}
            data-testid="make-order-confirm"
          >
            Confirm
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
