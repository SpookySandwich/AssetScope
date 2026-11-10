// Central account classification (SPEC §9.3). Every surface — dashboard,
// accounts, allocation, history — must go through this single mapping so the
// retirement-subtype list and the loan fix live in exactly one place.

export type AccountClass = 'asset' | 'liability';

export type Category =
  | 'bank'
  | 'investment'
  | 'retirement'
  | 'credit'
  | 'loan'
  | 'cash'
  | 'property'
  | 'vehicle'
  | 'other';

export type ClassifiableAccount = {
  type: string;
  subtype: string | null;
  source: string | null;           // 'plaid' | 'manual' | 'imported' (v1 schema extension)
  account_class: string | null;    // persisted override from the v1 schema extension
  manual_category: string | null;  // 'cash' | 'property' | 'vehicle' | 'investment' | 'loan' | 'other'
};

// Plaid investment subtypes that represent retirement vehicles (US-focused v1 set).
const RETIREMENT_SUBTYPES = new Set([
  '401a', '401k', '403b', '457b', 'ira', 'roth', 'roth 401k', 'sep ira',
  'simple ira', 'sarsep', 'pension', 'retirement', 'keogh', 'profit sharing plan',
  'thrift savings plan', 'hsa', // HSA treated as long-horizon savings; revisit if users object
]);

export function classify(a: ClassifiableAccount): { category: Category; accountClass: AccountClass } {
  // Manual accounts classify by their explicit category + class first.
  if (a.source === 'manual' || a.subtype === 'manual') {
    const cls: AccountClass = a.account_class === 'liability' ? 'liability' : 'asset';
    switch (a.manual_category) {
      case 'property': return { category: 'property', accountClass: cls };
      case 'vehicle': return { category: 'vehicle', accountClass: cls };
      case 'investment': return { category: 'investment', accountClass: cls };
      case 'loan': return { category: 'loan', accountClass: 'liability' };
      case 'cash': return { category: 'cash', accountClass: cls };
      default: return { category: cls === 'liability' ? 'loan' : 'other', accountClass: cls };
    }
  }

  switch (a.type) {
    case 'depository':
      return { category: 'bank', accountClass: 'asset' };
    case 'investment':
      return RETIREMENT_SUBTYPES.has((a.subtype ?? '').toLowerCase())
        ? { category: 'retirement', accountClass: 'asset' }
        : { category: 'investment', accountClass: 'asset' };
    case 'credit':
      return { category: 'credit', accountClass: 'liability' };
    case 'loan':
      // Upstream gap fix: loans are liabilities, not ignored (SPEC §2.2.2).
      return { category: 'loan', accountClass: 'liability' };
    default:
      // 'other' and unknown types: honor a persisted class, else treat as asset.
      return {
        category: 'other',
        accountClass: a.account_class === 'liability' ? 'liability' : 'asset',
      };
  }
}

/**
 * Convert a stored/Plaid balance to its signed contribution to net worth.
 * Assets contribute positively. Plaid reports credit/loan balances as the
 * amount owed (positive), so liabilities contribute negatively; a negative
 * Plaid liability is an overpayment/credit and therefore contributes
 * positively. Manual liabilities are always entered as a positive amount owed.
 */
export function netWorthContributionCents(a: ClassifiableAccount, cents: number): number {
  if (classify(a).accountClass === 'asset') return cents;
  const amountOwed = a.source === 'manual' || a.subtype === 'manual'
    ? Math.abs(cents)
    : cents;
  return -amountOwed;
}

// Display categories used by the Assets page / allocation donut (SPEC §9.8).
// Assets only — liabilities are surfaced separately.
export type AllocationBucket =
  | 'bankCash'
  | 'investment'
  | 'retirement'
  | 'property'
  | 'vehicle'
  | 'other';

export function allocationBucket(category: Category): AllocationBucket {
  switch (category) {
    case 'bank':
    case 'cash':
      return 'bankCash';
    case 'investment': return 'investment';
    case 'retirement': return 'retirement';
    case 'property': return 'property';
    case 'vehicle': return 'vehicle';
    default: return 'other';
  }
}
