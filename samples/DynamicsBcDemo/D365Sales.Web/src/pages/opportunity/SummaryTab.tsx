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
import { api, errorMessage, type Opportunity, type OpportunityDetail } from '../../api';
import { QuoteStatusBadge } from '../../components/StatusBadges';
import { Timeline } from '../../components/Timeline';
import { ExternalLink, FieldRow, RecordLink, Section } from '../../components/ui';
import { bcQuoteUrl } from '../../config';
import { formatDate, formatDateTime, formatMoney, orDash, toDateInputValue } from '../../format';
import { useNotify } from '../../hooks/useNotify';
import { isOpen, isStage, STAGES, stageLabel, type StageName } from '../../model';

const INTEGRATION_OWNED = 'Set by the integration from Business Central';

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
  hint: {
    display: 'block',
    fontSize: tokens.fontSizeBase200,
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
}

const toDraft = (o: Opportunity): SummaryDraft => ({
  name: o.name,
  estimatedCloseDate: toDateInputValue(o.estimatedCloseDate),
  closeProbability: o.closeProbability,
  stepName: isStage(o.stepName) ? o.stepName : STAGES[0],
});

const sameDraft = (a: SummaryDraft, b: SummaryDraft) =>
  a.name === b.name &&
  a.estimatedCloseDate === b.estimatedCloseDate &&
  a.closeProbability === b.closeProbability &&
  a.stepName === b.stepName;

interface SummaryTabProps {
  detail: OpportunityDetail;
  refresh: () => Promise<void>;
  onShowTimeline: () => void;
}

export function SummaryTab({ detail, refresh, onShowTimeline }: SummaryTabProps) {
  const styles = useStyles();
  const notify = useNotify();
  const ids = { name: useId('topic'), close: useId('close'), probability: useId('probability'), stage: useId('stage') };
  const { opportunity: o, account, owner } = detail;
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
  const valid = values.name.trim().length > 0;
  const change = (patch: Partial<SummaryDraft>) => setDraft({ ...values, ...patch });

  const save = async () => {
    setSaving(true);
    try {
      await api.updateOpportunity(o.opportunityId, {
        name: values.name.trim(),
        estimatedCloseDate: values.estimatedCloseDate ? `${values.estimatedCloseDate}T00:00:00` : null,
        closeProbability: values.closeProbability,
        stepName: values.stepName,
      });
      await refresh();
      setDraft(null);
      notify.success('Opportunity saved');
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
        <FieldRow label="Est. revenue">
          {formatMoney(o.estimatedValue)}
          <span className={styles.hint}>
            {o.csBcQuoteId
              ? 'Follows the Business Central quote total.'
              : 'From the product lines at CRM list prices until Business Central quotes.'}
          </span>
        </FieldRow>
        <FieldRow label="Owner">{orDash(owner?.fullName)}</FieldRow>
        <FieldRow label="Opportunity no.">{o.csNumber}</FieldRow>
        <FieldRow label="Created on">{formatDateTime(o.createdOn)}</FieldRow>
        {editable && (
          <div className={styles.actions}>
            <Button
              appearance="primary"
              icon={<Save20Regular />}
              disabled={!dirty || !valid || saving}
              onClick={() => void save()}
              data-testid="save-opportunity"
            >
              {saving ? 'Saving…' : 'Save'}
            </Button>
            <Button disabled={!dirty || saving} onClick={() => setDraft(null)}>
              Discard changes
            </Button>
            {dirty && <span className={styles.unsaved}>{valid ? 'Unsaved changes' : 'The topic is required.'}</span>}
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
          <FieldRow label="Sales order" lockedReason={INTEGRATION_OWNED}>
            {orDash(o.csBcOrderNumber)}
          </FieldRow>
          <FieldRow label="Quote requested" lockedReason={INTEGRATION_OWNED}>
            {o.csQuoteRequestedOn
              ? `${formatDateTime(o.csQuoteRequestedOn)} · revision ${o.csQuoteRequestRevision}`
              : '—'}
          </FieldRow>
          <FieldRow label="Lines revision" lockedReason="Goes up each time the product lines are saved">
            {o.csLinesRevision}
          </FieldRow>
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
