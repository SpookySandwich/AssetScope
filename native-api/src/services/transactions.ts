// Transactions read surface (MERC-5). The `transactions` table is populated
// by Plaid sync; this is the first service to expose it beyond insights.ts's
// single spending-increase rule. Category resolution and the transfer/income
// exclusion list intentionally mirror insights.ts:36-39 — do not diverge.
import { db } from '../core/database.js';
import { toCents, centsToAmount, parseAmountToCents } from './money.js';
import {
  normalizeCategory, rawSpellingsFor, NON_SPENDING_CATEGORIES as NON_SPENDING,
} from './categories.js';
import { accountDisplayNames } from './networth.js';

const CATEGORY_EXPR = "COALESCE(t.manual_category, t.category, t.raw_category, 'OTHER')";

// Categories insights.ts already excludes from "spending" in SQL: internal
// transfers and income aren't spend, and loan payments are tracked as debt
// paydown. Legacy spellings ("Transfer", "Loan Payment") only fold into these
// after normalizeCategory, so the JS pass below is the one that is complete.
const NON_SPENDING_CATEGORIES = ['TRANSFER_IN', 'TRANSFER_OUT', 'INCOME', 'LOAN_PAYMENTS'];

export type TransactionDto = {
  id: string;
  accountId: string;
  accountName: string;
  date: string;
  name: string;
  merchantName: string | null;
  amount: string;
  category: string;
  partySize: number | null;
  tripOrigin: string | null;
  tripDestination: string | null;
  tripKind: string | null;
};

type TransactionRow = {
  id: string;
  account_id: string;
  account_name: string;
  date: string;
  name: string;
  merchant_name: string | null;
  amount: number;
  category: string;
  party_size: number | null;
  trip_origin: string | null;
  trip_destination: string | null;
  trip_kind: string | null;
};

export async function listTransactions(opts: {
  from?: string;
  to?: string;
  accountId?: string;
  category?: string;
  limit?: number;
} = {}): Promise<{ transactions: TransactionDto[] }> {
  const conditions = ['t.pending = 0', 't.ignored = 0', 'a.archived_at IS NULL', 'a.excluded = 0'];
  const args: (string | number)[] = [];
  if (opts.from) { conditions.push('t.date >= ?'); args.push(opts.from); }
  if (opts.to) { conditions.push('t.date <= ?'); args.push(opts.to); }
  if (opts.accountId) { conditions.push('t.account_id = ?'); args.push(opts.accountId); }
  if (opts.category) {
    // Match every stored spelling of the requested category, not only the
    // canonical one, so a legacy "Services" row answers to GENERAL_SERVICES.
    const spellings = rawSpellingsFor(opts.category);
    conditions.push(`LOWER(${CATEGORY_EXPR}) IN (${spellings.map(() => '?').join(',')})`);
    args.push(...spellings);
  }
  const limit = Math.min(Math.max(Math.trunc(opts.limit ?? 100), 1), 1000);

  const res = await db.execute({
    sql: `SELECT t.id, t.account_id, a.name AS account_name, t.date, t.name, t.merchant_name, t.amount,
                 ${CATEGORY_EXPR} AS category, t.party_size, t.trip_origin, t.trip_destination, t.trip_kind
            FROM transactions t JOIN accounts a ON a.id = t.account_id
           WHERE ${conditions.join(' AND ')}
           ORDER BY t.date DESC, t.id DESC
           LIMIT ?`,
    args: [...args, limit],
  });

  const names = await accountDisplayNames();
  return {
    transactions: (res.rows as unknown as TransactionRow[]).map((row) => ({
      id: row.id,
      accountId: row.account_id,
      accountName: names.get(row.account_id) ?? row.account_name,
      date: row.date,
      name: row.name,
      merchantName: row.merchant_name,
      amount: centsToAmount(toCents(row.amount)),
      category: normalizeCategory(row.category),
      partySize: row.party_size,
      tripOrigin: row.trip_origin,
      tripDestination: row.trip_destination,
      tripKind: row.trip_kind,
    })),
  };
}

export type CategorySpendingDto = {
  category: string;
  amount: string;
  transactionCount: number;
};

export async function spendingByCategory(
  from: string,
  to: string,
  accountId?: string,
): Promise<{ from: string; to: string; categories: CategorySpendingDto[] }> {
  const res = await db.execute({
    sql: `SELECT ${CATEGORY_EXPR} AS category, t.amount
            FROM transactions t JOIN accounts a ON a.id = t.account_id
           WHERE t.date >= ? AND t.date <= ? AND t.pending = 0 AND t.ignored = 0 AND t.amount > 0
             AND ${CATEGORY_EXPR} NOT IN (${NON_SPENDING_CATEGORIES.map(() => '?').join(',')})
             AND a.archived_at IS NULL AND a.excluded = 0
             ${accountId ? 'AND t.account_id = ?' : ''}`,
    args: accountId
      ? [from, to, ...NON_SPENDING_CATEGORIES, accountId]
      : [from, to, ...NON_SPENDING_CATEGORIES],
  });

  const totals = new Map<string, { cents: number; count: number }>();
  for (const row of res.rows as unknown as { category: string; amount: number }[]) {
    const category = normalizeCategory(row.category);
    if (NON_SPENDING.has(category)) continue;
    const item = totals.get(category) ?? { cents: 0, count: 0 };
    item.cents += toCents(row.amount);
    item.count++;
    totals.set(category, item);
  }

  const categories = [...totals.entries()]
    .map(([category, { cents, count }]) => ({ category, cents, count }))
    .sort((a, b) => b.cents - a.cents || a.category.localeCompare(b.category))
    .map(({ category, cents, count }) => ({
      category, amount: centsToAmount(cents), transactionCount: count,
    }));

  return { from, to, categories };
}

export type MonthCashFlowDto = {
  month: string;
  moneyIn: string;
  moneyOut: string;
  net: string;
};

export type MonthlyCashFlowDto = {
  from: string;
  to: string;
  months: MonthCashFlowDto[];
  totals: { moneyIn: string; moneyOut: string; net: string };
};

// Money in/out reflects every non-pending, non-ignored transaction on
// included accounts, with no transfer/income exclusion — unlike
// spendingByCategory, cash flow is meant to capture all movement, not just
// discretionary spend. Positive `amount` is money leaving the account
// (matches the amount > 0 = spend convention insights.ts already relies on).
export async function monthlyCashFlow(
  from: string,
  to: string,
  accountId?: string,
): Promise<MonthlyCashFlowDto> {
  const res = await db.execute({
    sql: `SELECT substr(t.date, 1, 7) AS month, t.amount
            FROM transactions t JOIN accounts a ON a.id = t.account_id
           WHERE t.date >= ? AND t.date <= ? AND t.pending = 0 AND t.ignored = 0
             AND a.archived_at IS NULL AND a.excluded = 0
             ${accountId ? 'AND t.account_id = ?' : ''}`,
    args: accountId ? [from, to, accountId] : [from, to],
  });

  const byMonth = new Map<string, { inCents: number; outCents: number }>();
  let totalInCents = 0;
  let totalOutCents = 0;
  for (const row of res.rows as unknown as { month: string; amount: number }[]) {
    const item = byMonth.get(row.month) ?? { inCents: 0, outCents: 0 };
    const cents = toCents(row.amount);
    if (cents > 0) { item.outCents += cents; totalOutCents += cents; }
    else { item.inCents += -cents; totalInCents += -cents; }
    byMonth.set(row.month, item);
  }

  const months = [...byMonth.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([month, { inCents, outCents }]) => ({
      month,
      moneyIn: centsToAmount(inCents),
      moneyOut: centsToAmount(outCents),
      net: centsToAmount(inCents - outCents),
    }));

  return {
    from, to, months,
    totals: {
      moneyIn: centsToAmount(totalInCents),
      moneyOut: centsToAmount(totalOutCents),
      net: centsToAmount(totalInCents - totalOutCents),
    },
  };
}

export type TransactionsSummaryDto = {
  from: string;
  to: string;
  accountId: string | null;
  transactionCount: number;
  cashFlow: MonthlyCashFlowDto;
  spending: { total: string; categories: CategorySpendingDto[] };
};

// One call for the Transactions page header: cash flow over the window plus
// the category breakdown of discretionary spend. Both halves come from the
// two existing aggregates so the page never sums rows itself and cannot
// disagree with the CLI `spending` command.
export async function transactionsSummary(opts: {
  from: string;
  to: string;
  accountId?: string;
}): Promise<TransactionsSummaryDto> {
  const [cashFlow, byCategory, count] = await Promise.all([
    monthlyCashFlow(opts.from, opts.to, opts.accountId),
    spendingByCategory(opts.from, opts.to, opts.accountId),
    db.execute({
      sql: `SELECT COUNT(*) AS c
              FROM transactions t JOIN accounts a ON a.id = t.account_id
             WHERE t.date >= ? AND t.date <= ? AND t.pending = 0 AND t.ignored = 0
               AND a.archived_at IS NULL AND a.excluded = 0
               ${opts.accountId ? 'AND t.account_id = ?' : ''}`,
      args: opts.accountId ? [opts.from, opts.to, opts.accountId] : [opts.from, opts.to],
    }),
  ]);
  const totalCents = byCategory.categories.reduce((sum, c) => sum + (parseAmountToCents(c.amount) ?? 0), 0);
  return {
    from: opts.from,
    to: opts.to,
    accountId: opts.accountId ?? null,
    transactionCount: Number((count.rows[0] as unknown as { c: number }).c),
    cashFlow,
    spending: { total: centsToAmount(totalCents), categories: byCategory.categories },
  };
}
