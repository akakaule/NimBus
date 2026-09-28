// The presenter's machine may run any locale, so every format names en-GB explicitly.
const LOCALE = 'en-GB';

const moneyFormats = new Map<string, Intl.NumberFormat>();

/** "€95,700" (grids and totals) or "€9,600.00" (quote lines, fractionDigits = 2). */
export function formatMoney(amount: number | null | undefined, currencyCode?: string | null, fractionDigits = 0): string {
  if (amount === null || amount === undefined || !Number.isFinite(amount)) return '';
  const currency = currencyCode || 'EUR';
  const key = `${currency}:${fractionDigits}`;
  let format = moneyFormats.get(key);
  if (!format) {
    format = new Intl.NumberFormat(LOCALE, {
      style: 'currency',
      currency,
      minimumFractionDigits: fractionDigits,
      maximumFractionDigits: fractionDigits,
    });
    moneyFormats.set(key, format);
  }
  return format.format(amount);
}

const numberFormat = new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 2 });

/** Quantities and percentages: "8", "2.5". */
export function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '';
  return numberFormat.format(value);
}

const dateFormat = new Intl.DateTimeFormat(LOCALE, { day: 'numeric', month: 'short', year: 'numeric' });
const dateTimeFormat = new Intl.DateTimeFormat(LOCALE, {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});
const timeFormat = new Intl.DateTimeFormat(LOCALE, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

function toDate(value: string | null | undefined): Date | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** "28 Oct 2026". Date-only API values (no offset) parse as local midnight, so the day is kept. */
export function formatDate(value: string | null | undefined): string {
  const date = toDate(value);
  return date ? dateFormat.format(date) : '';
}

/** "28 Oct 2026, 14:05". */
export function formatDateTime(value: string | null | undefined): string {
  const date = toDate(value);
  return date ? dateTimeFormat.format(date) : '';
}

/** "14:05:38". */
export function formatTime(value: string | null | undefined): string {
  const date = toDate(value);
  return date ? timeFormat.format(date) : '';
}

/** "14:05:38" today, otherwise "28 Oct 2026, 14:05". */
export function formatTimestamp(value: string | null | undefined): string {
  const date = toDate(value);
  if (!date) return '';
  return date.toDateString() === new Date().toDateString() ? timeFormat.format(date) : dateTimeFormat.format(date);
}
