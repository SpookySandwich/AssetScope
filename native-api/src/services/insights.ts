import { db } from '../core/database.js';
import type { Range } from './networth.js';

export type InsightFact = {
  id: string;
  kind: 'spendingIncrease';
  tone: 'neutral' | 'caution';
  confidence: 'high' | 'medium' | 'low';
  priority: number;
  primaryAmount: string | null;
  secondaryAmount: string | null;
  tertiaryAmount: string | null;
  category: string | null;
  transactionCount: number;
};

const money = (value: number): string => (Number.isFinite(value) ? value : 0).toFixed(2);

function rangeDays(range: Range): number {
  return range === '1m' ? 31 : range === '3m' ? 92 : range === '1y' ? 366 : 3650;
}

function isoDaysAgo(days: number): string {
  return new Date(Date.now() - days * 86_400_000).toISOString().slice(0, 10);
}

export async function generateInsights(range: Range, limit = 3) {
  const days = rangeDays(range);
  const from = range === 'all' ? '0001-01-01' : isoDaysAgo(days);
  const previousFrom = range === 'all' ? from : isoDaysAgo(days * 2);
  const insightFacts: InsightFact[] = [];
  const spendingRows = await db.execute({
    sql: `SELECT t.date, COALESCE(t.manual_category, t.category, t.raw_category, 'OTHER') AS category,
                 t.amount
            FROM transactions t JOIN accounts a ON a.id = t.account_id
           WHERE t.date >= ? AND t.pending = 0 AND t.ignored = 0 AND t.amount > 0
             AND COALESCE(t.manual_category, t.category, t.raw_category, 'OTHER')
                 NOT IN ('TRANSFER_IN', 'TRANSFER_OUT', 'INCOME', 'LOAN_PAYMENTS')
             AND a.archived_at IS NULL AND a.excluded = 0`,
    args: [previousFrom],
  });
  const current = new Map<string, { amount: number; count: number }>();
  const previous = new Map<string, { amount: number; count: number }>();
  for (const row of spendingRows.rows as unknown as { date: string; category: string; amount: number }[]) {
    const target = row.date >= from ? current : previous;
    const item = target.get(row.category) ?? { amount: 0, count: 0 };
    item.amount += Number(row.amount);
    item.count++;
    target.set(row.category, item);
  }
  const spendingChange = range === 'all' ? undefined : [...current.entries()]
    .map(([category, value]) => ({
      category,
      amount: value.amount,
      count: value.count,
      increase: value.amount - (previous.get(category)?.amount ?? 0),
      baseline: previous.get(category)?.amount ?? 0,
    }))
    .filter((item) => item.count >= 3 && item.baseline >= 50 && item.increase >= 100
      && item.increase / item.baseline >= 0.25)
    .sort((a, b) => b.increase - a.increase)[0];
  if (spendingChange) {
    insightFacts.push({
      id: `spending-increase-${spendingChange.category}-${from}`,
      kind: 'spendingIncrease', tone: 'caution', confidence: 'medium', priority: 80,
      primaryAmount: money(spendingChange.increase),
      secondaryAmount: money(spendingChange.amount),
      tertiaryAmount: money(spendingChange.baseline),
      category: spendingChange.category,
      transactionCount: spendingChange.count,
    });
  }

  return {
    period: { from, to: new Date().toISOString().slice(0, 10), range },
    items: insightFacts
      .sort((a, b) => b.priority - a.priority || a.id.localeCompare(b.id))
      .slice(0, Math.min(Math.max(Math.trunc(limit), 1), 10)),
  };
}
