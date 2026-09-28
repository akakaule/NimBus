// The presenter's machine may run any locale, so every format names en-GB explicitly instead of
// relying on the browser default.

const EMPTY = '—';

const moneyFormats = new Map<string, Intl.NumberFormat>();

function moneyFormat(currencyCode: string): Intl.NumberFormat {
  let format = moneyFormats.get(currencyCode);
  if (!format) {
    try {
      format = new Intl.NumberFormat('en-GB', {
        style: 'currency',
        currency: currencyCode,
        minimumFractionDigits: 0,
        maximumFractionDigits: 0,
      });
    } catch {
      // An unknown currency code must not break a whole grid.
      format = moneyFormat('EUR');
    }
    moneyFormats.set(currencyCode, format);
  }
  return format;
}

/** €212,800 */
export function formatMoney(value: number | null | undefined, currencyCode?: string | null): string {
  if (value === null || value === undefined || Number.isNaN(value)) return EMPTY;
  return moneyFormat(currencyCode || 'EUR').format(value);
}

const numberFormat = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 2 });

/** 1,250 or 2.5 */
export function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return EMPTY;
  return numberFormat.format(value);
}

const dateFormat = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
const calendarDateFormat = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  timeZone: 'UTC',
});
const dateTimeFormat = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});
const dateTimeSecondsFormat = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
});

// A .NET DateTime without an offset (estimated close date, valid until) is a calendar date, not
// an instant: show it as-is instead of shifting it into the browser's time zone.
const CALENDAR_DATE = /^(\d{4})-(\d{2})-(\d{2})(?:T00:00:00(?:\.0+)?)?$/;

/** Parses an API timestamp. .NET sends 7 fractional digits; JavaScript only promises 3. */
export function parseTimestamp(value: string): Date {
  return new Date(value.replace(/(\.\d{3})\d+/, '$1'));
}

/** 28 Oct 2026 */
export function formatDate(value: string | null | undefined): string {
  if (!value) return EMPTY;
  const calendar = CALENDAR_DATE.exec(value);
  if (calendar) {
    return calendarDateFormat.format(new Date(Date.UTC(+calendar[1], +calendar[2] - 1, +calendar[3])));
  }
  const date = parseTimestamp(value);
  return Number.isNaN(date.getTime()) ? value : dateFormat.format(date);
}

/** 28 Oct 2026, 12:18 */
export function formatDateTime(value: string | null | undefined, withSeconds = false): string {
  if (!value) return EMPTY;
  const date = parseTimestamp(value);
  if (Number.isNaN(date.getTime())) return value;
  return (withSeconds ? dateTimeSecondsFormat : dateTimeFormat).format(date);
}

/** The yyyy-mm-dd value an <input type="date"> needs. */
export function toDateInputValue(value: string | null | undefined): string {
  return value && /^\d{4}-\d{2}-\d{2}/.test(value) ? value.slice(0, 10) : '';
}

/**
 * Reads an amount typed into a text input: "250000", "250000.50", "250,000" (commas grouping
 * thousands) or "250000,50" (a decimal comma). Blank is no amount; NaN when the text is not an
 * amount. String(n) of any stored amount reads back exactly.
 */
export function parseAmount(text: string): number | null {
  const compact = text.replace(/[\s€]/g, '');
  if (!compact) return null;
  const normalized = /^\d{1,3}(,\d{3})+(\.\d*)?$/.test(compact) ? compact.replace(/,/g, '') : compact.replace(',', '.');
  return /^\d+(\.\d*)?$/.test(normalized) ? Number(normalized) : Number.NaN;
}

/** A blank-safe display value. */
export function orDash(value: string | null | undefined): string {
  return value && value.trim() ? value : EMPTY;
}
