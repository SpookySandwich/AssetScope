// One category vocabulary for every read surface. Rows arrive with two
// vocabularies mixed: Plaid's canonical UPPER_SNAKE and a legacy Title Case
// set from raw_category. Normalize so one real category never splits into two
// rows ("Services" vs "GENERAL_SERVICES") and a legacy "Transfer" is still
// recognized as money moving between the user's own accounts.

export const CATEGORY_ALIASES = new Map(Object.entries({
  'service': 'GENERAL_SERVICES',
  'services': 'GENERAL_SERVICES',
  'bills & utilities': 'RENT_AND_UTILITIES',
  'bills and utilities': 'RENT_AND_UTILITIES',
  'fees': 'BANK_FEES',
  'bank fees': 'BANK_FEES',
  'home': 'HOME_IMPROVEMENT',
  'uncategorized': 'OTHER',
  'food and drink': 'FOOD_AND_DRINK',
  'food & drink': 'FOOD_AND_DRINK',
  'merchandise': 'GENERAL_MERCHANDISE',
  'shops': 'GENERAL_MERCHANDISE',
  'loan payment': 'LOAN_PAYMENTS',
  'loan payments': 'LOAN_PAYMENTS',
  'transfer in': 'TRANSFER_IN',
  'transfer out': 'TRANSFER_OUT',
}));

export function normalizeCategory(category: string): string {
  const trimmed = category.trim();
  if (trimmed.toUpperCase() === trimmed && trimmed.includes('_')) return trimmed;
  const alias = CATEGORY_ALIASES.get(trimmed.toLowerCase());
  if (alias) return alias;
  if (trimmed.toUpperCase() === trimmed) return trimmed;
  return trimmed.toUpperCase().replace(/&/g, 'AND').replace(/[\s-]+/g, '_');
}

/**
 * Categories that are never "spending": money moving between the user's own
 * accounts, debt paydown, and income. Superset of the SQL pre-filter in
 * insights.ts, which cannot see the legacy undirected "Transfer".
 */
export const NON_SPENDING_CATEGORIES = new Set([
  'TRANSFER_IN', 'TRANSFER_OUT', 'TRANSFER', 'INCOME', 'LOAN_PAYMENTS',
]);

/**
 * Lower-cased raw spellings that normalize to `canonical`, for SQL
 * `LOWER(category) IN (...)` filters that must match legacy rows too.
 */
export function rawSpellingsFor(canonical: string): string[] {
  const target = normalizeCategory(canonical);
  const spellings = new Set<string>([target.toLowerCase()]);
  for (const [alias, value] of CATEGORY_ALIASES) {
    if (value === target) spellings.add(alias);
  }
  const words = target.toLowerCase().replace(/_and_/g, ' & ').replace(/_/g, ' ');
  spellings.add(words);
  spellings.add(words.replace(/ & /g, ' and '));
  return [...spellings];
}
