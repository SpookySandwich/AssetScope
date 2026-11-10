import { db } from '../core/database.js';
import { classify, allocationBucket, netWorthContributionCents, type AllocationBucket } from './classify.js';
import { toCents, centsToAmount, money, ratio, type MoneyDto } from './money.js';
import { effectiveAccountSource, effectiveInstitutionName } from './account-provenance.js';
import { accountIdentities, type CardProductRef } from './rewards.js';

// Net-worth engine (SPEC §9). All series use as-of carry-forward: for each
// sample date every included account contributes its latest snapshot on or
// before that date. The upstream queries in core/queries.ts sum only snapshots
// that happen to exist in each period, so this module owns its own SQL.

export type AccountRecord = {
  id: string;
  name: string;
  nickname: string | null;
  owner: string | null;
  type: string;
  subtype: string | null;
  institution_name: string | null;
  mask: string | null;
  item_id: string | null;
  excluded: boolean;
  archived_at: string | null;
  source: string;
  account_class: string | null;
  manual_category: string | null;
  currency_code: string | null;
  notes: string | null;
  updated_at: string | null;
};

export type Snapshot = { date: string; cents: number };

export type PortfolioState = {
  date: string;
  assetsCents: number;
  liabilitiesCents: number;
  netWorthCents: number;
};

export async function loadAccounts(opts: {
  includeExcluded?: boolean;
  includeArchived?: boolean;
} = {}): Promise<AccountRecord[]> {
  const res = await db.execute(`
    SELECT a.id, a.name, a.nickname, a.owner, a.type, a.subtype, a.institution_name, a.mask, a.item_id,
           a.excluded, a.archived_at, a.source, a.account_class, a.manual_category, a.currency_code,
           a.notes, a.updated_at, p.institution_name AS plaid_institution_name
    FROM accounts a
    LEFT JOIN plaid_items p ON p.item_id = a.item_id
    ORDER BY a.name
  `);
  type AccountDbRow = Omit<AccountRecord, 'excluded' | 'source'> & {
    excluded: number;
    source: string | null;
    plaid_institution_name: string | null;
  };
  let rows = (res.rows as unknown as AccountDbRow[]).map((row) => {
    const { plaid_institution_name: itemInstitution, ...account } = row;
    return {
      ...account,
      institution_name: effectiveInstitutionName(account.institution_name, itemInstitution),
      source: effectiveAccountSource(account),
      excluded: Number(account.excluded) === 1,
    };
  });
  if (!opts.includeArchived) rows = rows.filter((r) => r.archived_at === null);
  if (!opts.includeExcluded) rows = rows.filter((r) => !r.excluded);
  return rows;
}

/** What each account is called wherever it is shown, by account id. */
export async function accountDisplayNames(): Promise<Map<string, string>> {
  const identities = await accountIdentities(await loadAccounts({ includeExcluded: true, includeArchived: true }));
  return new Map([...identities].map(([id, identity]) => [id, identity.displayName]));
}

/** All snapshots for the given accounts, sorted ascending by date per account. */
export async function loadSnapshots(accountIds: string[]): Promise<Map<string, Snapshot[]>> {
  const map = new Map<string, Snapshot[]>();
  if (accountIds.length === 0) return map;
  const placeholders = accountIds.map(() => '?').join(',');
  const res = await db.execute({
    sql: `SELECT account_id, date, balance FROM balance_history
          WHERE account_id IN (${placeholders})
          ORDER BY account_id, date ASC`,
    args: accountIds,
  });
  for (const row of res.rows as unknown as { account_id: string; date: string; balance: number }[]) {
    let list = map.get(row.account_id);
    if (!list) { list = []; map.set(row.account_id, list); }
    list.push({ date: row.date, cents: toCents(Number(row.balance)) });
  }
  return map;
}

/** Latest snapshot on or before `date`, else null. Snapshots must be date-ascending. */
export function snapshotAsOf(snapshots: Snapshot[], date: string): Snapshot | null {
  let lo = 0, hi = snapshots.length - 1, found = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (snapshots[mid].date <= date) { found = mid; lo = mid + 1; }
    else hi = mid - 1;
  }
  return found >= 0 ? snapshots[found] : null;
}

/** Included = not excluded, not archived, USD only (SPEC §9.4). */
export function isIncludedUsd(a: AccountRecord): boolean {
  return !a.excluded && a.archived_at === null && (a.currency_code ?? 'USD') === 'USD';
}

/**
 * Liability contribution rule (SPEC §9.4): Plaid liabilities keep their sign
 * (credit overpayment reduces liabilities); manual liabilities are stored as
 * positive amounts owed and contribute their absolute value.
 */
export function portfolioStateAt(
  date: string,
  accounts: AccountRecord[],
  snapshots: Map<string, Snapshot[]>,
): PortfolioState {
  let assets = 0, liabilities = 0;
  for (const a of accounts) {
    const snap = snapshotAsOf(snapshots.get(a.id) ?? [], date);
    if (!snap) continue;
    const { accountClass } = classify(a);
    if (accountClass === 'asset') assets += snap.cents;
    else liabilities -= netWorthContributionCents(a, snap.cents);
  }
  return { date, assetsCents: assets, liabilitiesCents: liabilities, netWorthCents: assets - liabilities };
}

// ── Sample-date generation (SPEC §9.6) ─────────────────────────────────────────

export type Granularity = 'day' | 'week' | 'month';

const isoDate = (d: Date): string => d.toISOString().slice(0, 10);

function addDays(iso: string, days: number): string {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return isoDate(d);
}

/** Inclusive sample dates from..to at the given granularity, capped at 500 points. */
export function sampleDates(from: string, to: string, granularity: Granularity): string[] {
  const dates: string[] = [];
  if (from > to) return dates;
  if (granularity === 'day') {
    for (let d = from; d <= to; d = addDays(d, 1)) dates.push(d);
  } else if (granularity === 'week') {
    // Each ISO week's Sunday (week end), plus `to` itself for a current point.
    let d = from;
    const dow = new Date(`${from}T00:00:00Z`).getUTCDay();
    d = addDays(from, (7 - dow) % 7);
    for (; d <= to; d = addDays(d, 7)) dates.push(d);
    if (dates[dates.length - 1] !== to) dates.push(to);
  } else {
    // Month ends, plus `to` itself.
    let cursor = new Date(`${from.slice(0, 7)}-01T00:00:00Z`);
    for (;;) {
      const monthEnd = new Date(Date.UTC(cursor.getUTCFullYear(), cursor.getUTCMonth() + 1, 0));
      const iso = isoDate(monthEnd);
      if (iso > to) break;
      if (iso >= from) dates.push(iso);
      cursor = new Date(Date.UTC(cursor.getUTCFullYear(), cursor.getUTCMonth() + 1, 1));
    }
    if (dates.length === 0 || dates[dates.length - 1] !== to) dates.push(to);
  }
  // Downsample evenly to ≤500 points, always keeping the last point.
  if (dates.length > 500) {
    const step = Math.ceil(dates.length / 500);
    const sampled = dates.filter((_, i) => i % step === 0);
    if (sampled[sampled.length - 1] !== dates[dates.length - 1]) sampled.push(dates[dates.length - 1]);
    return sampled;
  }
  return dates;
}

export type Range = '1m' | '3m' | '1y' | 'all';

export function rangeToWindow(range: Range, today: string, earliest: string | null): { from: string; to: string; granularity: Granularity } {
  switch (range) {
    case '1m': return { from: addDays(today, -30), to: today, granularity: 'day' };
    case '3m': return { from: addDays(today, -91), to: today, granularity: 'day' };
    case '1y': return { from: addDays(today, -365), to: today, granularity: 'week' };
    case 'all': return { from: earliest ?? addDays(today, -365), to: today, granularity: 'month' };
  }
}

// ── History series ─────────────────────────────────────────────────────────────

export type HistoryPointDto = {
  date: string;
  assets: MoneyDto;
  liabilities: MoneyDto;
  netWorth: MoneyDto;
  source: 'observed' | 'transactionEstimate';
};

type TransactionDelta = {
  account_id: string;
  date: string;
  cents: number;
};

export type AccountBalancePoint = {
  date: string;
  cents: number;
  source: 'observed' | 'transactionEstimate';
};

export type AccountBalanceTimeline = {
  dates: string[];
  byAccount: Map<string, AccountBalancePoint[]>;
};

/**
 * Build one account-level history model shared by every chart.
 *
 * Real balance snapshots win on and after each account's first observation.
 * Before that account-specific observation, posted Plaid transaction amounts
 * are reversed from its opening balance. This is an estimate only and is never
 * written to balance_history. Applying the rule per account is essential when
 * institutions are linked on different days: otherwise adding a later bank
 * creates a fake portfolio jump on the connection date even though Plaid has
 * enough transaction data to reconstruct the preceding part of the curve.
 */
export async function accountBalanceTimeline(
  from: string,
  to: string,
  granularity: Granularity,
  accounts: AccountRecord[],
  snapshots: Map<string, Snapshot[]>,
): Promise<AccountBalanceTimeline> {
  const firstObservedByAccount = new Map<string, string>();
  let firstPortfolioObservation: string | null = null;
  for (const account of accounts) {
    const first = snapshots.get(account.id)?.[0]?.date;
    if (!first) continue;
    firstObservedByAccount.set(account.id, first);
    if (firstPortfolioObservation === null || first < firstPortfolioObservation) {
      firstPortfolioObservation = first;
    }
  }
  if (firstPortfolioObservation === null || accounts.length === 0) {
    return { dates: [], byAccount: new Map() };
  }

  const placeholders = accounts.map(() => '?').join(',');
  const res = await db.execute({
    sql: `SELECT account_id, date, amount
          FROM transactions
          WHERE account_id IN (${placeholders})
            AND pending = 0 AND date >= ? AND date <= ?
          ORDER BY date ASC`,
    args: [...accounts.map((a) => a.id), from, to],
  });
  const transactions = (res.rows as unknown as { account_id: string; date: string; amount: number }[])
    .map((row): TransactionDelta => ({
      account_id: row.account_id,
      date: row.date,
      cents: toCents(Number(row.amount)),
    }))
    .filter((tx) => tx.date < (firstObservedByAccount.get(tx.account_id) ?? '0000-00-00'));

  const earliestTransaction = transactions[0]?.date;
  // Include one point immediately before the oldest available transaction.
  // Starting on the transaction date would absorb that transaction into the
  // baseline because posted balances change after it, making the oldest real
  // movement disappear from every normalized contribution chart.
  const estimateAnchor = earliestTransaction ? addDays(earliestTransaction, -1) : null;
  const start = estimateAnchor && estimateAnchor < firstPortfolioObservation
    ? (from > estimateAnchor ? from : estimateAnchor)
    : (from > firstPortfolioObservation ? from : firstPortfolioObservation);
  const dates = sampleDates(start, to, granularity);
  if (start <= to && !dates.includes(start)) dates.unshift(start);
  const byAccount = new Map<string, AccountBalancePoint[]>();
  const transactionsByAccount = new Map<string, TransactionDelta[]>();
  for (const tx of transactions) {
    const list = transactionsByAccount.get(tx.account_id) ?? [];
    list.push(tx);
    transactionsByAccount.set(tx.account_id, list);
  }

  for (const account of accounts) {
    const list = snapshots.get(account.id) ?? [];
    const firstObserved = firstObservedByAccount.get(account.id);
    if (!firstObserved || list.length === 0) continue;
    const accountTransactions = transactionsByAccount.get(account.id) ?? [];
    // Never backfill a newly discovered account as a flat balance merely
    // because another account made the portfolio timeline start earlier.
    // A pre-observation estimate is justified only when this specific Plaid
    // account has posted transactions that can anchor the reconstruction.
    const canEstimate = account.source === 'plaid' && accountTransactions.length > 0;
    const accountClass = classify(account).accountClass;
    const points: AccountBalancePoint[] = [];
    for (const date of dates) {
      const observed = snapshotAsOf(list, date);
      if (observed) {
        points.push({ date, cents: observed.cents, source: 'observed' });
        continue;
      }
      if (!canEstimate || date >= firstObserved) continue;
      let cents = list[0].cents;
      for (const tx of accountTransactions) {
        if (tx.date <= date || tx.date >= firstObserved) continue;
        cents += accountClass === 'liability' ? -tx.cents : tx.cents;
      }
      points.push({ date, cents, source: 'transactionEstimate' });
    }
    byAccount.set(account.id, points);
  }
  return { dates, byAccount };
}

export async function netWorthHistory(
  from: string,
  to: string,
  granularity: Granularity,
): Promise<HistoryPointDto[]> {
  const accounts = (await loadAccounts()).filter(isIncludedUsd);
  const snapshots = await loadSnapshots(accounts.map((a) => a.id));

  const timeline = await accountBalanceTimeline(from, to, granularity, accounts, snapshots);
  return timeline.dates.map((date): HistoryPointDto => {
    let assetsCents = 0;
    let liabilitiesCents = 0;
    let estimated = false;
    for (const account of accounts) {
      const point = timeline.byAccount.get(account.id)?.find((candidate) => candidate.date === date);
      if (!point) continue;
      estimated ||= point.source === 'transactionEstimate';
      if (classify(account).accountClass === 'liability') {
        liabilitiesCents -= netWorthContributionCents(account, point.cents);
      } else {
        assetsCents += point.cents;
      }
    }
    return {
      date,
      assets: money(assetsCents),
      liabilities: money(liabilitiesCents),
      netWorth: money(assetsCents - liabilitiesCents),
      source: estimated ? 'transactionEstimate' : 'observed',
    };
  });
}

// ── Current state + change (SPEC §9.7) ─────────────────────────────────────────

export type ChangeDto = {
  amount: string;
  ratio: string | null;
  comparison: 'yesterday' | 'previousRecord';
  comparedTo: string; // the date of the previous state
} | null;

export type SummaryDto = {
  assets: MoneyDto;
  liabilities: MoneyDto;
  netWorth: MoneyDto;
  change: ChangeDto;
  asOfDate: string | null;
};

/** Distinct snapshot dates across included accounts, ascending. */
function distinctDates(snapshots: Map<string, Snapshot[]>): string[] {
  const set = new Set<string>();
  for (const list of snapshots.values()) for (const s of list) set.add(s.date);
  return [...set].sort();
}

export async function currentSummary(): Promise<SummaryDto> {
  const accounts = (await loadAccounts()).filter(isIncludedUsd);
  const snapshots = await loadSnapshots(accounts.map((a) => a.id));
  const dates = distinctDates(snapshots);
  if (dates.length === 0) {
    return { assets: money(0), liabilities: money(0), netWorth: money(0), change: null, asOfDate: null };
  }
  const latest = dates[dates.length - 1];
  const current = portfolioStateAt(latest, accounts, snapshots);

  let change: ChangeDto = null;
  if (dates.length >= 2) {
    const prevDate = dates[dates.length - 2];
    // Aggregate change compares two complete as-of portfolio states (SPEC §9.7).
    const previous = portfolioStateAt(prevDate, accounts, snapshots);
    const delta = current.netWorthCents - previous.netWorthCents;
    change = {
      amount: centsToAmount(delta),
      ratio: ratio(delta, previous.netWorthCents),
      comparison: prevDate === addDays(latest, -1) ? 'yesterday' : 'previousRecord',
      comparedTo: prevDate,
    };
  }

  return {
    assets: money(current.assetsCents),
    liabilities: money(current.liabilitiesCents),
    netWorth: money(current.netWorthCents),
    change,
    asOfDate: latest,
  };
}

// ── Allocation (SPEC §9.8) ─────────────────────────────────────────────────────

export type AllocationSliceDto = {
  bucket: AllocationBucket;
  amount: MoneyDto;
  share: string | null; // decimal string 0..1, 4 places
  accountIds: string[];
};

export async function allocation(): Promise<{ totalAssets: MoneyDto; slices: AllocationSliceDto[] }> {
  const accounts = (await loadAccounts()).filter(isIncludedUsd);
  const snapshots = await loadSnapshots(accounts.map((a) => a.id));

  const buckets = new Map<AllocationBucket, { cents: number; accountIds: string[] }>();
  let total = 0;
  for (const a of accounts) {
    const list = snapshots.get(a.id) ?? [];
    if (list.length === 0) continue;
    const { category, accountClass } = classify(a);
    if (accountClass !== 'asset') continue; // assets only in the donut
    const cents = list[list.length - 1].cents;
    if (cents <= 0) continue;
    const bucket = allocationBucket(category);
    const entry = buckets.get(bucket) ?? { cents: 0, accountIds: [] };
    entry.cents += cents;
    entry.accountIds.push(a.id);
    buckets.set(bucket, entry);
    total += cents;
  }

  const order: AllocationBucket[] = ['bankCash', 'investment', 'retirement', 'property', 'vehicle', 'other'];
  const slices: AllocationSliceDto[] = order
    .filter((b) => buckets.has(b))
    .map((b) => {
      const e = buckets.get(b)!;
      return {
        bucket: b,
        amount: money(e.cents),
        share: total > 0 ? (e.cents / total).toFixed(4) : null,
        accountIds: e.accountIds,
      };
    });
  return { totalAssets: money(total), slices };
}

// ── Per-account current view (SPEC §9.2, §9.7, §11.4) ──────────────────────────

export type Freshness = 'never' | 'fresh' | 'stale' | 'very_stale' | 'attention' | 'manual';

export type AccountSummaryDto = {
  id: string;
  displayName: string;
  institutionName: string | null;
  nickname: string | null;
  owner: string | null;
  mask: string | null;
  source: string;
  providerSources: string[];
  accountClass: 'asset' | 'liability';
  category: string;
  type: string;
  subtype: string | null;
  /** Which card a credit account is, as chosen in the app or detected; null when not identified. */
  cardProduct: CardProductRef | null;
  currencyCode: string;
  balance: { amount: string; currencyCode: string; asOfDate: string } | null;
  change: { amount: string; ratio: string | null; comparison: 'yesterday' | 'previousRecord'; comparedTo: string } | null;
  sparkline: { date: string; amount: string }[];
  excluded: boolean;
  archived: boolean;
  freshness: Freshness;
  lastSyncedAt: string | null;
  attention: string | null;
};

type ItemStatus = { lastSyncedAt: number | null; status: string | null; lastErrorCode: string | null; lastSuccessAt: string | null };

async function loadItemStatuses(): Promise<Map<string, ItemStatus>> {
  const res = await db.execute(
    'SELECT item_id, last_synced_at, status, last_error_code, last_success_at FROM plaid_items',
  );
  const map = new Map<string, ItemStatus>();
  for (const r of res.rows as unknown as { item_id: string; last_synced_at: number | null; status: string | null; last_error_code: string | null; last_success_at: string | null }[]) {
    map.set(r.item_id, {
      lastSyncedAt: r.last_synced_at === null ? null : Number(r.last_synced_at),
      status: r.status,
      lastErrorCode: r.last_error_code,
      lastSuccessAt: r.last_success_at,
    });
  }
  return map;
}

function freshnessFor(a: AccountRecord, item: ItemStatus | undefined, latestSnapshotDate: string | null, nowMs: number): { freshness: Freshness; lastSyncedAt: string | null; attention: string | null } {
  if (a.source === 'manual') {
    return {
      freshness: 'manual',
      lastSyncedAt: latestSnapshotDate,
      attention: null,
    };
  }
  if (item?.status === 'attention' || item?.lastErrorCode) {
    return { freshness: 'attention', lastSyncedAt: msToIso(item?.lastSyncedAt ?? null), attention: item?.lastErrorCode ?? 'ATTENTION' };
  }
  const ms = item?.lastSyncedAt ?? null;
  if (ms === null) {
    return latestSnapshotDate === null
      ? { freshness: 'never', lastSyncedAt: null, attention: null }
      : { freshness: 'very_stale', lastSyncedAt: null, attention: null };
  }
  const ageHours = (nowMs - ms) / 3_600_000;
  const freshness: Freshness = ageHours <= 24 ? 'fresh' : ageHours <= 24 * 7 ? 'stale' : 'very_stale';
  return { freshness, lastSyncedAt: msToIso(ms), attention: null };
}

const msToIso = (ms: number | null): string | null =>
  ms === null ? null : new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z');

const SPARKLINE_DAYS = 30;

export async function accountSummaries(opts: {
  includeExcluded?: boolean;
  includeArchived?: boolean;
} = {}): Promise<AccountSummaryDto[]> {
  const accounts = await loadAccounts({ includeExcluded: true, includeArchived: opts.includeArchived ?? false });
  const visible = opts.includeExcluded === false ? accounts.filter((a) => !a.excluded) : accounts;
  const snapshots = await loadSnapshots(visible.map((a) => a.id));
  const providerSources = new Map<string, string[]>();
  if (visible.length > 0) {
    const placeholders = visible.map(() => '?').join(',');
    const links = await db.execute({
      sql: `SELECT account_id, provider FROM account_provider_links
            WHERE account_id IN (${placeholders}) ORDER BY provider`,
      args: visible.map(account => account.id),
    });
    for (const row of links.rows as unknown as { account_id: string; provider: string }[]) {
      const sources = providerSources.get(row.account_id) ?? [];
      if (!sources.includes(row.provider)) sources.push(row.provider);
      providerSources.set(row.account_id, sources);
    }
  }
  const items = await loadItemStatuses();
  const identities = await accountIdentities(visible);
  const nowMs = Date.now();
  const today = new Date().toISOString().slice(0, 10);
  const sparkFrom = addDays(today, -SPARKLINE_DAYS);
  // Account detail charts use the same timeline as Home and Trends. Keeping a
  // separate raw-snapshot sparkline produced flat two-point account charts even
  // while the portfolio chart correctly used Plaid transaction estimates.
  const sparkTimeline = await accountBalanceTimeline(
    sparkFrom, today, 'day', visible, snapshots,
  );

  return visible.map((a) => {
    const list = snapshots.get(a.id) ?? [];
    const latest = list.length ? list[list.length - 1] : null;
    const prev = list.length >= 2 ? list[list.length - 2] : null;
    const { category, accountClass } = classify(a);
    const contribution = (cents: number) => netWorthContributionCents(a, cents);
    const item = a.item_id ? items.get(a.item_id) : undefined;
    const fresh = freshnessFor(a, item, latest?.date ?? null, nowMs);

    const sparkline = (sparkTimeline.byAccount.get(a.id) ?? [])
      .map((point) => ({
        date: point.date,
        amount: centsToAmount(netWorthContributionCents(a, point.cents)),
      }));

    return {
      id: a.id,
      displayName: identities.get(a.id)!.displayName,
      institutionName: a.institution_name,
      nickname: a.nickname,
      owner: a.owner,
      mask: a.mask,
      source: a.source,
      providerSources: [...new Set([a.source, ...(providerSources.get(a.id) ?? [])])],
      accountClass,
      category,
      type: a.type,
      subtype: a.subtype,
      cardProduct: identities.get(a.id)!.cardProduct,
      currencyCode: a.currency_code ?? 'USD',
      balance: latest
        ? { amount: centsToAmount(contribution(latest.cents)), currencyCode: a.currency_code ?? 'USD', asOfDate: latest.date }
        : null,
      change: latest && prev
        ? {
            amount: centsToAmount(contribution(latest.cents) - contribution(prev.cents)),
            ratio: ratio(contribution(latest.cents) - contribution(prev.cents), contribution(prev.cents)),
            comparison: prev.date === addDays(latest.date, -1) ? 'yesterday' : 'previousRecord',
            comparedTo: prev.date,
          }
        : null,
      sparkline,
      excluded: a.excluded,
      archived: a.archived_at !== null,
      freshness: fresh.freshness,
      lastSyncedAt: fresh.lastSyncedAt,
      attention: fresh.attention,
    };
  });
}

export { addDays };
