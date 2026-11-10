import { describe, it, expect, beforeEach, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});

import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema } from '../../native-api/src/services/schema.js';
import {
  listTransactions, spendingByCategory, monthlyCashFlow, transactionsSummary,
} from '../../native-api/src/services/transactions.js';

async function clean() {
  for (const t of ['transactions', 'accounts']) {
    await db.execute(`DELETE FROM ${t}`).catch(() => {});
  }
}

async function addAccount(id: string, over: Record<string, string | number | null> = {}) {
  await db.execute({
    sql: `INSERT INTO accounts (id, name, type, source, excluded, archived_at)
          VALUES (?, ?, 'depository', 'plaid', ?, ?)`,
    args: [id, over.name ?? id, over.excluded ?? 0, over.archived_at ?? null],
  });
}

type TxnOverrides = {
  merchant_name?: string | null;
  category?: string | null;
  raw_category?: string | null;
  manual_category?: string | null;
  pending?: number;
  ignored?: number;
};

async function addTxn(id: string, accountId: string, date: string, name: string, amount: number, over: TxnOverrides = {}) {
  await db.execute({
    sql: `INSERT INTO transactions
            (id, account_id, date, name, merchant_name, amount, category, raw_category, manual_category, pending, ignored)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    args: [
      id, accountId, date, name,
      over.merchant_name ?? null,
      amount,
      over.category ?? null,
      over.raw_category ?? null,
      over.manual_category ?? null,
      over.pending ?? 0,
      over.ignored ?? 0,
    ],
  });
}

beforeEach(async () => {
  await ensureNativeSchema();
  await clean();
});

describe('listTransactions', () => {
  it('returns newest first and joins the account name', async () => {
    await addAccount('chk', { name: 'Everyday Checking' });
    await addTxn('t1', 'chk', '2026-01-01', 'Coffee', 5, { category: 'FOOD_AND_DRINK' });
    await addTxn('t2', 'chk', '2026-01-03', 'Groceries', 60, { category: 'FOOD_AND_DRINK' });
    await addTxn('t3', 'chk', '2026-01-02', 'Gas', 40, { category: 'TRANSPORTATION' });

    const { transactions } = await listTransactions();
    expect(transactions.map((t) => t.id)).toEqual(['t2', 't3', 't1']);
    expect(transactions[0].accountName).toBe('Everyday Checking');
    expect(transactions[0].amount).toBe('60.00');
  });

  it('excludes pending and ignored transactions', async () => {
    await addAccount('chk');
    await addTxn('t1', 'chk', '2026-01-01', 'Pending charge', 20, { pending: 1 });
    await addTxn('t2', 'chk', '2026-01-01', 'Ignored charge', 30, { ignored: 1 });
    await addTxn('t3', 'chk', '2026-01-01', 'Real charge', 10);

    const { transactions } = await listTransactions();
    expect(transactions.map((t) => t.id)).toEqual(['t3']);
  });

  it('excludes transactions on archived or excluded accounts', async () => {
    await addAccount('archived', { archived_at: '2026-01-01T00:00:00Z' });
    await addAccount('excluded', { excluded: 1 });
    await addAccount('visible');
    await addTxn('t1', 'archived', '2026-01-01', 'Old', 10);
    await addTxn('t2', 'excluded', '2026-01-01', 'Hidden', 10);
    await addTxn('t3', 'visible', '2026-01-01', 'Kept', 10);

    const { transactions } = await listTransactions();
    expect(transactions.map((t) => t.id)).toEqual(['t3']);
  });

  it('filters by from/to, accountId, category, and limit', async () => {
    await addAccount('chk');
    await addAccount('credit');
    await addTxn('t1', 'chk', '2026-01-01', 'A', 10, { category: 'FOOD_AND_DRINK' });
    await addTxn('t2', 'chk', '2026-02-01', 'B', 20, { category: 'TRANSPORTATION' });
    await addTxn('t3', 'credit', '2026-01-15', 'C', 30, { category: 'FOOD_AND_DRINK' });

    expect((await listTransactions({ from: '2026-01-10' })).transactions.map((t) => t.id)).toEqual(['t2', 't3']);
    expect((await listTransactions({ to: '2026-01-10' })).transactions.map((t) => t.id)).toEqual(['t1']);
    expect((await listTransactions({ accountId: 'credit' })).transactions.map((t) => t.id)).toEqual(['t3']);
    expect((await listTransactions({ category: 'FOOD_AND_DRINK' })).transactions.map((t) => t.id)).toEqual(['t3', 't1']);
    expect((await listTransactions({ limit: 1 })).transactions).toHaveLength(1);
  });
});

describe('category normalization', () => {
  it('folds legacy spellings into the canonical vocabulary and keeps them out of spending', async () => {
    await addAccount('chk');
    await addTxn('t1', 'chk', '2026-01-02', 'Legacy services', 200, { raw_category: 'Services' });
    await addTxn('t2', 'chk', '2026-01-03', 'Plaid services', 212, { category: 'GENERAL_SERVICES' });
    await addTxn('t3', 'chk', '2026-01-04', 'Legacy transfer', 950, { raw_category: 'Transfer' });
    await addTxn('t4', 'chk', '2026-01-05', 'Legacy loan', 43.2, { raw_category: 'Loan Payment' });

    const { transactions } = await listTransactions();
    expect(transactions.map((t) => t.category))
      .toEqual(['LOAN_PAYMENTS', 'TRANSFER', 'GENERAL_SERVICES', 'GENERAL_SERVICES']);
    expect((await listTransactions({ category: 'GENERAL_SERVICES' })).transactions.map((t) => t.id))
      .toEqual(['t2', 't1']);

    const { categories } = await spendingByCategory('2026-01-01', '2026-01-31');
    expect(categories).toEqual([{ category: 'GENERAL_SERVICES', amount: '412.00', transactionCount: 2 }]);
  });
});

describe('spendingByCategory', () => {
  it('resolves category via manual_category > category > raw_category > OTHER', async () => {
    await addAccount('chk');
    await addTxn('t1', 'chk', '2026-01-01', 'A', 10, { manual_category: 'DINING', category: 'FOOD_AND_DRINK' });
    await addTxn('t2', 'chk', '2026-01-01', 'B', 20, { category: 'TRANSPORTATION', raw_category: 'GAS' });
    await addTxn('t3', 'chk', '2026-01-01', 'C', 30, { raw_category: 'MISC' });
    await addTxn('t4', 'chk', '2026-01-01', 'D', 40);

    const { categories } = await spendingByCategory('2026-01-01', '2026-01-31');
    const byCategory = Object.fromEntries(categories.map((c) => [c.category, c.amount]));
    expect(byCategory.DINING).toBe('10.00');
    expect(byCategory.TRANSPORTATION).toBe('20.00');
    expect(byCategory.MISC).toBe('30.00');
    expect(byCategory.OTHER).toBe('40.00');
  });

  it('excludes transfers, income, and loan payments from spending totals', async () => {
    await addAccount('chk');
    await addTxn('t1', 'chk', '2026-01-01', 'Transfer in', 500, { category: 'TRANSFER_IN' });
    await addTxn('t2', 'chk', '2026-01-01', 'Transfer out', 500, { category: 'TRANSFER_OUT' });
    await addTxn('t3', 'chk', '2026-01-01', 'Paycheck', 2000, { category: 'INCOME' });
    await addTxn('t4', 'chk', '2026-01-01', 'Loan payment', 300, { category: 'LOAN_PAYMENTS' });
    await addTxn('t5', 'chk', '2026-01-01', 'Groceries', 60, { category: 'FOOD_AND_DRINK' });

    const { categories } = await spendingByCategory('2026-01-01', '2026-01-31');
    expect(categories).toEqual([{ category: 'FOOD_AND_DRINK', amount: '60.00', transactionCount: 1 }]);
  });

  it('only counts positive amounts (spend), not negative (refunds/credits)', async () => {
    await addAccount('chk');
    await addTxn('t1', 'chk', '2026-01-01', 'Purchase', 50, { category: 'SHOPPING' });
    await addTxn('t2', 'chk', '2026-01-02', 'Refund', -20, { category: 'SHOPPING' });

    const { categories } = await spendingByCategory('2026-01-01', '2026-01-31');
    expect(categories).toEqual([{ category: 'SHOPPING', amount: '50.00', transactionCount: 1 }]);
  });

  it('excludes pending, ignored, and archived/excluded-account transactions', async () => {
    await addAccount('chk');
    await addAccount('archived', { archived_at: '2026-01-01T00:00:00Z' });
    await addTxn('t1', 'chk', '2026-01-01', 'Pending', 50, { category: 'SHOPPING', pending: 1 });
    await addTxn('t2', 'chk', '2026-01-01', 'Ignored', 50, { category: 'SHOPPING', ignored: 1 });
    await addTxn('t3', 'archived', '2026-01-01', 'Old', 50, { category: 'SHOPPING' });

    const { categories } = await spendingByCategory('2026-01-01', '2026-01-31');
    expect(categories).toEqual([]);
  });
});

describe('monthlyCashFlow', () => {
  it('buckets by calendar month and separates money in from money out', async () => {
    await addAccount('chk');
    await addTxn('t1', 'chk', '2026-01-05', 'Purchase', 100); // out
    await addTxn('t2', 'chk', '2026-01-20', 'Refund', -30); // in
    await addTxn('t3', 'chk', '2026-02-10', 'Paycheck', -2000, { category: 'INCOME' }); // in

    const { months, totals } = await monthlyCashFlow('2026-01-01', '2026-02-28');
    expect(months).toEqual([
      { month: '2026-01', moneyIn: '30.00', moneyOut: '100.00', net: '-70.00' },
      { month: '2026-02', moneyIn: '2000.00', moneyOut: '0.00', net: '2000.00' },
    ]);
    expect(totals).toEqual({ moneyIn: '2030.00', moneyOut: '100.00', net: '1930.00' });
  });

  it('does not exclude transfers or income from cash flow (unlike spendingByCategory)', async () => {
    await addAccount('chk');
    await addTxn('t1', 'chk', '2026-01-05', 'Transfer out', 500, { category: 'TRANSFER_OUT' });

    const { totals } = await monthlyCashFlow('2026-01-01', '2026-01-31');
    expect(totals.moneyOut).toBe('500.00');
  });

  it('excludes pending, ignored, and archived/excluded-account transactions', async () => {
    await addAccount('chk');
    await addAccount('excluded', { excluded: 1 });
    await addTxn('t1', 'chk', '2026-01-05', 'Pending', 100, { pending: 1 });
    await addTxn('t2', 'chk', '2026-01-05', 'Ignored', 100, { ignored: 1 });
    await addTxn('t3', 'excluded', '2026-01-05', 'Excluded account', 100);

    const { months, totals } = await monthlyCashFlow('2026-01-01', '2026-01-31');
    expect(months).toEqual([]);
    expect(totals).toEqual({ moneyIn: '0.00', moneyOut: '0.00', net: '0.00' });
  });

  it('scopes to one account when asked', async () => {
    await addAccount('chk');
    await addAccount('credit');
    await addTxn('t1', 'chk', '2026-01-05', 'Purchase', 100);
    await addTxn('t2', 'credit', '2026-01-06', 'Card purchase', 40);

    const { totals } = await monthlyCashFlow('2026-01-01', '2026-01-31', 'credit');
    expect(totals).toEqual({ moneyIn: '0.00', moneyOut: '40.00', net: '-40.00' });
  });
});

describe('transactionsSummary', () => {
  it('combines cash flow, category spend, and the row count for the window', async () => {
    await addAccount('chk');
    await addAccount('credit');
    await addTxn('t1', 'chk', '2026-01-02', 'Paycheck', -2000, { category: 'INCOME' });
    await addTxn('t2', 'chk', '2026-01-05', 'Rent', 1500, { category: 'RENT_AND_UTILITIES' });
    await addTxn('t3', 'credit', '2026-01-06', 'Dinner', 60, { category: 'FOOD_AND_DRINK' });
    await addTxn('t4', 'chk', '2026-01-07', 'To brokerage', 500, { category: 'TRANSFER_OUT' });
    await addTxn('t5', 'chk', '2026-02-01', 'Next month', 10, { category: 'FOOD_AND_DRINK' });

    const summary = await transactionsSummary({ from: '2026-01-01', to: '2026-01-31' });
    expect(summary.transactionCount).toBe(4);
    expect(summary.cashFlow.totals).toEqual({ moneyIn: '2000.00', moneyOut: '2060.00', net: '-60.00' });
    // Spending excludes the transfer; cash flow includes it.
    expect(summary.spending.total).toBe('1560.00');
    expect(summary.spending.categories.map((c) => c.category)).toEqual(['RENT_AND_UTILITIES', 'FOOD_AND_DRINK']);

    const scoped = await transactionsSummary({ from: '2026-01-01', to: '2026-01-31', accountId: 'credit' });
    expect(scoped.accountId).toBe('credit');
    expect(scoped.transactionCount).toBe(1);
    expect(scoped.spending.total).toBe('60.00');
    expect(scoped.cashFlow.totals.moneyOut).toBe('60.00');
  });
});
