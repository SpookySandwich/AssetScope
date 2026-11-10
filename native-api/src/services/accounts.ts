import { randomUUID } from 'node:crypto';
import { db } from '../core/database.js';
import { ApiError, notFound, validation } from '../errors.js';
import { parseAmountToCents, centsToDbValue, toCents, centsToAmount } from './money.js';
import { effectiveAccountSource, type AccountProvenanceFields } from './account-provenance.js';
import { netWorthContributionCents, type ClassifiableAccount } from './classify.js';

// Manual asset/liability CRUD + account metadata edits (SPEC §10.5–§10.7, §17, §18).

const MANUAL_CATEGORIES = new Set(['cash', 'property', 'vehicle', 'investment', 'loan', 'other']);

export type ManualAccountInput = {
  name: string;
  accountClass: 'asset' | 'liability';
  manualCategory: string;
  initialValue: { amount: string; currencyCode?: string; effectiveDate: string };
  includeInNetWorth?: boolean;
  notes?: string | null;
};

const todayLocal = (): string => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

function validateEffectiveDate(date: string): void {
  if (!DATE_RE.test(date)) throw validation('effectiveDate must be YYYY-MM-DD.', { field: 'effectiveDate' });
  if (date > todayLocal()) throw validation('effectiveDate must not be in the future.', { field: 'effectiveDate' });
}

type EditableAccountRow = AccountProvenanceFields & ClassifiableAccount & { id: string };

async function getAccountRow(accountId: string): Promise<EditableAccountRow> {
  const res = await db.execute({
    sql: `SELECT id, source, subtype, item_id, type, account_class, manual_category
          FROM accounts WHERE id = ?`,
    args: [accountId],
  });
  if (res.rows.length === 0) throw notFound('Account');
  return res.rows[0] as unknown as EditableAccountRow;
}

const isManual = (account: AccountProvenanceFields): boolean =>
  effectiveAccountSource(account) === 'manual';

export async function createManualAccount(input: ManualAccountInput): Promise<string> {
  const name = (input.name ?? '').trim();
  if (!name || name.length > 100) throw validation('Name must be 1–100 characters.', { field: 'name' });
  if (input.accountClass !== 'asset' && input.accountClass !== 'liability') {
    throw validation("accountClass must be 'asset' or 'liability'.", { field: 'accountClass' });
  }
  if (!MANUAL_CATEGORIES.has(input.manualCategory)) {
    throw validation('Unknown manual category.', { field: 'manualCategory' });
  }
  const currency = input.initialValue.currencyCode ?? 'USD';
  if (currency !== 'USD') throw validation('v1 supports USD manual values only.', { field: 'currencyCode' });
  const cents = parseAmountToCents(input.initialValue.amount);
  // Manual values are always non-negative; liabilities are entered as "amount owed" (SPEC §10.6).
  if (cents === null || cents < 0) throw validation('Amount must be a non-negative decimal string.', { field: 'amount' });
  validateEffectiveDate(input.initialValue.effectiveDate);

  const id = `manual-${randomUUID()}`;
  const now = new Date().toISOString();
  await db.batch([
    {
      sql: `INSERT INTO accounts (id, name, type, subtype, source, account_class, manual_category,
              currency_code, excluded, notes, created_at, updated_at)
            VALUES (?, ?, 'other', 'manual', 'manual', ?, ?, 'USD', ?, ?, ?, ?)`,
      args: [id, name, input.accountClass, input.manualCategory,
             input.includeInNetWorth === false ? 1 : 0, input.notes ?? null, now, now],
    },
    {
      sql: `INSERT INTO balance_history (account_id, balance, date) VALUES (?, ?, ?)
            ON CONFLICT(account_id, date) DO UPDATE SET balance = excluded.balance`,
      args: [id, centsToDbValue(cents), input.initialValue.effectiveDate],
    },
  ], 'write');
  return id;
}

export async function updateManualValue(
  accountId: string,
  input: { amount: string; effectiveDate?: string; note?: string },
): Promise<void> {
  const account = await getAccountRow(accountId);
  if (!isManual(account)) {
    throw new ApiError('CONFLICT', 'Only manual accounts accept value updates. Plaid balances come from sync.');
  }
  const cents = parseAmountToCents(input.amount);
  if (cents === null || cents < 0) throw validation('Amount must be a non-negative decimal string.', { field: 'amount' });
  const effectiveDate = input.effectiveDate ?? todayLocal();
  validateEffectiveDate(effectiveDate);

  await db.batch([
    {
      sql: `INSERT INTO balance_history (account_id, balance, date) VALUES (?, ?, ?)
            ON CONFLICT(account_id, date) DO UPDATE SET balance = excluded.balance`,
      args: [accountId, centsToDbValue(cents), effectiveDate],
    },
    { sql: `UPDATE accounts SET updated_at = ? WHERE id = ?`, args: [new Date().toISOString(), accountId] },
  ], 'write');
}

export type AccountPatch = {
  nickname?: string | null;
  owner?: string | null;
  excluded?: boolean;
  // Manual accounts only:
  name?: string;
  manualCategory?: string;
  notes?: string | null;
};

const PATCHABLE = new Set(['nickname', 'owner', 'excluded', 'name', 'manualCategory', 'notes']);

export async function patchAccount(accountId: string, patch: AccountPatch): Promise<void> {
  for (const key of Object.keys(patch)) {
    if (!PATCHABLE.has(key)) throw validation(`Unknown field: ${key}`, { field: key });
  }
  const account = await getAccountRow(accountId);

  const sets: string[] = [];
  const args: (string | number | null)[] = [];

  if ('nickname' in patch) {
    const v = patch.nickname === null ? null : String(patch.nickname).trim() || null;
    if (v !== null && v.length > 100) throw validation('Nickname too long.', { field: 'nickname' });
    sets.push('nickname = ?'); args.push(v);
  }
  if ('owner' in patch) {
    const v = patch.owner === null ? null : String(patch.owner).trim() || null;
    if (v !== null && v.length > 100) throw validation('Owner too long.', { field: 'owner' });
    sets.push('owner = ?'); args.push(v);
  }
  if ('excluded' in patch) {
    if (typeof patch.excluded !== 'boolean') throw validation('excluded must be boolean.', { field: 'excluded' });
    sets.push('excluded = ?'); args.push(patch.excluded ? 1 : 0);
  }
  if ('name' in patch || 'manualCategory' in patch || 'notes' in patch) {
    // Institution-reported identity is immutable for Plaid accounts (SPEC §10.5).
    if (!isManual(account)) throw validation('Only manual accounts allow editing name/category/notes.');
    if ('name' in patch) {
      const v = String(patch.name ?? '').trim();
      if (!v || v.length > 100) throw validation('Name must be 1–100 characters.', { field: 'name' });
      sets.push('name = ?'); args.push(v);
    }
    if ('manualCategory' in patch) {
      if (!MANUAL_CATEGORIES.has(String(patch.manualCategory))) throw validation('Unknown manual category.', { field: 'manualCategory' });
      sets.push('manual_category = ?'); args.push(String(patch.manualCategory));
    }
    if ('notes' in patch) {
      sets.push('notes = ?'); args.push(patch.notes === null ? null : String(patch.notes).slice(0, 2000));
    }
  }

  if (sets.length === 0) throw validation('No editable fields supplied.');
  sets.push('updated_at = ?'); args.push(new Date().toISOString());
  args.push(accountId);
  await db.execute({ sql: `UPDATE accounts SET ${sets.join(', ')} WHERE id = ?`, args });
}

export async function setArchived(accountId: string, archived: boolean): Promise<void> {
  await getAccountRow(accountId);
  await db.execute({
    sql: 'UPDATE accounts SET archived_at = ?, updated_at = ? WHERE id = ?',
    args: [archived ? new Date().toISOString() : null, new Date().toISOString(), accountId],
  });
}

export type DeletePreview = { historyCount: number };

export async function deleteManualAccount(accountId: string): Promise<DeletePreview> {
  const account = await getAccountRow(accountId);
  if (!isManual(account)) {
    throw new ApiError('CONFLICT', 'Only manual accounts can be deleted. Plaid accounts can be excluded or archived.');
  }
  const countRes = await db.execute({
    sql: 'SELECT COUNT(*) AS c FROM balance_history WHERE account_id = ?',
    args: [accountId],
  });
  const historyCount = Number((countRes.rows[0] as unknown as { c: number }).c);
  await db.batch([
    { sql: 'DELETE FROM balance_history WHERE account_id = ?', args: [accountId] },
    { sql: 'DELETE FROM accounts WHERE id = ?', args: [accountId] },
  ], 'write');
  return { historyCount };
}

export type BalanceSnapshotDto = { date: string; amount: string };

export async function recentSnapshots(accountId: string, limit = 10): Promise<BalanceSnapshotDto[]> {
  const account = await getAccountRow(accountId);
  const res = await db.execute({
    sql: 'SELECT date, balance FROM balance_history WHERE account_id = ? ORDER BY date DESC LIMIT ?',
    args: [accountId, limit],
  });
  return (res.rows as unknown as { date: string; balance: number }[]).map((r) => ({
    date: r.date,
    amount: centsToAmount(netWorthContributionCents(account, toCents(Number(r.balance)))),
  }));
}
