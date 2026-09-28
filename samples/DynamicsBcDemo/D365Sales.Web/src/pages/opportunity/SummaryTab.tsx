import { useEffect, useState } from 'react';
import {
  Button,
  Input,
  makeStyles,
  Select,
  SpinButton,
  tokens,
  useId,
} from '@fluentui/react-components';
import { Save20Regular } from '@fluentui/react-icons';
import { api, errorMessage, type Opportunity, type OpportunityDetail, type ProductGroup } from '../../api';
import { QuoteStatusBadge } from '../../components/StatusBadges';
import { Timeline } from '../../components/Timeline';
import { ExternalLink, FieldRow, RecordLink, Section } from '../../components/ui';
import { bcQuoteUrl } from '../../config';
import { formatDate, formatDateTime, formatMoney, orDash, parseAmount, toDateInputValue } from '../../format';
import { useNotify } from '../../hooks/useNotify';
import { usePolling } from '../../hooks/usePolling';
import { isOpen, isStage, STAGES, stageLabel, type StageName } from '../../model';

const INTEGRATION_OWNED = 'Set by the integration from Business Central';

// Product groups arrive from Business Central (its initial sync), so the list keeps refreshing.
const PRODUCT_GROUPS_POLL_MS = 10_000;

const useStyles = makeStyles({
  columns: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.25fr) minmax(0, 1fr)',
    gap: '12px',
    alignItems: 'start',
    '@media (max-width: 1100px)': {
      gridTemplateColumns: 'minmax(0, 1fr)',
    },
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    minWidth: 0,
  },
  input: {
    width: '100%',
    maxWidth: '360px',
  },
  narrow: {
    width: '120px',
  },
  amount: {
    width: '180px',
  },
  hint: {
    display: 'block',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  note: {
    marginTop: '8px',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginTop: '12px',
    paddingTop: '12px',
    borderTop: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  unsaved: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});

interface SummaryDraft {
  name: string;
  estimatedCloseDate: string;
  closeProbability: number;
  stepName: StageName;
  /** As typed; blank is no estimate. */
  estimatedValue: string;
  /** Blank is no product group. */
  productGroupId: string;
}

const toDraft = (o: Opportunity): SummaryDraft => ({
  name: o.name,
  estimatedCloseDate: toDateInputValue(o.estimatedCloseDate),
  closeProbability: o.closeProbability,
  stepName: isStage(o.stepName) ? o.stepName : STAGES[0],
  // Plain digits, so parseAmount reads the stored value back exactly.
  estimatedValue: o.estimatedValue === null ? '' : String(o.estimatedValue),
  productGroupId: o.csProductGroupId ?? '',
});

const sameDraft = (a: SummaryDraft, b: SummaryDraft) =>
  a.name === b.name &&
  a.estimatedCloseDate === b.estimatedCloseDate &&
  a.closeProbability === b.closeProbability &&
  a.stepName === b.stepName &&
  parseAmount(a.estimatedValue) === parseAmount(b.estimatedValue) &&
  a.productGroupId === b.productGroupId;

interface ProductGroupSelectProps {
  id: string;
  value: string;
  /** The opportunity's saved product group, kept selectable while the list loads. */
  current: ProductGroup | null;
  onChange: (productGroupId: string) => void;
}

/** The product groups Business Central has synced; polled, so they appear once its initial sync has run. */
function ProductGroupSelect({ id, value, current, onChange }: ProductGroupSelectProps) {
  const styles = useStyles();
  const poll = usePolling('productgroups', (signal) => api.productGroups(signal), PRODUCT_GROUPS_POLL_MS);
  const groups = poll.data ?? [];
  const options =
    current && !groups.some((group) => group.productGroupId === current.productGroupId) ? [current, ...groups] : groups;

  return (
    <>
      <Select
        id={id}
        className={styles.input}
        appearance="filled-darker"
        value={value}
        disabled={options.length === 0}
        onChange={(_, data) => onChange(data.value)}
        data-testid="product-group"
      >
        <option value="">—</option>
        {options.map((group) => (
          <option key={group.productGroupId} value={group.productGroupId}>
            {group.name}
          </option>
        ))}
      </Select>
      {poll.data?.length === 0 && (
        <span className={styles.hint}>Product groups come from Business Central with the initial sync.</span>
      )}
    </>
  );
}

interface SummaryTabProps {
  detail: OpportunityDetail;
  refresh: () => Promise<void>;
  onShowTimeline: () => void;
}

export function SummaryTab({ detail, refresh, onShowTimeline }: SummaryTabProps) {
  const styles = useStyles();
  const notify = useNotify();
  const ids = {
    name: useId('topic'),
    productGroup: useId('product-group'),
    revenue: useId('revenue'),
    close: useId('close'),
    probability: useId('probability'),
    stage: useId('stage'),
  };
  const { opportunity: o, account, owner, productGroup } = detail;
  const editable = isOpen(o);
  const [draft, setDraft] = useState<SummaryDraft | null>(null);
  const [saving, setSaving] = useState(false);

  // The integration can close the opportunity (won) while the seller is looking at it.
  useEffect(() => {
    if (!editable) setDraft(null);
  }, [editable]);

  const server = toDraft(o);
  const values = draft ?? server;
  const dirty = draft !== null && !sameDraft(draft, server);
  const estimatedValue = parseAmount(values.estimatedValue);
  const problem = !values.name.trim()
    ? 'The topic is required.'
    : Number.isNaN(estimatedValue)
      ? 'Est. revenue must be an amount, such as 250000.'
      : undefined;
  const change = (patch: Partial<SummaryDraft>) => setDraft({ ...values, ...patch });

  const save = async () => {
    setSaving(true);
    try {
      await api.updateOpportunity(o.opportunityId, {
        name: values.name.trim(),
        estimatedCloseDate: values.estimatedCloseDate ? `${values.estimatedCloseDate}T00:00:00` : null,
        closeProbability: values.closeProbability,
        stepName: values.stepName,
        estimatedValue,
        productGroupId: values.productGroupId || null,
      });
      await refresh();
      setDraft(null);
      notify.success('Opportunity saved', 'Saved and sent to Business Central.');
    } catch (error) {
      notify.error('The opportunity was not saved', errorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className={styles.columns}>
      <Section title="Opportunity" caption={editable ? undefined : 'Closed — read-only'} testId="opportunity-summary">
        <FieldRow label="Topic" htmlFor={ids.name}>
          {editable ? (
            <Input
              id={ids.name}
              className={styles.input}
              appearance="filled-darker"
              value={values.name}
              onChange={(_, data) => change({ name: data.value })}
            />
          ) : (
            o.name
          )}
        </FieldRow>
        <FieldRow label="Account">
          {account ? <RecordLink to={`/accounts/${account.accountId}`}>{account.name}</RecordLink> : '—'}
        </FieldRow>
        <FieldRow label="Product group" htmlFor={ids.productGroup}>
          {editable ? (
            <ProductGroupSelect
              id={ids.productGroup}
              value={values.productGroupId}
              current={productGroup}
              onChange={(productGroupId) => change({ productGroupId })}
            />
          ) : (
            orDash(productGroup?.name)
          )}
        </FieldRow>
        <FieldRow label="Est. revenue" htmlFor={ids.revenue}>
          {editable ? (
            <Input
              id={ids.revenue}
              className={styles.amount}
              appearance="filled-darker"
              inputMode="decimal"
              contentBefore="€"
              value={values.estimatedValue}
              aria-invalid={Number.isNaN(estimatedValue) || undefined}
              onChange={(_, data) => change({ estimatedValue: data.value })}
              data-testid="estimated-revenue-input"
            />
          ) : (
            formatMoney(o.estimatedValue)
          )}
          <span className={styles.hint}>
            The seller&apos;s estimate. The Business Central quote is shown on the Quotes tab.
          </span>
        </FieldRow>
        <FieldRow label="Est. close date" htmlFor={ids.close}>
          {editable ? (
            <Input
              id={ids.close}
              type="date"
              appearance="filled-darker"
              value={values.estimatedCloseDate}
              onChange={(_, data) => change({ estimatedCloseDate: data.value })}
            />
          ) : (
            formatDate(o.estimatedCloseDate)
          )}
        </FieldRow>
        <FieldRow label="Probability (%)" htmlFor={ids.probability}>
          {editable ? (
            <SpinButton
              id={ids.probability}
              className={styles.narrow}
              appearance="filled-darker"
              min={0}
              max={100}
              step={5}
              value={values.closeProbability}
              onChange={(_, data) => {
                const next =
                  data.value ?? (data.displayValue !== undefined ? Number.parseInt(data.displayValue, 10) : NaN);
                if (Number.isFinite(next)) change({ closeProbability: Math.min(100, Math.max(0, Math.round(next))) });
              }}
            />
          ) : (
            `${o.closeProbability}%`
          )}
        </FieldRow>
        <FieldRow label="Stage" htmlFor={ids.stage}>
          {editable ? (
            <Select
              id={ids.stage}
              className={styles.narrow}
              appearance="filled-darker"
              value={values.stepName}
              onChange={(_, data) => {
                if (isStage(data.value)) change({ stepName: data.value });
              }}
            >
              {STAGES.map((stage) => (
                <option key={stage} value={stage}>
                  {stageLabel(stage)}
                </option>
              ))}
            </Select>
          ) : (
            stageLabel(o.stepName)
          )}
        </FieldRow>
        <FieldRow label="Owner">{orDash(owner?.fullName)}</FieldRow>
        <FieldRow label="Opportunity no.">{o.csNumber}</FieldRow>
        <FieldRow label="Created on">{formatDateTime(o.createdOn)}</FieldRow>
        {editable && (
          <div className={styles.actions}>
            <Button
              appearance="primary"
              icon={<Save20Regular />}
              disabled={!dirty || problem !== undefined || saving}
              onClick={() => void save()}
              data-testid="save-opportunity"
            >
              {saving ? 'Saving…' : 'Save'}
            </Button>
            <Button disabled={!dirty || saving} onClick={() => setDraft(null)}>
              Discard changes
            </Button>
            {dirty && <span className={styles.unsaved}>{problem ?? 'Unsaved changes'}</span>}
          </div>
        )}
      </Section>

      <div className={styles.stack}>
        <Section title="Business Central" caption="Set by the integration — read-only">
          <FieldRow label="Quote" lockedReason={INTEGRATION_OWNED}>
            {o.csBcQuoteId && o.csBcQuoteNumber ? (
              <ExternalLink href={bcQuoteUrl(o.csBcQuoteId)}>{o.csBcQuoteNumber}</ExternalLink>
            ) : (
              '—'
            )}
          </FieldRow>
          <FieldRow label="Quote status" lockedReason={INTEGRATION_OWNED}>
            {o.csBcQuoteStatus ? <QuoteStatusBadge status={o.csBcQuoteStatus} /> : '—'}
          </FieldRow>
          <span className={styles.note}>
            This opportunity is also in Business Central. A Business Central user creates the quote there and links
            it to the opportunity.
          </span>
        </Section>
        <Section
          title="Recent activity"
          actions={
            <Button appearance="transparent" size="small" onClick={onShowTimeline}>
              View all
            </Button>
          }
        >
          <Timeline entries={detail.timeline} limit={4} testId="recent-activity" />
        </Section>
      </div>
    </div>
  );
}
