import { Badge, Button, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { ArrowSync20Regular, Dismiss20Regular, Edit20Regular, Open20Regular, Save20Regular } from '@fluentui/react-icons';
import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import {
  api,
  errorMessage,
  type Blocked,
  type Customer,
  type CustomerDetail,
  type CustomerEdit,
  type QuoteSummary,
  type SalesOrder,
} from '../api';
import { BlockedBadge, OrderStatusBadge, QuoteStatusBadge } from '../components/Badges';
import { BcTable, type Column } from '../components/BcTable';
import { ChoiceRow, TextRow, type Choice } from '../components/EditableFields';
import { FastTab } from '../components/FastTab';
import { FieldColumn, FieldGrid, FieldRow, Value } from '../components/Fields';
import { PageBody, Panel } from '../components/Layout';
import { ActionButton, PageHeader } from '../components/PageHeader';
import { RouterLink } from '../components/RouterLink';
import { LoadError, Loading, Notice, StaleNotice, type NoticeState } from '../components/States';
import { d365AccountUrl } from '../config';
import { formatDate, formatMoney } from '../format';
import { usePolling } from '../hooks/usePolling';

const useStyles = makeStyles({
  aside: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  explain: {
    margin: 0,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '520px',
    paddingTop: tokens.spacingVerticalS,
  },
  d365Link: {
    marginTop: tokens.spacingVerticalS,
  },
});

/** The Customer card form, as strings so inputs can hold half-typed values. */
interface CustomerForm {
  displayName: string;
  addressLine1: string;
  city: string;
  postalCode: string;
  countryCode: string;
  phoneNumber: string;
  email: string;
  website: string;
  salespersonCode: string;
  creditLimit: string;
  blocked: Blocked;
  paymentTermsCode: string;
}

const NONE = '(none)';
const BLANK = '(blank)';
const PAYMENT_TERMS = ['14 DAYS', '30 DAYS', '60 DAYS'];

const blockedHelp: Record<Blocked, string> = {
  '': 'Not blocked: all transactions are allowed.',
  Ship: 'Ship: no new shipments to this customer.',
  Invoice: 'Invoice: no invoicing or shipping for this customer.',
  All: 'All: no transactions with this customer at all.',
};

const blockedChoices: Choice[] = [
  { value: BLANK, text: '(blank)' },
  { value: 'Ship', text: 'Ship' },
  { value: 'Invoice', text: 'Invoice' },
  { value: 'All', text: 'All' },
];

function toBlocked(value: string): Blocked {
  return value === 'Ship' || value === 'Invoice' || value === 'All' ? value : '';
}

function toForm(customer: Customer): CustomerForm {
  return {
    displayName: customer.displayName,
    addressLine1: customer.addressLine1 ?? '',
    city: customer.city ?? '',
    postalCode: customer.postalCode ?? '',
    countryCode: customer.countryCode,
    phoneNumber: customer.phoneNumber ?? '',
    email: customer.email ?? '',
    website: customer.website ?? '',
    salespersonCode: customer.salespersonCode ?? '',
    creditLimit: String(customer.creditLimit),
    blocked: toBlocked(customer.blocked),
    paymentTermsCode: customer.paymentTermsCode ?? '',
  };
}

const orNull = (value: string) => (value.trim() === '' ? null : value.trim());

function validate(form: CustomerForm): string | null {
  if (!form.displayName.trim()) return 'Name is required.';
  if (!form.countryCode.trim()) return 'Country/Region Code is required.';
  const creditLimit = Number(form.creditLimit);
  if (form.creditLimit.trim() === '' || !Number.isFinite(creditLimit) || creditLimit < 0) {
    return 'Credit Limit must be a number of 0 or more.';
  }
  return null;
}

/** The full edit shape PUT /api/app/customers/{id} takes. */
function toEdit(form: CustomerForm): CustomerEdit {
  return {
    displayName: form.displayName.trim(),
    addressLine1: orNull(form.addressLine1),
    city: orNull(form.city),
    postalCode: orNull(form.postalCode),
    countryCode: form.countryCode.trim().toUpperCase(),
    phoneNumber: orNull(form.phoneNumber),
    email: orNull(form.email),
    website: orNull(form.website),
    salespersonCode: orNull(form.salespersonCode),
    creditLimit: Number(form.creditLimit),
    blocked: form.blocked,
    paymentTermsCode: orNull(form.paymentTermsCode),
  };
}

const quoteColumns: Column<QuoteSummary>[] = [
  { key: 'number', header: 'No.', width: '110px', render: (q) => <RouterLink to={`/quotes/${q.id}`}>{q.number}</RouterLink> },
  { key: 'external', header: 'External Document No.', width: '170px', render: (q) => q.externalDocumentNumber },
  { key: 'description', header: 'Description', render: (q) => q.description },
  { key: 'status', header: 'Status', width: '110px', render: (q) => <QuoteStatusBadge status={q.status} /> },
  { key: 'amount', header: 'Amount Excl. Tax', width: '150px', align: 'end', render: (q) => formatMoney(q.totalAmountExcludingTax, q.currencyCode) },
  { key: 'order', header: 'Order No.', width: '110px', render: (q) => q.orderNumber },
];

const orderColumns: Column<SalesOrder>[] = [
  { key: 'number', header: 'No.', width: '110px', render: (o) => <RouterLink to={`/orders/${o.id}`}>{o.number}</RouterLink> },
  { key: 'quote', header: 'Quote No.', width: '110px', render: (o) => o.quoteNumber },
  { key: 'external', header: 'External Document No.', render: (o) => o.externalDocumentNumber },
  { key: 'date', header: 'Order Date', width: '130px', render: (o) => formatDate(o.orderDate) },
  { key: 'amount', header: 'Amount Excl. Tax', width: '150px', align: 'end', render: (o) => formatMoney(o.totalAmountExcludingTax, o.currencyCode) },
  { key: 'status', header: 'Status', width: '100px', render: (o) => <OrderStatusBadge status={o.status} /> },
];

/** Customer card. Keyed by id, so another customer starts with fresh state. */
export default function CustomerCardPage() {
  const { id = '' } = useParams();
  return <CustomerCard key={id} id={id} />;
}

function CustomerCard({ id }: { id: string }) {
  const detail = usePolling((signal) => api.customer(id, signal), 3000, id);
  if (!detail.data) {
    return (
      <>
        <PageHeader caption="Customer Card" title="Customer" />
        <PageBody>{detail.error ? <LoadError error={detail.error} onRetry={detail.refresh} /> : <Loading />}</PageBody>
      </>
    );
  }
  const current = detail.data;
  return (
    <CustomerCardView
      detail={current}
      error={detail.error}
      onSaved={(customer) => detail.replace({ ...current, customer })}
    />
  );
}

interface CustomerCardViewProps {
  detail: CustomerDetail;
  error: Error | undefined;
  /** Shows the customer the PUT returned right away. */
  onSaved: (customer: Customer) => void;
}

function CustomerCardView({ detail, error, onSaved }: CustomerCardViewProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const { customer, quotes, orders } = detail;
  const [form, setForm] = useState<CustomerForm | null>(null);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<NoticeState | null>(null);
  const { data: salespeople } = usePolling((signal) => api.salespeople(signal), null);

  const editing = form !== null;
  const shown = form ?? toForm(customer);
  const set = (change: Partial<CustomerForm>) => setForm((current) => (current ? { ...current, ...change } : current));

  const salespersonChoices: Choice[] = [
    { value: NONE, text: '(none)' },
    ...(salespeople ?? []).map((s) => ({ value: s.code, text: `${s.code} · ${s.displayName}` })),
  ];
  if (shown.salespersonCode && !salespersonChoices.some((c) => c.value === shown.salespersonCode)) {
    salespersonChoices.push({ value: shown.salespersonCode, text: shown.salespersonCode });
  }
  const paymentTermChoices: Choice[] = PAYMENT_TERMS.map((t) => ({ value: t, text: t }));
  if (shown.paymentTermsCode && !PAYMENT_TERMS.includes(shown.paymentTermsCode)) {
    paymentTermChoices.push({ value: shown.paymentTermsCode, text: shown.paymentTermsCode });
  }

  const startEdit = () => {
    setNotice(null);
    setForm(toForm(customer));
  };

  const save = async () => {
    if (!form) return;
    const problem = validate(form);
    if (problem) {
      setNotice({ intent: 'error', title: 'Check the customer card', message: problem });
      return;
    }
    setSaving(true);
    setNotice(null);
    try {
      const saved = await api.updateCustomer(customer.id, toEdit(form));
      onSaved(saved);
      setForm(null);
      setNotice({
        intent: 'success',
        title: `Customer ${saved.number} saved`,
        // Unlinked customers reach Dynamics 365 too: it upserts them by the BC customer id (go-live).
        message: 'The changes are on their way to the account in Dynamics 365 through NimBus.',
      });
    } catch (e) {
      setNotice({ intent: 'error', title: 'Business Central', message: errorMessage(e) });
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <PageHeader
        caption="Customer Card"
        title={`${customer.number} · ${customer.displayName}`}
        badges={
          <>
            <BlockedBadge blocked={customer.blocked} />
            {customer.origin === 1 && (
              <Badge appearance="outline" shape="rounded" color="informative">
                Converted from CRM prospect
              </Badge>
            )}
          </>
        }
        actions={
          editing ? (
            <>
              <Button
                appearance="primary"
                size="small"
                icon={saving ? <Spinner size="tiny" /> : <Save20Regular />}
                onClick={save}
                disabled={saving}
                data-testid="customer-save"
              >
                Save
              </Button>
              <ActionButton icon={<Dismiss20Regular />} onClick={() => setForm(null)} disabled={saving} data-testid="customer-cancel">
                Cancel
              </ActionButton>
            </>
          ) : (
            <>
              <ActionButton icon={<Edit20Regular />} onClick={startEdit} data-testid="customer-edit">
                Edit
              </ActionButton>
              {customer.crmAccountId && (
                <ActionButton
                  as="a"
                  href={d365AccountUrl(customer.crmAccountId)}
                  target="_blank"
                  rel="noopener noreferrer"
                  icon={<Open20Regular />}
                  data-testid="open-in-d365"
                >
                  Open in Dynamics 365
                </ActionButton>
              )}
            </>
          )
        }
        aside={
          <span className={styles.aside}>
            <ArrowSync20Regular aria-hidden /> Changes are sent to Dynamics 365 through NimBus.
          </span>
        }
      />

      <PageBody>
        <StaleNotice error={error} />
        <Notice notice={notice} onDismiss={() => setNotice(null)} />

        <Panel padded>
          <FastTab title="General" summary={[customer.city, customer.countryCode].filter(Boolean).join(', ')} data-testid="fasttab-general">
            <FieldGrid>
              <FieldColumn>
                <FieldRow label="No.">{customer.number}</FieldRow>
                <TextRow label="Name" value={shown.displayName} editing={editing} required onChange={(v) => set({ displayName: v })} data-testid="field-name" />
                <TextRow label="Address" value={shown.addressLine1} editing={editing} onChange={(v) => set({ addressLine1: v })} />
                <TextRow label="Post Code" value={shown.postalCode} editing={editing} onChange={(v) => set({ postalCode: v })} />
                <TextRow label="City" value={shown.city} editing={editing} onChange={(v) => set({ city: v })} />
                <TextRow label="Country/Region Code" value={shown.countryCode} editing={editing} required onChange={(v) => set({ countryCode: v })} />
              </FieldColumn>
              <FieldColumn>
                <TextRow label="Phone No." value={shown.phoneNumber} editing={editing} type="tel" onChange={(v) => set({ phoneNumber: v })} />
                <TextRow label="Email" value={shown.email} editing={editing} type="email" onChange={(v) => set({ email: v })} />
                <TextRow label="Home Page" value={shown.website} editing={editing} type="url" onChange={(v) => set({ website: v })} />
              </FieldColumn>
            </FieldGrid>
          </FastTab>

          <FastTab
            title="Invoicing"
            summary={`Credit limit ${formatMoney(customer.creditLimit, customer.currencyCode)} · ${customer.paymentTermsCode ?? ''}${customer.blocked ? ` · Blocked: ${customer.blocked}` : ''}`}
            data-testid="fasttab-invoicing"
          >
            <FieldGrid>
              <FieldColumn>
                <TextRow
                  label={`Credit Limit (${customer.currencyCode})`}
                  value={shown.creditLimit}
                  display={formatMoney(customer.creditLimit, customer.currencyCode)}
                  editing={editing}
                  type="number"
                  onChange={(v) => set({ creditLimit: v })}
                  data-testid="field-credit-limit"
                />
                <FieldRow label={`Balance Due (${customer.currencyCode})`}>
                  <Value>{formatMoney(customer.balanceDue, customer.currencyCode)}</Value>
                </FieldRow>
              </FieldColumn>
              <FieldColumn>
                <ChoiceRow
                  label="Payment Terms Code"
                  value={shown.paymentTermsCode}
                  choices={paymentTermChoices}
                  editing={editing}
                  placeholder="Choose payment terms"
                  onChange={(v) => set({ paymentTermsCode: v })}
                  data-testid="field-payment-terms"
                />
                <ChoiceRow
                  label="Blocked"
                  value={shown.blocked === '' ? BLANK : shown.blocked}
                  choices={blockedChoices}
                  editing={editing}
                  display={shown.blocked ? <BlockedBadge blocked={shown.blocked} /> : ''}
                  hint={blockedHelp[shown.blocked]}
                  onChange={(v) => set({ blocked: toBlocked(v) })}
                  data-testid="field-blocked"
                />
              </FieldColumn>
            </FieldGrid>
          </FastTab>

          <FastTab title="Sales" summary={customer.salespersonCode ?? ''} data-testid="fasttab-sales">
            <FieldGrid>
              <FieldColumn>
                <ChoiceRow
                  label="Salesperson Code"
                  value={shown.salespersonCode || NONE}
                  choices={salespersonChoices}
                  editing={editing}
                  display={shown.salespersonCode ? salespersonChoices.find((c) => c.value === shown.salespersonCode)?.text : ''}
                  onChange={(v) => set({ salespersonCode: v === NONE ? '' : v })}
                  data-testid="field-salesperson"
                />
              </FieldColumn>
            </FieldGrid>
          </FastTab>

          <FastTab
            title="CRM link"
            caption="AL extension fields"
            summary={customer.crmAccountId ? 'Linked to a Dynamics 365 account' : 'Not linked to Dynamics 365'}
            data-testid="fasttab-crm"
          >
            <FieldGrid>
              <FieldColumn>
                <FieldRow label="CRM Account ID">
                  <Value mono>{customer.crmAccountId}</Value>
                </FieldRow>
                <FieldRow label="Origin">
                  {customer.origin === 1 ? 'Converted from a CRM prospect (Make Order)' : 'Created in Business Central'}
                </FieldRow>
              </FieldColumn>
              <FieldColumn>
                <p className={styles.explain}>
                  Business Central owns this customer. NimBus mirrors every change to the linked account in Dynamics 365,
                  where these fields are read-only.
                </p>
                {customer.crmAccountId && (
                  <div className={styles.d365Link}>
                    <Button
                      as="a"
                      appearance="secondary"
                      size="small"
                      href={d365AccountUrl(customer.crmAccountId)}
                      target="_blank"
                      rel="noopener noreferrer"
                      icon={<Open20Regular />}
                    >
                      Open in Dynamics 365
                    </Button>
                  </div>
                )}
              </FieldColumn>
            </FieldGrid>
          </FastTab>

          <FastTab title="Sales Quotes" summary={`${quotes.length}`} data-testid="customer-quotes">
            <BcTable
              aria-label="Sales quotes of this customer"
              columns={quoteColumns}
              rows={quotes}
              rowKey={(q) => q.id}
              onRowClick={(q) => navigate(`/quotes/${q.id}`)}
              empty="No sales quotes for this customer."
            />
          </FastTab>

          <FastTab title="Sales Orders" summary={`${orders.length}`} data-testid="customer-orders">
            <BcTable
              aria-label="Sales orders of this customer"
              columns={orderColumns}
              rows={orders}
              rowKey={(o) => o.id}
              onRowClick={(o) => navigate(`/orders/${o.id}`)}
              empty="No sales orders for this customer."
            />
          </FastTab>
        </Panel>
      </PageBody>
    </>
  );
}
