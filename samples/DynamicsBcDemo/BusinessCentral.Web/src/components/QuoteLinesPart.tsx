import {
  Badge,
  Button,
  Dropdown,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Option,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  tokens,
} from '@fluentui/react-components';
import { Add20Regular, ArrowSync20Regular, Delete20Regular, Dismiss20Regular, Edit20Regular, Save20Regular } from '@fluentui/react-icons';
import { useRef, useState } from 'react';
import { api, errorMessage, type Item, type QuoteDetail, type QuoteLine, type QuoteLineEdit } from '../api';
import { formatMoney, formatNumber } from '../format';
import { usePolling } from '../hooks/usePolling';
import { BcTable, type Column } from './BcTable';
import { ActionButton } from './PageHeader';

const useStyles = makeStyles({
  toolbar: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXXS,
    marginLeft: `calc(${tokens.spacingHorizontalS} * -1)`,
    marginBottom: tokens.spacingVerticalS,
  },
  caption: {
    marginLeft: 'auto',
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  headerCell: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
    borderBottom: `1px solid ${tokens.colorNeutralStroke1}`,
  },
  headerEnd: {
    '& .fui-TableHeaderCell__button': {
      justifyContent: 'flex-end',
    },
  },
  cell: {
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase300,
  },
  end: {
    textAlign: 'right',
    whiteSpace: 'nowrap',
    fontVariantNumeric: 'tabular-nums',
  },
  numberInput: {
    textAlign: 'right',
  },
  fill: {
    width: '100%',
    minWidth: 0,
  },
  option: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    width: '100%',
  },
  optionNumber: {
    fontWeight: tokens.fontWeightSemibold,
    minWidth: '92px',
  },
  optionName: {
    flexGrow: 1,
  },
  optionPrice: {
    color: tokens.colorNeutralForeground3,
    fontVariantNumeric: 'tabular-nums',
  },
  draftTotal: {
    display: 'flex',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    fontSize: tokens.fontSizeBase300,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  draftTotalValue: {
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    minWidth: '140px',
    textAlign: 'right',
  },
  message: {
    marginBottom: tokens.spacingVerticalS,
  },
  scroller: {
    overflowX: 'auto',
  },
  table: {
    minWidth: '860px',
  },
});

interface DraftLine {
  key: string;
  itemNumber: string;
  description: string;
  quantity: string;
  unitPrice: string;
  discountPercent: string;
}

const round2 = (value: number) => Math.round(value * 100) / 100;

/** The amount BC computes for a line: quantity × unit price, less the line discount. */
function lineAmount(line: DraftLine): number {
  const quantity = Number(line.quantity);
  const unitPrice = Number(line.unitPrice);
  const discount = Number(line.discountPercent || 0);
  if (![quantity, unitPrice, discount].every(Number.isFinite)) return 0;
  return round2(quantity * unitPrice * (1 - discount / 100));
}

function toDraft(line: QuoteLine): DraftLine {
  return {
    key: line.id,
    itemNumber: line.itemNumber,
    description: line.description,
    quantity: String(line.quantity),
    unitPrice: String(line.unitPrice),
    discountPercent: String(line.discountPercent),
  };
}

/** The first problem with the edited lines, or null when BC can take them. */
function validate(lines: DraftLine[]): string | null {
  if (lines.length === 0) return 'A quote needs at least one line.';
  for (const [index, line] of lines.entries()) {
    const n = index + 1;
    if (!line.itemNumber) return `Choose an item on line ${n}.`;
    const quantity = Number(line.quantity);
    if (line.quantity.trim() === '' || !Number.isFinite(quantity) || quantity <= 0) return `Line ${n}: quantity must be greater than 0.`;
    const unitPrice = Number(line.unitPrice);
    if (line.unitPrice.trim() === '' || !Number.isFinite(unitPrice) || unitPrice < 0) return `Line ${n}: unit price can't be negative.`;
    const discount = Number(line.discountPercent || 0);
    if (!Number.isFinite(discount) || discount < 0 || discount > 100) return `Line ${n}: line discount must be between 0 and 100 %.`;
  }
  return null;
}

function toEdit(line: DraftLine): QuoteLineEdit {
  return {
    itemNumber: line.itemNumber,
    quantity: Number(line.quantity),
    unitPrice: Number(line.unitPrice),
    discountPercent: Number(line.discountPercent || 0),
  };
}

interface QuoteLinesPartProps {
  quote: QuoteDetail;
  /** Lines can change while the quote is Draft or Sent. */
  editable: boolean;
  onEditingChange: (editing: boolean) => void;
  onSaved: (updated: QuoteDetail) => void;
}

/** The Lines part of the quote card: read-only grid, or an edit grid while editing. */
export function QuoteLinesPart({ quote, editable, onEditingChange, onSaved }: QuoteLinesPartProps) {
  const styles = useStyles();
  const [draft, setDraft] = useState<DraftLine[] | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const newLineCounter = useRef(0);
  const { data: items } = usePolling((signal) => api.items(signal), null);

  const currency = quote.currencyCode;

  const setEditing = (lines: DraftLine[] | null) => {
    setDraft(lines);
    setError(null);
    onEditingChange(lines !== null);
  };

  const update = (key: string, change: Partial<DraftLine>) =>
    setDraft((lines) => lines && lines.map((line) => (line.key === key ? { ...line, ...change } : line)));

  const pickItem = (key: string, itemNumber: string | undefined) => {
    const item = items?.find((i) => i.number === itemNumber);
    if (item) update(key, { itemNumber: item.number, description: item.displayName, unitPrice: String(item.unitPrice) });
  };

  const addLine = () => {
    newLineCounter.current += 1;
    const line: DraftLine = {
      key: `new-${newLineCounter.current}`,
      itemNumber: '',
      description: '',
      quantity: '1',
      unitPrice: '',
      discountPercent: '0',
    };
    setDraft((lines) => [...(lines ?? []), line]);
  };

  const save = async () => {
    if (!draft) return;
    const problem = validate(draft);
    if (problem) {
      setError(problem);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const updated = await api.updateQuoteLines(quote.id, draft.map(toEdit));
      setEditing(null);
      onSaved(updated);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setSaving(false);
    }
  };

  const readColumns: Column<QuoteLine>[] = [
    { key: 'item', header: 'Item No.', width: '130px', render: (l) => l.itemNumber },
    { key: 'description', header: 'Description', render: (l) => l.description },
    { key: 'quantity', header: 'Quantity', width: '100px', align: 'end', render: (l) => formatNumber(l.quantity) },
    { key: 'price', header: 'Unit Price Excl. Tax', width: '160px', align: 'end', render: (l) => formatMoney(l.unitPrice, currency, 2) },
    { key: 'discount', header: 'Line Discount %', width: '130px', align: 'end', render: (l) => formatNumber(l.discountPercent) },
    {
      key: 'amount',
      header: 'Line Amount Excl. Tax',
      width: '170px',
      align: 'end',
      render: (l) => formatMoney(l.amountExcludingTax, currency, 2),
    },
  ];

  return (
    <div data-testid="quote-lines">
      <div className={styles.toolbar}>
        {draft === null ? (
          editable && (
            <ActionButton icon={<Edit20Regular />} onClick={() => setEditing(quote.lines.map(toDraft))} data-testid="edit-lines">
              Edit lines
            </ActionButton>
          )
        ) : (
          <>
            <ActionButton icon={<Add20Regular />} onClick={addLine} disabled={saving} data-testid="add-line">
              New line
            </ActionButton>
            <Button
              appearance="primary"
              size="small"
              icon={saving ? <Spinner size="tiny" /> : <Save20Regular />}
              onClick={save}
              disabled={saving}
              data-testid="save-lines"
            >
              Save
            </Button>
            <ActionButton icon={<Dismiss20Regular />} onClick={() => setEditing(null)} disabled={saving} data-testid="cancel-lines">
              Cancel
            </ActionButton>
          </>
        )}
        <span className={styles.caption}>
          {draft !== null ? (
            quote.crmOpportunityId && (
              <>
                <ArrowSync20Regular aria-hidden /> Changes are sent to Dynamics 365 through NimBus.
              </>
            )
          ) : (
            !editable && `This quote is ${quote.status} and can no longer be changed.`
          )}
        </span>
      </div>

      {error && (
        <MessageBar intent="error" className={styles.message}>
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {draft === null ? (
        <BcTable
          aria-label="Quote lines"
          columns={readColumns}
          rows={quote.lines}
          rowKey={(l) => l.id}
          empty={
            editable ? (
              <span data-testid="no-lines-hint">No lines yet — choose Edit lines to add items.</span>
            ) : (
              'This quote has no lines.'
            )
          }
        />
      ) : (
        <EditGrid
          lines={draft}
          items={items ?? []}
          currency={currency}
          disabled={saving}
          onChange={update}
          onPickItem={pickItem}
          onRemove={(key) => setDraft((lines) => lines && lines.filter((l) => l.key !== key))}
        />
      )}
    </div>
  );
}

interface EditGridProps {
  lines: DraftLine[];
  items: Item[];
  currency: string;
  disabled: boolean;
  onChange: (key: string, change: Partial<DraftLine>) => void;
  onPickItem: (key: string, itemNumber: string | undefined) => void;
  onRemove: (key: string) => void;
}

function EditGrid({ lines, items, currency, disabled, onChange, onPickItem, onRemove }: EditGridProps) {
  const styles = useStyles();
  const total = lines.reduce((sum, line) => sum + lineAmount(line), 0);
  const headers: { label: string; width?: string; end?: boolean }[] = [
    { label: 'Item No.', width: '240px' },
    { label: 'Description' },
    { label: 'Quantity', width: '110px', end: true },
    { label: 'Unit Price Excl. Tax', width: '150px', end: true },
    { label: 'Line Discount %', width: '120px', end: true },
    { label: 'Line Amount Excl. Tax', width: '160px', end: true },
    { label: '', width: '48px' },
  ];

  return (
    <div className={styles.scroller}>
      <Table size="small" aria-label="Edit quote lines" className={styles.table}>
        <TableHeader>
          <TableRow>
            {headers.map((h, index) => (
              <TableHeaderCell
                key={index}
                className={mergeClasses(styles.headerCell, h.end && styles.headerEnd)}
                style={h.width ? { width: h.width } : undefined}
              >
                {h.label}
              </TableHeaderCell>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {lines.map((line, index) => (
            <TableRow key={line.key} data-testid="edit-line">
              <TableCell className={styles.cell}>
                {line.key.startsWith('new-') ? (
                  <ItemDropdown
                    items={items}
                    currency={currency}
                    value={line.itemNumber}
                    disabled={disabled}
                    label={`Item for line ${index + 1}`}
                    onPick={(itemNumber) => onPickItem(line.key, itemNumber)}
                  />
                ) : (
                  line.itemNumber
                )}
              </TableCell>
              <TableCell className={styles.cell}>{line.description}</TableCell>
              <TableCell className={styles.cell}>
                <Input
                  type="number"
                  size="small"
                  className={styles.fill}
                  input={{ className: styles.numberInput }}
                  min={0}
                  step="any"
                  aria-label={`Quantity, line ${index + 1}`}
                  value={line.quantity}
                  disabled={disabled}
                  onChange={(_, data) => onChange(line.key, { quantity: data.value })}
                />
              </TableCell>
              <TableCell className={styles.cell}>
                <Input
                  type="number"
                  size="small"
                  className={styles.fill}
                  input={{ className: styles.numberInput }}
                  min={0}
                  step="any"
                  aria-label={`Unit price, line ${index + 1}`}
                  value={line.unitPrice}
                  disabled={disabled}
                  onChange={(_, data) => onChange(line.key, { unitPrice: data.value })}
                />
              </TableCell>
              <TableCell className={styles.cell}>
                <Input
                  type="number"
                  size="small"
                  className={styles.fill}
                  input={{ className: styles.numberInput }}
                  min={0}
                  max={100}
                  step="any"
                  aria-label={`Line discount %, line ${index + 1}`}
                  value={line.discountPercent}
                  disabled={disabled}
                  onChange={(_, data) => onChange(line.key, { discountPercent: data.value })}
                />
              </TableCell>
              <TableCell className={mergeClasses(styles.cell, styles.end)}>{formatMoney(lineAmount(line), currency, 2)}</TableCell>
              <TableCell className={styles.cell}>
                <Button
                  appearance="subtle"
                  size="small"
                  icon={<Delete20Regular />}
                  aria-label={`Remove line ${index + 1}`}
                  title="Remove line"
                  disabled={disabled}
                  onClick={() => onRemove(line.key)}
                />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <div className={styles.draftTotal}>
        <span>Total Excl. Tax ({currency}), not saved yet</span>
        <span className={styles.draftTotalValue}>{formatMoney(total, currency, 2)}</span>
      </div>
    </div>
  );
}

interface ItemDropdownProps {
  items: Item[];
  currency: string;
  value: string;
  disabled: boolean;
  label: string;
  onPick: (itemNumber: string | undefined) => void;
}

/** Item picker for a new line; blocked items are listed but can't be chosen. */
function ItemDropdown({ items, currency, value, disabled, label, onPick }: ItemDropdownProps) {
  const styles = useStyles();
  return (
    <Dropdown
      size="small"
      className={styles.fill}
      placeholder="Choose an item"
      aria-label={label}
      value={value}
      selectedOptions={value ? [value] : []}
      disabled={disabled}
      onOptionSelect={(_, data) => onPick(data.optionValue)}
      data-testid="line-item"
    >
      {items.map((item) => (
        <Option key={item.number} value={item.number} text={`${item.number} ${item.displayName}`} disabled={item.blocked}>
          <span className={styles.option}>
            <span className={styles.optionNumber}>{item.number}</span>
            <span className={styles.optionName}>{item.displayName}</span>
            {item.blocked ? (
              <Badge appearance="tint" color="danger" size="small">
                Blocked
              </Badge>
            ) : (
              <span className={styles.optionPrice}>{formatMoney(item.unitPrice, currency)}</span>
            )}
          </span>
        </Option>
      ))}
    </Dropdown>
  );
}
