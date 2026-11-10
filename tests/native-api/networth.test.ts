import { describe, it, expect, beforeEach, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});
vi.mock('../../native-api/src/core/backup.js', () => ({ backupDb: async () => {} }));

import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema } from '../../native-api/src/services/schema.js';
import {
  netWorthHistory, currentSummary, allocation, accountSummaries,
  sampleDates, snapshotAsOf,
} from '../../native-api/src/services/networth.js';

const today = new Date().toISOString().slice(0, 10);
function daysAgo(n: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - n);
  return d.toISOString().slice(0, 10);
}

async function clean() {
  for (const t of ['transactions', 'balance_history', 'accounts', 'plaid_items', 'sync_item_runs', 'sync_runs']) {
    await db.execute(`DELETE FROM ${t}`).catch(() => {});
  }
}

async function addAccount(id: string, type: string, over: Record<string, string | number | null> = {}) {
  const cols: Record<string, string | number | null> = {
    id, name: id, type,
    subtype: over.subtype ?? null,
    source: over.source ?? 'plaid',
    account_class: over.account_class ?? null,
    manual_category: over.manual_category ?? null,
    currency_code: over.currency_code ?? 'USD',
    excluded: over.excluded ?? 0,
    archived_at: over.archived_at ?? null,
  };
  await db.execute({
    sql: `INSERT INTO accounts (id, name, type, subtype, source, account_class, manual_category, currency_code, excluded, archived_at)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    args: [cols.id, cols.name, cols.type, cols.subtype, cols.source, cols.account_class,
           cols.manual_category, cols.currency_code, cols.excluded, cols.archived_at],
  });
}

async function addSnapshot(accountId: string, date: string, balance: number) {
  await db.execute({
    sql: `INSERT INTO balance_history (account_id, balance, date) VALUES (?, ?, ?)
          ON CONFLICT(account_id, date) DO UPDATE SET balance = excluded.balance`,
    args: [accountId, balance, date],
  });
}

beforeEach(async () => {
  await ensureNativeSchema();
  await clean();
});

describe('carry-forward as-of series (SPEC §9.5)', () => {
  it('uses posted Transactions data for a labelled estimate before the first real snapshot', async () => {
    await addAccount('checking-estimate', 'depository');
    await addAccount('card-estimate', 'credit');
    await addSnapshot('checking-estimate', today, 900);
    await addSnapshot('card-estimate', today, 72);
    await db.batch([
      { sql: `INSERT INTO transactions (id, account_id, date, name, amount, pending)
              VALUES ('estimate-anchor', 'checking-estimate', ?, 'Anchor', 0, 0)`, args: [daysAgo(4)] },
      { sql: `INSERT INTO transactions (id, account_id, date, name, amount, pending)
              VALUES ('checking-spend', 'checking-estimate', ?, 'Purchase', 100, 0)`, args: [daysAgo(1)] },
      { sql: `INSERT INTO transactions (id, account_id, date, name, amount, pending)
              VALUES ('card-spend', 'card-estimate', ?, 'Card purchase', 50, 0)`, args: [daysAgo(1)] },
      { sql: `INSERT INTO transactions (id, account_id, date, name, amount, pending)
              VALUES ('pending-spend', 'checking-estimate', ?, 'Pending', 999, 1)`, args: [daysAgo(1)] },
    ], 'write');

    const points = await netWorthHistory(daysAgo(4), today, 'day');
    expect(points).toHaveLength(5);
    expect(points[0].source).toBe('transactionEstimate');
    expect(points[0].assets.amount).toBe('1000.00');
    expect(points[0].liabilities.amount).toBe('22.00');
    expect(points[0].netWorth.amount).toBe('978.00');
    expect(points.at(-1)?.source).toBe('observed');
    expect(points.at(-1)?.netWorth.amount).toBe('828.00');
  });

  it('carries an old manual value forward across dates with no snapshot', async () => {
    await addAccount('home', 'other', { subtype: 'manual', source: 'manual', manual_category: 'property', account_class: 'asset' });
    await addSnapshot('home', daysAgo(10), 500000);

    const points = await netWorthHistory(daysAgo(5), today, 'day');
    expect(points.length).toBe(6);
    for (const p of points) {
      expect(p.assets.amount).toBe('500000.00'); // still effective days later
      expect(p.netWorth.amount).toBe('500000.00');
    }
  });

  it('does not sum only dates where snapshots exist (the upstream bug)', async () => {
    await addAccount('checking', 'depository');
    await addAccount('brokerage', 'investment');
    // checking has a snapshot every day; brokerage only 3 days ago.
    await addSnapshot('checking', daysAgo(2), 1000);
    await addSnapshot('checking', daysAgo(1), 1000);
    await addSnapshot('checking', daysAgo(0), 1000);
    await addSnapshot('brokerage', daysAgo(3), 5000);

    const points = await netWorthHistory(daysAgo(2), today, 'day');
    // Every sampled day must include the brokerage's carried-forward 5000.
    for (const p of points) expect(p.assets.amount).toBe('6000.00');
  });

  it('skips accounts with no snapshot on or before the sample date', async () => {
    await addAccount('checking', 'depository');
    await addAccount('new-acct', 'depository');
    await addSnapshot('checking', daysAgo(5), 1000);
    await addSnapshot('new-acct', daysAgo(0), 700);

    const points = await netWorthHistory(daysAgo(2), today, 'day');
    expect(points[0].assets.amount).toBe('1000.00');      // new-acct not yet born
    expect(points[points.length - 1].assets.amount).toBe('1700.00');
  });

  it('estimates a later-linked Plaid account before its own first snapshot', async () => {
    await addAccount('already-linked', 'depository');
    await addAccount('linked-later', 'depository');
    await addSnapshot('already-linked', daysAgo(5), 1000);
    await addSnapshot('linked-later', today, 700);
    await db.batch([
      { sql: `INSERT INTO transactions (id, account_id, date, name, amount, pending)
              VALUES ('later-anchor', 'linked-later', ?, 'Anchor', 0, 0)`, args: [daysAgo(4)] },
      { sql: `INSERT INTO transactions (id, account_id, date, name, amount, pending)
              VALUES ('later-spend', 'linked-later', ?, 'Purchase', 100, 0)`, args: [daysAgo(2)] },
    ], 'write');

    const points = await netWorthHistory(daysAgo(4), today, 'day');
    expect(points[0]).toMatchObject({
      source: 'transactionEstimate',
      assets: { amount: '1800.00' },
    });
    expect(points.at(-1)).toMatchObject({
      source: 'observed',
      assets: { amount: '1700.00' },
    });

    const accounts = await accountSummaries({ includeExcluded: false });
    const later = accounts.find((account) => account.id === 'linked-later');
    expect(later?.sparkline[0]).toMatchObject({ amount: '800.00' });
    expect(later?.sparkline.at(-1)).toMatchObject({ amount: '700.00' });
  });

  it('returns empty series when no data exists (no fake flat lines, SPEC §16.4)', async () => {
    const points = await netWorthHistory(daysAgo(30), today, 'day');
    expect(points).toEqual([]);
  });
});

describe('net-worth composition (SPEC §9.4)', () => {
  it('subtracts loans as liabilities (upstream ignored them)', async () => {
    await addAccount('checking', 'depository');
    await addAccount('autoloan', 'loan');
    await addSnapshot('checking', today, 10000);
    await addSnapshot('autoloan', today, 8000);

    const s = await currentSummary();
    expect(s.assets.amount).toBe('10000.00');
    expect(s.liabilities.amount).toBe('8000.00');
    expect(s.netWorth.amount).toBe('2000.00');
  });

  it('takes abs() of manual liabilities but preserves Plaid liability sign', async () => {
    await addAccount('mortgage', 'other', { subtype: 'manual', source: 'manual', manual_category: 'loan', account_class: 'liability' });
    await addAccount('visa', 'credit');
    await addSnapshot('mortgage', today, -250000); // legacy negative entry
    await addSnapshot('visa', today, -50);         // credit overpayment stays negative

    const s = await currentSummary();
    expect(s.liabilities.amount).toBe('249950.00'); // 250000 + (-50)
  });

  it('omits excluded accounts from totals and history (UC-008)', async () => {
    await addAccount('checking', 'depository');
    await addAccount('dupe', 'depository', { excluded: 1 });
    await addSnapshot('checking', today, 1000);
    await addSnapshot('dupe', today, 999);

    const s = await currentSummary();
    expect(s.assets.amount).toBe('1000.00');
    const points = await netWorthHistory(daysAgo(1), today, 'day');
    expect(points[points.length - 1].assets.amount).toBe('1000.00');
  });

  it('keeps non-USD accounts out of USD totals (SPEC §2.2.6)', async () => {
    await addAccount('checking', 'depository');
    await addAccount('eur-acct', 'depository', { currency_code: 'EUR' });
    await addSnapshot('checking', today, 1000);
    await addSnapshot('eur-acct', today, 5000);

    const s = await currentSummary();
    expect(s.assets.amount).toBe('1000.00');
  });

  it('treats no-snapshot accounts as NoBalance, not zero (SPEC §9.2)', async () => {
    await addAccount('checking', 'depository');
    const list = await accountSummaries();
    expect(list[0].balance).toBeNull();
  });

  it('derives Plaid provenance and institution from the item relationship', async () => {
    await db.execute({
      sql: `INSERT INTO plaid_items (item_id, access_token, institution_name, status)
            VALUES (?, ?, ?, ?)`,
      args: ['item-robinhood', 'encrypted-test-token', 'Robinhood', 'active'],
    });
    await db.execute({
      sql: `INSERT INTO accounts (id, name, type, subtype, item_id, source, currency_code)
            VALUES (?, ?, ?, ?, ?, ?, ?)`,
      args: ['rh-brokerage', 'Robinhood individual', 'investment', 'brokerage',
             'item-robinhood', null, 'USD'],
    });

    const list = await accountSummaries();
    expect(list[0].source).toBe('plaid');
    expect(list[0].institutionName).toBe('Robinhood');
  });

  it('returns balances as signed net-worth contributions', async () => {
    await addAccount('checking', 'depository');
    await addAccount('visa', 'credit');
    await addAccount('overpaid-card', 'credit');
    await addSnapshot('checking', today, 1000);
    await addSnapshot('visa', daysAgo(1), 50);
    await addSnapshot('visa', today, 72);
    await addSnapshot('overpaid-card', today, -20);

    const list = await accountSummaries();
    const byId = Object.fromEntries(list.map((a) => [a.id, a]));
    expect(byId.checking.balance?.amount).toBe('1000.00');
    expect(byId.visa.balance?.amount).toBe('-72.00');
    expect(byId.visa.change?.amount).toBe('-22.00');
    expect(byId['overpaid-card'].balance?.amount).toBe('20.00');
  });
});

describe('change computation (SPEC §9.7)', () => {
  it('labels the comparison "yesterday" only when previous state is actually yesterday', async () => {
    await addAccount('checking', 'depository');
    await addSnapshot('checking', daysAgo(1), 900);
    await addSnapshot('checking', today, 1000);

    const s = await currentSummary();
    expect(s.change?.comparison).toBe('yesterday');
    expect(s.change?.amount).toBe('100.00');
  });

  it('labels older comparisons "previousRecord"', async () => {
    await addAccount('checking', 'depository');
    await addSnapshot('checking', daysAgo(9), 900);
    await addSnapshot('checking', today, 1000);

    const s = await currentSummary();
    expect(s.change?.comparison).toBe('previousRecord');
    expect(s.change?.comparedTo).toBe(daysAgo(9));
  });

  it('compares complete portfolio states, not summed row deltas', async () => {
    await addAccount('a', 'depository');
    await addAccount('b', 'depository');
    // a: snapshots on day-2 and today; b: only day-1. Row-delta logic would
    // pair mismatched dates; portfolio-state logic compares day-1 vs today.
    await addSnapshot('a', daysAgo(2), 100);
    await addSnapshot('b', daysAgo(1), 500);
    await addSnapshot('a', today, 200);

    const s = await currentSummary();
    // today's state: a=200 + b=500(carried) = 700; previous state (day-1): a=100(carried) + b=500 = 600.
    expect(s.netWorth.amount).toBe('700.00');
    expect(s.change?.amount).toBe('100.00');
  });

  it('returns null change with a single record (SPEC §22.3)', async () => {
    await addAccount('checking', 'depository');
    await addSnapshot('checking', today, 1000);
    const s = await currentSummary();
    expect(s.change).toBeNull();
  });
});

describe('allocation (SPEC §9.8)', () => {
  it('includes assets only and drills down to account ids', async () => {
    await addAccount('checking', 'depository');
    await addAccount('visa', 'credit');
    await addAccount('car', 'other', { subtype: 'manual', source: 'manual', manual_category: 'vehicle', account_class: 'asset' });
    await addSnapshot('checking', today, 6000);
    await addSnapshot('visa', today, 1500);
    await addSnapshot('car', today, 4000);

    const a = await allocation();
    expect(a.totalAssets.amount).toBe('10000.00');
    const buckets = a.slices.map((s) => s.bucket);
    expect(buckets).toEqual(['bankCash', 'vehicle']); // no credit slice
    expect(a.slices[0].share).toBe('0.6000');
    expect(a.slices[1].accountIds).toEqual(['car']);
  });
});

describe('sampling (SPEC §9.6)', () => {
  it('caps series at 500 points and keeps the final point', () => {
    const dates = sampleDates('2020-01-01', '2026-01-01', 'day');
    expect(dates.length).toBeLessThanOrEqual(501);
    expect(dates[dates.length - 1]).toBe('2026-01-01');
  });

  it('month granularity emits month ends plus the current date', () => {
    const dates = sampleDates('2026-01-15', '2026-03-10', 'month');
    expect(dates).toEqual(['2026-01-31', '2026-02-28', '2026-03-10']);
  });

  it('binary search picks the latest snapshot on or before the date', () => {
    const snaps = [
      { date: '2026-01-01', cents: 1 },
      { date: '2026-01-05', cents: 2 },
      { date: '2026-02-01', cents: 3 },
    ];
    expect(snapshotAsOf(snaps, '2025-12-31')).toBeNull();
    expect(snapshotAsOf(snaps, '2026-01-01')?.cents).toBe(1);
    expect(snapshotAsOf(snaps, '2026-01-10')?.cents).toBe(2);
    expect(snapshotAsOf(snaps, '2026-03-01')?.cents).toBe(3);
  });
});
