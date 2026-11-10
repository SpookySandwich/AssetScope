export type AccountProvenanceFields = {
  item_id: string | null;
  source: string | null;
  subtype: string | null;
};

/**
 * The Plaid item relationship is the authoritative account provenance.
 * `accounts.source` is retained for compatibility and indexing, but it can be
 * absent on rows written by early builds before provenance became explicit.
 */
export function effectiveAccountSource(account: AccountProvenanceFields): string {
  if (account.item_id !== null && account.item_id.trim().length > 0) return 'plaid';
  if (account.subtype === 'manual' || account.source === 'manual') return 'manual';
  return account.source ?? 'imported';
}

/** Prefer account-specific metadata, then the institution on its Plaid item. */
export function effectiveInstitutionName(
  accountInstitution: string | null,
  itemInstitution: string | null,
): string | null {
  const accountName = accountInstitution?.trim();
  if (accountName) return accountName;
  const itemName = itemInstitution?.trim();
  return itemName || null;
}
