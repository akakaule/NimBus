import { useEffect, useRef, useState } from 'react';
import {
  Button,
  Input,
  makeStyles,
  mergeClasses,
  Select,
  TableCell,
  TableRow,
  tokens,
  useId,
} from '@fluentui/react-components';
import { Add20Regular, BoxMultiple20Regular, Delete20Regular, Edit20Regular, Save20Regular } from '@fluentui/react-icons';
import { api, errorMessage, type Opportunity, type OpportunityLine } from '../../api';
import { EmptyState } from '../../components/PageStates';
import { RecordGrid, type GridColumn } from '../../components/RecordGrid';
import { Section } from '../../components/ui';
import { formatMoney, formatNumber } from '../../format';
import { useNotify } from '../../hooks/useNotify';
import { usePolling } from '../../hooks/usePolling';
import { isOpen } from '../../model';

const useStyles = makeStyles({
  totalRow: {
    ':hover': { backgroundColor: 'transparent' },
  },
  totalCell: {
    fontWeight: tokens.fontWeightSemibold,
    borderTop: `1px solid ${tokens.colorNeutralStroke1}`,
  },
  totalAmount: {
    textAlign: 'end',
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
  quantity: {
    width: '96px',
  },
  addRow: {
    display: 'flex',
    alignItems: 'flex-end',
    flexWrap: 'wrap',
    gap: '8px',
    marginTop: '12px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  addField: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  product: {
    minWidth: '320px',
  },
  note: {
    marginTop: '12px',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  invalid: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

interface DraftLine {
  key: string;
  productNumber: string;
  description: string;
  pricePerUnit: number;
  quantity: string;
}

/** Accepts "2", "2.5" and, with a decimal comma, "2,5". */
const parseQuantity = (value: string): number => {
  const n = Number(value.trim().replace(',', '.'));
  return value.trim() && Number.isFinite(n) ? n : NaN;
};
const validQuantity = (value: string) => parseQuantity(value) > 0;
const draftAmount = (line: DraftLine) => (validQuantity(line.quantity) ? parseQuantity(line.quantity) * line.pricePerUnit : 0);

const viewColumns: GridColumn<OpportunityLine>[] = [
  { id: 'product', header: 'Product', width: '130px', render: (line) => line.productNumber },
  { id: 'description', header: 'Description', render: (line) => line.description },
  { id: 'quantity', header: 'Quantity', numeric: true, width: '100px', render: (line) => formatNumber(line.quantity) },
  { id: 'price', header: 'Unit price', numeric: true, width: '130px', render: (line) => formatMoney(line.pricePerUnit) },
  { id: 'amount', header: 'Amount', numeric: true, width: '140px', render: (line) => formatMoney(line.extendedAmount) },
];

interface LinesTabProps {
  opportunity: Opportunity;
  refresh: () => Promise<void>;
}

export function LinesTab({ opportunity, refresh }: LinesTabProps) {
  const styles = useStyles();
  const notify = useNotify();
  const productSelectId = useId('product');
  const quantityId = useId('quantity');
  const editable = isOpen(opportunity);
  const products = usePolling('products', (signal) => api.products(signal));
  const [draft, setDraft] = useState<DraftLine[] | null>(null);
  const [newProduct, setNewProduct] = useState('');
  const [newQuantity, setNewQuantity] = useState('1');
  const [saving, setSaving] = useState(false);
  const nextKey = useRef(0);

  useEffect(() => {
    if (!editable) setDraft(null);
  }, [editable]);

  const startEditing = () => {
    setDraft(
      opportunity.lines.map((line) => ({
        key: line.opportunityProductId,
        productNumber: line.productNumber,
        description: line.description,
        pricePerUnit: line.pricePerUnit,
        quantity: String(line.quantity),
      })),
    );
    setNewProduct('');
    setNewQuantity('1');
  };

  const addLine = () => {
    const product = products.data?.find((p) => p.productNumber === newProduct);
    if (!draft || !product || !validQuantity(newQuantity)) return;
    nextKey.current += 1;
    setDraft([
      ...draft,
      {
        key: `new-${nextKey.current}`,
        productNumber: product.productNumber,
        description: product.name,
        pricePerUnit: product.price,
        quantity: newQuantity,
      },
    ]);
    setNewProduct('');
    setNewQuantity('1');
  };

  const setQuantity = (key: string, quantity: string) =>
    setDraft((lines) => lines?.map((line) => (line.key === key ? { ...line, quantity } : line)) ?? null);
  const removeLine = (key: string) => setDraft((lines) => lines?.filter((line) => line.key !== key) ?? null);

  const invalid = draft?.some((line) => !validQuantity(line.quantity)) ?? false;
  // Every save bumps the lines revision (and so makes the next quote request a new one): only
  // offer it when something changed.
  const changed =
    draft !== null &&
    (draft.length !== opportunity.lines.length ||
      draft.some(
        (line, index) =>
          line.productNumber !== opportunity.lines[index].productNumber ||
          parseQuantity(line.quantity) !== opportunity.lines[index].quantity,
      ));

  const save = async () => {
    if (!draft) return;
    setSaving(true);
    try {
      const result = await api.setLines(
        opportunity.opportunityId,
        draft.map((line) => ({ productNumber: line.productNumber, quantity: parseQuantity(line.quantity) })),
      );
      await refresh();
      setDraft(null);
      notify.success(
        'Product lines saved',
        opportunity.csQuoteRequestedOn
          ? `Revision ${result.csLinesRevision}. Request the quote again so Business Central quotes the new lines.`
          : `Revision ${result.csLinesRevision}. Estimated revenue ${formatMoney(result.estimatedValue)}.`,
      );
    } catch (error) {
      notify.error('The product lines were not saved', errorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  const editColumns: GridColumn<DraftLine>[] = [
    { id: 'product', header: 'Product', width: '130px', render: (line) => line.productNumber },
    { id: 'description', header: 'Description', render: (line) => line.description },
    {
      id: 'quantity',
      header: 'Quantity',
      width: '120px',
      interactive: true,
      render: (line) => (
        <Input
          className={styles.quantity}
          size="small"
          inputMode="decimal"
          value={line.quantity}
          aria-label={`Quantity of ${line.productNumber}`}
          aria-invalid={!validQuantity(line.quantity)}
          onChange={(_, data) => setQuantity(line.key, data.value)}
        />
      ),
    },
    { id: 'price', header: 'Unit price', numeric: true, width: '130px', render: (line) => formatMoney(line.pricePerUnit) },
    { id: 'amount', header: 'Amount', numeric: true, width: '140px', render: (line) => formatMoney(draftAmount(line)) },
    {
      id: 'remove',
      header: '',
      width: '48px',
      interactive: true,
      render: (line) => (
        <Button
          appearance="subtle"
          size="small"
          icon={<Delete20Regular />}
          aria-label={`Remove ${line.productNumber}`}
          onClick={() => removeLine(line.key)}
        />
      ),
    },
  ];

  const total = draft
    ? draft.reduce((sum, line) => sum + draftAmount(line), 0)
    : opportunity.lines.reduce((sum, line) => sum + line.extendedAmount, 0);
  // Product, description, quantity and unit price; then the amount column (and the remove column).
  const totalRow = (
    <TableRow className={styles.totalRow}>
      <TableCell className={styles.totalCell} colSpan={4}>
        Total
      </TableCell>
      <TableCell className={mergeClasses(styles.totalCell, styles.totalAmount)} data-testid="lines-total">
        {formatMoney(total)}
      </TableCell>
      {draft && <TableCell className={styles.totalCell} />}
    </TableRow>
  );

  const empty = (
    <EmptyState icon={<BoxMultiple20Regular />} title="No product lines">
      {editable ? 'Choose Edit lines to add products.' : undefined}
    </EmptyState>
  );

  const actions = draft ? (
    <>
      <Button
        appearance="primary"
        icon={<Save20Regular />}
        disabled={!changed || invalid || saving}
        onClick={() => void save()}
        data-testid="save-lines"
      >
        {saving ? 'Saving…' : 'Save lines'}
      </Button>
      <Button disabled={saving} onClick={() => setDraft(null)}>
        Cancel
      </Button>
    </>
  ) : (
    <Button icon={<Edit20Regular />} disabled={!editable} onClick={startEditing} data-testid="edit-lines">
      Edit lines
    </Button>
  );

  return (
    <Section
      title="Product lines"
      caption={editable ? `Revision ${opportunity.csLinesRevision}` : 'Closed — read-only'}
      actions={actions}
      testId="product-lines"
    >
      {draft ? (
        <>
          <RecordGrid
            ariaLabel="Product lines (editing)"
            columns={editColumns}
            rows={draft}
            rowKey={(line) => line.key}
            empty={empty}
            footer={draft.length > 0 ? totalRow : undefined}
          />
          {invalid && <span className={styles.invalid}>Quantities must be numbers greater than zero.</span>}
          <div className={styles.addRow}>
            <label className={styles.addField} htmlFor={productSelectId}>
              Product
              <Select
                id={productSelectId}
                className={styles.product}
                value={newProduct}
                disabled={!products.data}
                onChange={(_, data) => setNewProduct(data.value)}
              >
                <option value="">{products.data ? 'Choose a product…' : products.error ? 'Products unavailable' : 'Loading products…'}</option>
                {products.data?.map((product) => (
                  <option key={product.productId} value={product.productNumber}>
                    {product.productNumber} · {product.name} · {formatMoney(product.price)}
                  </option>
                ))}
              </Select>
            </label>
            <label className={styles.addField} htmlFor={quantityId}>
              Quantity
              <Input
                id={quantityId}
                className={styles.quantity}
                inputMode="decimal"
                value={newQuantity}
                onChange={(_, data) => setNewQuantity(data.value)}
              />
            </label>
            <Button
              icon={<Add20Regular />}
              disabled={!newProduct || !validQuantity(newQuantity)}
              onClick={addLine}
              data-testid="add-line"
            >
              Add line
            </Button>
          </div>
        </>
      ) : (
        <RecordGrid
          ariaLabel="Product lines"
          columns={viewColumns}
          rows={opportunity.lines}
          rowKey={(line) => line.opportunityProductId}
          empty={empty}
          footer={opportunity.lines.length > 0 ? totalRow : undefined}
        />
      )}
      <span className={styles.note}>
        Prices shown are CRM list prices; the binding price is on the Business Central quote.
      </span>
    </Section>
  );
}
