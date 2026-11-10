// Monetary values cross the API as fixed two-decimal strings (SPEC §9.1).
// The upstream database stores REAL (binary float), so all aggregation here is
// done in integer cents to avoid float drift; only the final value is formatted.

export type MoneyDto = { amount: string; currencyCode: string };

/** Convert a DB float to integer cents, rounding half away from zero. */
export function toCents(value: number): number {
  return Math.sign(value) * Math.round(Math.abs(value) * 100);
}

/** Format integer cents as a fixed two-decimal string, e.g. -123456 → "-1234.56". */
export function centsToAmount(cents: number): string {
  const sign = cents < 0 ? '-' : '';
  const abs = Math.abs(cents);
  const whole = Math.floor(abs / 100);
  const frac = String(abs % 100).padStart(2, '0');
  return `${sign}${whole}.${frac}`;
}

export function money(cents: number, currencyCode = 'USD'): MoneyDto {
  return { amount: centsToAmount(cents), currencyCode };
}

/** Ratio as a decimal string with 4 fractional digits, or null when undefined. */
export function ratio(deltaCents: number, baseCents: number): string | null {
  if (baseCents === 0) return null;
  return (deltaCents / Math.abs(baseCents)).toFixed(4);
}

/** Parse a user-supplied decimal string ("1234.56") into cents. Returns null when malformed. */
export function parseAmountToCents(input: string): number | null {
  if (typeof input !== 'string' || !/^-?\d{1,13}(\.\d{1,2})?$/.test(input.trim())) return null;
  const [wholeRaw, fracRaw = ''] = input.trim().split('.');
  const negative = wholeRaw.startsWith('-');
  const whole = Math.abs(parseInt(wholeRaw, 10));
  const frac = parseInt(fracRaw.padEnd(2, '0') || '0', 10);
  const cents = whole * 100 + frac;
  return negative ? -cents : cents;
}

/** Cents back to the float the upstream schema stores. */
export function centsToDbValue(cents: number): number {
  return cents / 100;
}
