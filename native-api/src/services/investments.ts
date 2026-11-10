import { db } from '../core/database.js';
import { centsToAmount, toCents } from './money.js';

export type InvestmentRange = '1d' | '1w' | '1m' | '3m' | '1y' | 'all';

export type InvestmentAllocationInput = {
  bucket: string;
  value: number;
  cashEquivalent: boolean;
};

export type InvestmentAllocationSlice = {
  bucket: string;
  value: number;
};

/** Stable semantic bucket for every instrument discriminator returned by SnapTrade. */
export function investmentBucketForInstrument(kind: string): string {
  switch (kind.trim().toLowerCase()) {
    case 'stock':
    case 'adr':
      return 'Stocks';
    case 'etf':
    case 'cef':
      return 'ETFs';
    case 'mutualfund':
      return 'Mutual Funds';
    case 'crypto':
      return 'Crypto';
    default:
      return 'Other';
  }
}

const amount = (value: number): string => (Number.isFinite(value) ? value : 0).toFixed(2);

function fromDate(range: InvestmentRange, to: Date): string | null {
  if (range === 'all') return null;
  const from = new Date(to);
  from.setUTCDate(from.getUTCDate() - ({ '1d': 1, '1w': 7, '1m': 31, '3m': 92, '1y': 366 } as const)[range]);
  return from.toISOString().slice(0, 10);
}

function bucketKey(date: string, range: InvestmentRange): string {
  if (range === 'all') return date.slice(0, 7);
  if (range === '1y') {
    const d = new Date(`${date}T00:00:00Z`);
    const start = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
    const week = Math.floor((d.getTime() - start.getTime()) / 604_800_000);
    return `${d.getUTCFullYear()}-${String(week).padStart(2, '0')}`;
  }
  return date;
}

/**
 * Reconciles security-level values and the broker's cash balance to the
 * account totals shown elsewhere in the UI. Some brokers expose a sweep fund
 * both as a cash-equivalent position and in `cash`; blindly adding both makes
 * the donut exceed the portfolio total. The provider account total is the
 * authoritative constraint: remove overlap from Cash first, preserve a
 * missing provider breakdown as Unclassified, and only proportionally
 * reconcile securities when stale position quotes alone exceed the current
 * total. A balance is not cash merely because the provider omitted positions.
 */
export function buildInvestmentAllocation(
  positions: InvestmentAllocationInput[],
  reportedCash: number,
  portfolioTotal: number,
): InvestmentAllocationSlice[] {
  const target = Math.max(0, Number.isFinite(portfolioTotal) ? portfolioTotal : 0);
  const buckets = new Map<string, number>();
  for (const position of positions) {
    const value = Number.isFinite(position.value) ? Math.max(0, position.value) : 0;
    if (value === 0) continue;
    const bucket = position.cashEquivalent ? 'Cash' : position.bucket;
    buckets.set(bucket, (buckets.get(bucket) ?? 0) + value);
  }
  const safeReportedCash = Number.isFinite(reportedCash) ? Math.max(0, reportedCash) : 0;
  if (safeReportedCash > 0) buckets.set('Cash', (buckets.get('Cash') ?? 0) + safeReportedCash);

  let allocated = [...buckets.values()].reduce((sum, value) => sum + value, 0);
  if (allocated < target) {
    buckets.set('Unclassified', (buckets.get('Unclassified') ?? 0) + target - allocated);
  } else if (allocated > target) {
    let excess = allocated - target;
    const cash = buckets.get('Cash') ?? 0;
    const cashReduction = Math.min(cash, excess);
    if (cashReduction > 0) {
      const reconciledCash = cash - cashReduction;
      if (reconciledCash > 1e-9) buckets.set('Cash', reconciledCash);
      else buckets.delete('Cash');
      excess -= cashReduction;
    }

    if (excess > 1e-9) {
      allocated = [...buckets.values()].reduce((sum, value) => sum + value, 0);
      const scale = allocated === 0 ? 0 : target / allocated;
      for (const [bucket, value] of buckets) buckets.set(bucket, value * scale);
    }
  }

  return [...buckets.entries()]
    .filter(([, value]) => value > 1e-9)
    .sort((a, b) => b[1] - a[1])
    .map(([bucket, value]) => ({ bucket, value }));
}

export async function investmentAccounts() {
  const result = await db.execute(`
    SELECT a.id, a.name, a.nickname, a.institution_name, a.mask, a.source,
           a.provider_connection_id,
           (SELECT bh.balance FROM balance_history bh
             WHERE bh.account_id = a.id ORDER BY bh.date DESC LIMIT 1) AS balance,
           (SELECT bh.date FROM balance_history bh
             WHERE bh.account_id = a.id ORDER BY bh.date DESC LIMIT 1) AS balance_date,
           (SELECT bh2.balance FROM balance_history bh2
             WHERE bh2.account_id = a.id ORDER BY bh2.date DESC LIMIT 1 OFFSET 1) AS previous_balance,
           (SELECT COUNT(*) FROM investment_positions p WHERE p.account_id = a.id) AS position_count,
           COALESCE(
             (SELECT MAX(p.as_of_at) FROM investment_positions p WHERE p.account_id = a.id),
             (SELECT sc.last_success_at FROM snaptrade_connections sc
               WHERE sc.connection_id = a.provider_connection_id),
             (SELECT datetime(pi.last_synced_at / 1000, 'unixepoch') FROM plaid_items pi
               WHERE pi.item_id = a.item_id)
           ) AS last_synced_at
      FROM accounts a
     WHERE a.type = 'investment' AND a.archived_at IS NULL
     ORDER BY COALESCE(balance, 0) DESC, a.name`);
  return (result.rows as unknown as Record<string, unknown>[]).map((row) => {
    const balance = Number(row.balance ?? 0);
    const previous = row.previous_balance === null ? null : Number(row.previous_balance);
    const change = previous === null ? null : balance - previous;
    return {
      id: String(row.id),
      name: String(row.nickname ?? row.name ?? 'Investment account'),
      institutionName: row.institution_name === null ? null : String(row.institution_name),
      mask: row.mask === null ? null : String(row.mask),
      source: String(row.source ?? 'imported'),
      balance: { amount: amount(balance), currencyCode: 'USD', asOfDate: row.balance_date === null ? null : String(row.balance_date) },
      change: change === null ? null : {
        amount: amount(change),
        ratio: previous === null || previous === 0 ? null : (change / Math.abs(previous)).toFixed(6),
      },
      positionCount: Number(row.position_count ?? 0),
      lastSyncedAt: row.last_synced_at === null ? null : String(row.last_synced_at),
    };
  });
}

export async function investmentPositions(accountId?: string) {
  const args: string[] = [];
  const filter = accountId ? 'WHERE p.account_id = ?' : '';
  if (accountId) args.push(accountId);
  const result = await db.execute({
    sql: `SELECT p.*, a.name AS account_name, a.institution_name
            FROM investment_positions p
            JOIN accounts a ON a.id = p.account_id
            ${filter}
           ORDER BY CAST(p.market_value AS REAL) DESC, p.symbol`,
    args,
  });
  return (result.rows as unknown as Record<string, unknown>[]).map((row) => {
    const marketValue = Number(row.market_value ?? 0);
    const gainLoss = row.gain_loss === null ? null : Number(row.gain_loss);
    const cost = marketValue - (gainLoss ?? 0);
    return {
      id: String(row.id),
      accountId: String(row.account_id),
      accountName: String(row.account_name),
      institutionName: row.institution_name === null ? null : String(row.institution_name),
      symbol: String(row.symbol),
      name: String(row.name),
      instrumentType: String(row.instrument_type),
      units: row.units === null ? null : String(row.units),
      averagePrice: row.average_price === null ? null : String(row.average_price),
      currentPrice: row.price === null ? null : String(row.price),
      value: amount(marketValue),
      gainLoss: gainLoss === null ? null : amount(gainLoss),
      gainLossRatio: gainLoss === null || cost === 0 ? null : (gainLoss / Math.abs(cost)).toFixed(6),
      currencyCode: String(row.currency_code ?? 'USD'),
      cashEquivalent: Number(row.cash_equivalent ?? 0) === 1,
      asOfAt: String(row.as_of_at),
    };
  });
}

export async function investmentActivities(limit = 100, accountId?: string) {
  const safeLimit = Math.min(Math.max(Math.trunc(limit), 1), 1000);
  const args: (string | number)[] = [];
  const filter = accountId ? 'WHERE ia.account_id = ?' : '';
  if (accountId) args.push(accountId);
  args.push(safeLimit);
  const result = await db.execute({
    sql: `SELECT ia.*, a.name AS account_name, a.institution_name
            FROM investment_activities ia
            JOIN accounts a ON a.id = ia.account_id
            ${filter}
           ORDER BY COALESCE(ia.trade_date, ia.updated_at) DESC
           LIMIT ?`,
    args,
  });
  return (result.rows as unknown as Record<string, unknown>[]).map((row) => ({
    id: String(row.id),
    accountId: String(row.account_id),
    accountName: String(row.account_name),
    institutionName: row.institution_name === null ? null : String(row.institution_name),
    type: String(row.activity_type),
    tradeDate: row.trade_date === null ? null : String(row.trade_date),
    settlementDate: row.settlement_date === null ? null : String(row.settlement_date),
    symbol: row.symbol === null ? null : String(row.symbol),
    description: row.description === null ? null : String(row.description),
    units: row.units === null ? null : String(row.units),
    price: row.price === null ? null : String(row.price),
    amount: row.amount === null ? null : String(row.amount),
    fee: row.fee === null ? null : String(row.fee),
    currencyCode: String(row.currency_code ?? 'USD'),
  }));
}

export async function investmentOrders(limit = 100, accountId?: string) {
  const safeLimit = Math.min(Math.max(Math.trunc(limit), 1), 1000);
  const args: (string | number)[] = [];
  const filter = accountId ? 'WHERE io.account_id = ?' : '';
  if (accountId) args.push(accountId);
  args.push(safeLimit);
  const result = await db.execute({
    sql: `SELECT io.*, a.name AS account_name, a.institution_name
            FROM investment_orders io
            JOIN accounts a ON a.id = io.account_id
            ${filter}
           ORDER BY COALESCE(io.time_placed, io.time_updated, io.time_executed) DESC
           LIMIT ?`,
    args,
  });
  return (result.rows as unknown as Record<string, unknown>[]).map((row) => ({
    id: String(row.id),
    accountId: String(row.account_id),
    accountName: String(row.account_name),
    institutionName: row.institution_name === null ? null : String(row.institution_name),
    brokerageOrderId: row.brokerage_order_id === null ? null : String(row.brokerage_order_id),
    status: String(row.status),
    action: row.action === null ? null : String(row.action),
    symbol: row.symbol === null ? null : String(row.symbol),
    orderType: row.order_type === null ? null : String(row.order_type),
    timeInForce: row.time_in_force === null ? null : String(row.time_in_force),
    totalQuantity: row.total_quantity === null ? null : String(row.total_quantity),
    openQuantity: row.open_quantity === null ? null : String(row.open_quantity),
    filledQuantity: row.filled_quantity === null ? null : String(row.filled_quantity),
    executionPrice: row.execution_price === null ? null : String(row.execution_price),
    limitPrice: row.limit_price === null ? null : String(row.limit_price),
    stopPrice: row.stop_price === null ? null : String(row.stop_price),
    timePlaced: row.time_placed === null ? null : String(row.time_placed),
    timeUpdated: row.time_updated === null ? null : String(row.time_updated),
    timeExecuted: row.time_executed === null ? null : String(row.time_executed),
    expiryDate: row.expiry_date === null ? null : String(row.expiry_date),
  }));
}

export type RealizedMethod = 'fifo';

/**
 * Realized profit and loss from cached brokerage activity, matched FIFO.
 *
 * Only BUY and SELL rows carrying a symbol open or close lots. SnapTrade also
 * reports card and cash activity (POSDEBIT, POSCREDIT, ADJUSTMENT) through the
 * same table with no symbol and zero units; those never touch a lot.
 *
 * Cost is carried per lot in integer cents and consumed proportionally, so a
 * partially sold lot cannot drift and a fully consumed lot releases exactly the
 * cents it was opened with. Proceeds come from the activity's own `amount`
 * (the cash the brokerage actually moved) rather than units × price.
 *
 * A sell with no matching buy is reported with `basisComplete: false` and a
 * realized figure covering only the units we can account for. Cached history is
 * finite, so shares bought before it begins genuinely have no basis here, and
 * inventing one would misstate a financial number.
 */
export async function investmentRealizedPnl(limit = 200, accountId?: string) {
  const safeLimit = Math.min(Math.max(Math.trunc(limit), 1), 1000);
  const args: string[] = [];
  const filter = accountId ? 'AND ia.account_id = ?' : '';
  if (accountId) args.push(accountId);
  const result = await db.execute({
    sql: `SELECT ia.*, a.name AS account_name, a.institution_name
            FROM investment_activities ia
            JOIN accounts a ON a.id = ia.account_id
           WHERE ia.activity_type IN ('BUY', 'SELL')
             AND ia.symbol IS NOT NULL AND TRIM(ia.symbol) <> ''
             ${filter}
           ORDER BY COALESCE(ia.trade_date, ia.updated_at) ASC`,
    args,
  });
  const rows = result.rows as unknown as Record<string, unknown>[];

  type Lot = { units: number; costCents: number };
  const lots = new Map<string, Lot[]>();
  const trades: Record<string, unknown>[] = [];
  let totalCents = 0;
  let yearCents = 0;
  let monthCents = 0;
  let buyCount = 0;
  let sellCount = 0;
  let tradesMissingBasis = 0;

  const now = new Date();
  const yearPrefix = String(now.getUTCFullYear());
  const monthPrefix = `${yearPrefix}-${String(now.getUTCMonth() + 1).padStart(2, '0')}`;

  for (const row of rows) {
    const symbol = String(row.symbol);
    // Lots are per account and symbol: the same ticker in two accounts is two
    // independent inventories, and matching across them would be wrong.
    const key = `${String(row.account_id)} ${symbol}`;
    const units = Math.abs(Number(row.units ?? 0));
    const isBuy = String(row.activity_type) === 'BUY';
    const cashCents = toCents(Math.abs(Number(row.amount ?? 0)));
    const feeCents = toCents(Math.abs(Number(row.fee ?? 0)));
    if (!Number.isFinite(units) || units <= 0) continue;

    const base = {
      id: String(row.id),
      accountId: String(row.account_id),
      accountName: String(row.account_name),
      institutionName: row.institution_name === null ? null : String(row.institution_name),
      tradeDate: row.trade_date === null ? null : String(row.trade_date),
      symbol,
      description: row.description === null ? null : String(row.description),
      units: String(units),
      price: row.price === null ? null : String(row.price),
      currencyCode: String(row.currency_code ?? 'USD'),
      fee: centsToAmount(feeCents),
    };

    if (isBuy) {
      buyCount += 1;
      const inventory = lots.get(key) ?? [];
      // A buy's fee is part of what the shares cost.
      inventory.push({ units, costCents: cashCents + feeCents });
      lots.set(key, inventory);
      trades.push({
        ...base,
        action: 'buy',
        amount: centsToAmount(cashCents),
        costBasis: centsToAmount(cashCents + feeCents),
        realized: null,
        realizedRatio: null,
        basisComplete: true,
      });
      continue;
    }

    sellCount += 1;
    const inventory = lots.get(key) ?? [];
    let remaining = units;
    let basisCents = 0;
    let matchedUnits = 0;
    while (remaining > 1e-9 && inventory.length > 0) {
      const lot = inventory[0];
      const take = Math.min(remaining, lot.units);
      // Proportional draw keeps integer cents exact when the lot is emptied.
      const takeCents = take >= lot.units
        ? lot.costCents
        : Math.round((lot.costCents * take) / lot.units);
      basisCents += takeCents;
      lot.costCents -= takeCents;
      lot.units -= take;
      matchedUnits += take;
      remaining -= take;
      if (lot.units <= 1e-9) inventory.shift();
    }
    lots.set(key, inventory);

    const basisComplete = remaining <= 1e-6;
    if (!basisComplete) tradesMissingBasis += 1;
    // Only the covered fraction of the sale can be scored against a basis.
    const coveredRatio = units === 0 ? 0 : matchedUnits / units;
    const proceedsCents = Math.round(cashCents * coveredRatio);
    const realizedCents = proceedsCents - basisCents - Math.round(feeCents * coveredRatio);

    totalCents += realizedCents;
    const date = base.tradeDate ?? '';
    if (date.startsWith(yearPrefix)) yearCents += realizedCents;
    if (date.startsWith(monthPrefix)) monthCents += realizedCents;

    trades.push({
      ...base,
      action: 'sell',
      amount: centsToAmount(cashCents),
      costBasis: centsToAmount(basisCents),
      realized: centsToAmount(realizedCents),
      realizedRatio: basisCents === 0 ? null : (realizedCents / Math.abs(basisCents)).toFixed(6),
      basisComplete,
    });
  }

  // Newest first for display; the FIFO walk above required oldest first.
  trades.reverse();
  return {
    method: 'fifo' as RealizedMethod,
    summary: {
      total: centsToAmount(totalCents),
      yearToDate: centsToAmount(yearCents),
      monthToDate: centsToAmount(monthCents),
      buyCount,
      sellCount,
      tradesMissingBasis,
      currencyCode: 'USD',
    },
    trades: trades.slice(0, safeLimit),
  };
}

export async function investmentPerformance(range: InvestmentRange) {
  const today = new Date();
  const to = today.toISOString().slice(0, 10);
  const from = fromDate(range, today);
  const accountResult = await db.execute(
    "SELECT id FROM accounts WHERE type = 'investment' AND archived_at IS NULL AND excluded = 0",
  );
  const accountIds = (accountResult.rows as unknown as { id: string }[]).map((r) => r.id);
  if (accountIds.length === 0) return [];
  const placeholders = accountIds.map(() => '?').join(',');
  const history = await db.execute({
    sql: `SELECT account_id, date, balance FROM balance_history
           WHERE account_id IN (${placeholders}) AND date <= ?
           ORDER BY date, account_id`,
    args: [...accountIds, to],
  });
  const rows = history.rows as unknown as { account_id: string; date: string; balance: number }[];
  const latest = new Map<string, number>();
  const points: { date: string; value: number }[] = [];
  let index = 0;
  while (index < rows.length) {
    const date = rows[index].date;
    while (index < rows.length && rows[index].date === date) {
      latest.set(rows[index].account_id, Number(rows[index].balance));
      index++;
    }
    if (from === null || date >= from) {
      points.push({ date, value: [...latest.values()].reduce((sum, value) => sum + value, 0) });
    }
  }
  const bucketed = new Map<string, { date: string; value: number }>();
  for (const point of points) bucketed.set(bucketKey(point.date, range), point);
  const compact = [...bucketed.values()];
  const first = compact[0]?.value ?? 0;
  return compact.map((point) => ({
    date: point.date,
    value: amount(point.value),
    gain: amount(point.value - first),
    gainRatio: first === 0 ? null : ((point.value - first) / Math.abs(first)).toFixed(6),
  }));
}

export async function investmentOverview(range: InvestmentRange) {
  const [accounts, positions, performance] = await Promise.all([
    investmentAccounts(),
    investmentPositions(),
    investmentPerformance(range),
  ]);
  const totalValue = accounts.reduce((sum, account) => sum + Number(account.balance.amount), 0);
  const totalGain = positions.reduce((sum, position) => sum + Number(position.gainLoss ?? 0), 0);
  const investedValue = positions
    .filter((position) => !position.cashEquivalent)
    .reduce((sum, position) => sum + Math.max(0, Number(position.value)), 0);
  const cost = investedValue - totalGain;
  const allocationPositions = positions.map((position) => ({
    bucket: investmentBucketForInstrument(position.instrumentType),
    value: Number(position.value),
    cashEquivalent: position.cashEquivalent,
  }));
  const cashResult = await db.execute(`
    SELECT COALESCE(SUM(CAST(ib.cash AS REAL)), 0) AS cash
      FROM investment_balances ib JOIN accounts a ON a.id = ib.account_id
     WHERE a.archived_at IS NULL`);
  const cash = Number((cashResult.rows[0] as unknown as { cash: number }).cash ?? 0);
  const allocation = buildInvestmentAllocation(allocationPositions, cash, totalValue);
  const allocatedTotal = allocation.reduce((sum, slice) => sum + slice.value, 0);
  return {
    totalValue: { amount: amount(totalValue), currencyCode: 'USD' },
    totalGainLoss: {
      amount: amount(totalGain),
      ratio: cost === 0 ? null : (totalGain / Math.abs(cost)).toFixed(6),
    },
    accounts,
    positions,
    performance,
    allocation: allocation.map((slice) => ({
        bucket: slice.bucket,
        amount: amount(slice.value),
        share: allocatedTotal === 0 ? null : (slice.value / allocatedTotal).toFixed(6),
      })),
  };
}
