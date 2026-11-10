import { createHash, randomUUID } from 'node:crypto';
import { db } from '../core/database.js';
import { ApiError } from '../errors.js';
import { log } from '../log.js';
import { requireSnapTradeGateway } from './snaptrade-config.js';
import type {
  SnapTradeBalanceHistory,
  SnapTradeGateway,
  SnapTradeRecord,
} from './snaptrade-client.js';

export type InvestmentSyncTrigger = 'initial' | 'launch' | 'manual' | 'scheduled' | 'portal-return' | 'retry';

export type InvestmentSyncRun = {
  id: string;
  trigger: InvestmentSyncTrigger;
  status: 'running' | 'succeeded' | 'failed';
  startedAt: string;
  completedAt: string | null;
  connectionCount: number;
  accountCount: number;
  positionCount: number;
  activityCount: number;
  orderCount: number;
  errorCode: string | null;
};

type RemoteAccountSnapshot = {
  account: SnapTradeRecord;
  positions: SnapTradeRecord[];
  positionAsOf: string | null;
  balances: SnapTradeRecord[];
  activities: SnapTradeRecord[];
  orders: SnapTradeRecord[];
  balanceHistory: SnapTradeBalanceHistory | null;
};

export type ExistingProviderAccount = {
  id: string;
  source: string | null;
  institution_name: string | null;
  mask: string | null;
  type: string;
  subtype: string | null;
  latest_balance: number | null;
};

export type RemoteProviderIdentity = {
  institutionName: string;
  mask: string | null;
  type: string;
  subtype: string;
  accountClass: string;
  total: number | null;
};

let activeRun: Promise<InvestmentSyncRun> | null = null;

const record = (value: unknown): SnapTradeRecord =>
  value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as SnapTradeRecord : {};

const text = (value: unknown): string | null =>
  typeof value === 'string' && value.trim() ? value.trim() : null;

const bool = (value: unknown): boolean => value === true;

function number(value: unknown): number | null {
  const parsed = typeof value === 'number' ? value : typeof value === 'string' ? Number(value) : NaN;
  return Number.isFinite(parsed) ? parsed : null;
}

function decimal(value: unknown, places = 8): string | null {
  const parsed = number(value);
  if (parsed === null) return null;
  const fixed = parsed.toFixed(places);
  return fixed.includes('.') ? fixed.replace(/0+$/, '').replace(/\.$/, '') : fixed;
}

function historyDate(value: unknown): string | null {
  const raw = text(value);
  if (!raw) return null;
  const date = raw.slice(0, 10);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return null;
  const parsed = new Date(`${date}T00:00:00Z`);
  return Number.isFinite(parsed.valueOf()) && parsed.toISOString().slice(0, 10) === date
    ? date
    : null;
}

const stableId = (...parts: (string | null)[]): string =>
  createHash('sha256').update(parts.map((p) => p ?? '').join('\u001f')).digest('hex').slice(0, 32);

const normalizedInstitution = (value: string): string =>
  value.toLowerCase().replace(/[^a-z0-9]/g, '');

const normalizedSubtype = (value: string | null): string =>
  (value ?? '').toLowerCase().replace(/[^a-z0-9]/g, '');

const canonicalBalance = (accountClass: string, value: number | null): number | null =>
  value === null || !Number.isFinite(value)
    ? null
    : accountClass === 'liability' ? Math.abs(value) : value;

/**
 * Resolve a provider account to one authoritative local account. Provider
 * account numbers often differ, so mask matching alone is insufficient. A
 * semantic fallback is allowed only for a unique Plaid candidate with the
 * same institution, account type/subtype and current balance. Ambiguous
 * candidates intentionally remain separate.
 */
export function matchCanonicalProviderAccount(
  identity: RemoteProviderIdentity,
  accounts: ExistingProviderAccount[],
): ExistingProviderAccount | undefined {
  const institution = normalizedInstitution(identity.institutionName);
  const plaidCandidates = accounts.filter((account) =>
    account.source === 'plaid'
    && account.type === identity.type
    && normalizedInstitution(account.institution_name ?? '') === institution);

  if (identity.mask) {
    const exactMask = plaidCandidates.filter((account) =>
      account.mask !== null && String(account.mask) === identity.mask);
    if (exactMask.length === 1) return exactMask[0];
  }

  const subtype = normalizedSubtype(identity.subtype);
  const remoteBalance = canonicalBalance(identity.accountClass, identity.total);
  if (!subtype || remoteBalance === null) return undefined;
  const semanticMatches = plaidCandidates.filter((account) =>
    normalizedSubtype(account.subtype) === subtype
    && account.latest_balance !== null
    && Math.abs(canonicalBalance(identity.accountClass, Number(account.latest_balance))! - remoteBalance) <= 0.01);
  return semanticMatches.length === 1 ? semanticMatches[0] : undefined;
}

export function providerBalanceForStorage(accountClass: string, value: number): number {
  return accountClass === 'liability' ? Math.abs(value) : value;
}

function instrumentFields(position: SnapTradeRecord): {
  providerId: string | null; symbol: string; name: string; kind: string;
} {
  const instrument = record(position.instrument);
  const providerId = text(instrument.id);
  const symbol = text(instrument.symbol) ?? text(instrument.raw_symbol) ?? providerId ?? 'UNKNOWN';
  return {
    providerId,
    symbol,
    name: text(instrument.description) ?? symbol,
    kind: text(instrument.kind) ?? 'other',
  };
}

function activitySymbol(activity: SnapTradeRecord): string | null {
  const symbol = record(activity.symbol);
  const nested = record(symbol.symbol);
  return text(nested.symbol) ?? text(symbol.symbol) ?? text(symbol.raw_symbol) ?? null;
}

function currencyCode(value: unknown, fallback = 'USD'): string {
  if (typeof value === 'string' && /^[A-Za-z]{3}$/.test(value)) return value.toUpperCase();
  const currency = record(value);
  const code = text(currency.code);
  return code && /^[A-Za-z]{3}$/.test(code) ? code.toUpperCase() : fallback;
}

function accountIdentity(remote: SnapTradeRecord) {
  const providerId = text(remote.id);
  if (!providerId) throw new ApiError('DATA_CORRUPT', 'SnapTrade returned an account without an ID.');
  const category = text(remote.account_category) ?? 'INVESTMENT';
  const connectionId = text(remote.brokerage_authorization);
  const accountId = `snaptrade-${providerId}`;
  const accountNumber = text(remote.number);
  const sync = record(remote.sync_status);
  const holdings = record(sync.holdings);
  const archived = ['closed', 'archived', 'unavailable'].includes(text(remote.status) ?? '');
  return {
    providerId,
    institutionAccountId: text(remote.institution_account_id),
    connectionId,
    accountId,
    name: text(remote.name) ?? text(remote.institution_name) ?? 'Investment account',
    institutionName: text(remote.institution_name) ?? 'SnapTrade',
    mask: accountNumber ? accountNumber.slice(-4) : null,
    type: category === 'DEPOSIT' ? 'depository' : category === 'LOC' ? 'credit' : 'investment',
    subtype: text(remote.raw_type) ?? (category === 'INVESTMENT' ? 'brokerage' : category.toLowerCase()),
    accountClass: category === 'LOC' ? 'liability' : 'asset',
    archived,
    createdAt: text(remote.created_date),
    lastSyncedAt: text(holdings.last_successful_sync),
    total: number(record(record(remote.balance).total).amount),
    totalCurrency: currencyCode(record(record(remote.balance).total).currency),
  };
}

async function fetchSnapshot(gateway: SnapTradeGateway): Promise<{
  connections: SnapTradeRecord[]; accounts: RemoteAccountSnapshot[];
}> {
  const [connections, remoteAccounts] = await Promise.all([
    gateway.listConnections(),
    gateway.listAccounts(),
  ]);
  const accounts: RemoteAccountSnapshot[] = [];
  // Account-level requests are deliberately sequential. This is a desktop app
  // with few accounts, and staying below provider burst limits is preferable to
  // shaving a second off a background sync.
  for (const account of remoteAccounts) {
    const accountId = text(account.id);
    if (!accountId) throw new ApiError('DATA_CORRUPT', 'SnapTrade returned an account without an ID.');
    const optionalHistory = gateway.listBalanceHistory(accountId).catch((error) => {
      // Current holdings remain useful if this experimental enrichment is
      // disabled, malformed at the provider, rate-limited, or temporarily down.
      const code = error instanceof ApiError ? error.code : 'INTERNAL_ERROR';
      log('warn', `SnapTrade balance history unavailable for account ${accountId}; code=${code}`);
      return null;
    });
    const [positions, balances, activities, orders, balanceHistory] = await Promise.all([
      gateway.listPositions(accountId),
      gateway.listBalances(accountId),
      gateway.listActivities(accountId),
      gateway.listOrders(accountId),
      optionalHistory,
    ]);
    accounts.push({
      account,
      positions: positions.results,
      positionAsOf: positions.asOf,
      balances,
      activities,
      orders,
      balanceHistory,
    });
  }
  return { connections, accounts };
}

async function commitSnapshot(
  runId: string,
  snapshot: { connections: SnapTradeRecord[]; accounts: RemoteAccountSnapshot[] },
): Promise<InvestmentSyncRun> {
  const now = new Date().toISOString();
  const today = now.slice(0, 10);
  const existingResult = await db.execute(`
    SELECT a.id, a.source, a.institution_name, a.mask, a.type, a.subtype,
           (SELECT bh.balance FROM balance_history bh
             WHERE bh.account_id = a.id ORDER BY bh.date DESC LIMIT 1) AS latest_balance
      FROM accounts a WHERE a.archived_at IS NULL`);
  const existingAccounts = existingResult.rows as unknown as ExistingProviderAccount[];
  const existingById = new Map(existingAccounts.map((account) => [account.id, account]));
  const linksResult = await db.execute(
    "SELECT provider_account_id, account_id FROM account_provider_links WHERE provider = 'snaptrade'",
  );
  const linkedAccount = new Map((linksResult.rows as unknown as {
    provider_account_id: string; account_id: string;
  }[]).map((link) => [link.provider_account_id, link.account_id]));

  const statements: { sql: string; args: (string | number | null)[] }[] = [
    { sql: 'DELETE FROM snaptrade_connections', args: [] },
    { sql: `UPDATE accounts SET archived_at = ?, updated_at = ? WHERE source = 'snaptrade'`, args: [now, now] },
    { sql: `DELETE FROM investment_positions WHERE source = 'snaptrade'`, args: [] },
    { sql: `DELETE FROM investment_balances WHERE source = 'snaptrade'`, args: [] },
    { sql: `DELETE FROM investment_activities WHERE source = 'snaptrade'`, args: [] },
    { sql: `DELETE FROM investment_orders WHERE source = 'snaptrade'`, args: [] },
    { sql: `DELETE FROM account_provider_links WHERE provider = 'snaptrade'`, args: [] },
  ];

  for (const connection of snapshot.connections) {
    const connectionId = text(connection.id);
    if (!connectionId) throw new ApiError('DATA_CORRUPT', 'SnapTrade returned a connection without an ID.');
    const brokerage = record(connection.brokerage);
    statements.push({
      sql: `INSERT INTO snaptrade_connections (
              connection_id, brokerage_id, brokerage_slug, brokerage_name,
              brokerage_logo_url, display_name, connection_type, disabled,
              disabled_at, created_at, remote_updated_at, last_success_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      args: [
        connectionId, text(brokerage.id), text(brokerage.slug),
        text(brokerage.display_name) ?? text(brokerage.name) ?? text(connection.name) ?? 'Brokerage',
        text(brokerage.aws_s3_square_logo_url) ?? text(brokerage.aws_s3_logo_url),
        text(connection.name), text(connection.type) ?? 'read', bool(connection.disabled) ? 1 : 0,
        text(connection.disabled_date), text(connection.created_date), text(connection.updated_date), now,
      ],
    });
  }

  let positionCount = 0;
  let activityCount = 0;
  let orderCount = 0;
  for (const snapshotAccount of snapshot.accounts) {
    const identity = accountIdentity(snapshotAccount.account);
    const previousLink = linkedAccount.get(identity.providerId);
    const linked = previousLink ? existingById.get(previousLink) : undefined;
    // A link to an authoritative Plaid account always wins. Links created by
    // older builds to provider-only duplicates may migrate when a unique
    // semantic Plaid match is now available.
    const canonical = matchCanonicalProviderAccount(identity, existingAccounts);
    const matched = linked && linked.source !== 'snaptrade' ? linked : canonical ?? linked;
    const localAccountId = matched?.id ?? identity.accountId;
    let computedTotal = 0;

    // A brokerage account first discovered through Plaid hands its balance
    // series over once SnapTrade syncs it: SnapTrade reports positions plus the
    // cash sweep, which Plaid's account balance omits. Ownership moves before
    // any snapshot below is written, and only ever toward the better source.
    if (matched?.source === 'plaid' && identity.type === 'investment') {
      statements.push({
        sql: `UPDATE accounts SET balance_source = 'snaptrade'
               WHERE id = ? AND COALESCE(balance_source, 'plaid') <> 'snaptrade'`,
        args: [localAccountId],
      });
    }

    if (matched?.source === 'snaptrade' || matched === undefined) {
      statements.push({
        sql: `INSERT INTO accounts (
              id, name, type, subtype, institution_name, mask, source,
              account_class, currency_code, excluded, archived_at, created_at,
              updated_at, provider_account_id, provider_connection_id, balance_source)
            VALUES (?, ?, ?, ?, ?, ?, 'snaptrade', ?, ?, 0, ?, ?, ?, ?, ?, 'snaptrade')
            ON CONFLICT(id) DO UPDATE SET
              name = excluded.name,
              type = excluded.type,
              subtype = excluded.subtype,
              institution_name = excluded.institution_name,
              mask = excluded.mask,
              source = 'snaptrade',
              account_class = excluded.account_class,
              currency_code = excluded.currency_code,
              archived_at = excluded.archived_at,
              updated_at = excluded.updated_at,
              provider_account_id = excluded.provider_account_id,
              provider_connection_id = excluded.provider_connection_id,
              balance_source = COALESCE(accounts.balance_source, 'snaptrade')`,
        args: [
        localAccountId, identity.name, identity.type, identity.subtype,
        identity.institutionName, identity.mask, identity.accountClass, identity.totalCurrency,
        identity.archived ? now : null, identity.createdAt ?? now, now,
        identity.providerId, identity.connectionId,
        ],
      });
    }
    statements.push({
      sql: `INSERT INTO account_provider_links
              (provider, provider_account_id, account_id, provider_connection_id,
               institution_account_id, last_seen_at)
            VALUES ('snaptrade', ?, ?, ?, ?, ?)`,
      args: [identity.providerId, localAccountId, identity.connectionId, identity.institutionAccountId, now],
    });

    for (const balance of snapshotAccount.balances) {
      const code = currencyCode(balance.currency, identity.totalCurrency);
      const cash = number(balance.cash);
      const buyingPower = number(balance.buying_power);
      if (code === identity.totalCurrency && cash !== null) computedTotal += cash;
      statements.push({
        sql: `INSERT INTO investment_balances
                (account_id, currency_code, source, cash, buying_power, as_of_at)
              VALUES (?, ?, 'snaptrade', ?, ?, ?)`,
        args: [localAccountId, code, decimal(cash), decimal(buyingPower), now],
      });
    }

    for (const position of snapshotAccount.positions) {
      const instrument = instrumentFields(position);
      const units = number(position.units);
      const price = number(position.price);
      const average = number(position.cost_basis);
      const marketValue = units !== null && price !== null ? units * price : null;
      const gainLoss = units !== null && price !== null && average !== null
        ? units * (price - average) : null;
      const cashEquivalent = bool(position.cash_equivalent);
      if (!cashEquivalent && marketValue !== null) computedTotal += marketValue;
      statements.push({
        sql: `INSERT INTO investment_positions (
                id, account_id, source, provider_position_id, symbol, name,
                instrument_type, units, price, average_price, market_value,
                gain_loss, currency_code, cash_equivalent, as_of_at)
              VALUES (?, ?, 'snaptrade', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        args: [
          `position-${stableId(identity.providerId, instrument.providerId, instrument.symbol)}`,
          localAccountId, instrument.providerId, instrument.symbol, instrument.name,
          instrument.kind, decimal(units), decimal(price), decimal(average),
          decimal(marketValue), decimal(gainLoss), currencyCode(position.currency, identity.totalCurrency),
          cashEquivalent ? 1 : 0, snapshotAccount.positionAsOf ?? now,
        ],
      });
      positionCount++;
    }

    for (const activity of snapshotAccount.activities) {
      const remoteId = text(activity.id);
      const activityType = text(activity.type) ?? 'OTHER';
      const tradeDate = text(activity.trade_date);
      const symbol = activitySymbol(activity);
      const id = stableId(identity.providerId, remoteId ?? stableId(
        identity.providerId, tradeDate, activityType, symbol,
        decimal(activity.units), decimal(activity.amount), text(activity.description),
      ));
      statements.push({
        sql: `INSERT INTO investment_activities (
                id, account_id, source, activity_type, trade_date, settlement_date,
                symbol, description, units, price, amount, fee, currency_code,
                external_reference_id, updated_at)
              VALUES (?, ?, 'snaptrade', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        args: [
          `activity-${id}`, localAccountId, activityType, tradeDate,
          text(activity.settlement_date), symbol, text(activity.description),
          decimal(activity.units), decimal(activity.price), decimal(activity.amount), decimal(activity.fee),
          currencyCode(activity.currency, identity.totalCurrency), text(activity.external_reference_id), now,
        ],
      });
      activityCount++;
    }

    for (const order of snapshotAccount.orders) {
      const universalSymbol = record(order.universal_symbol);
      const optionSymbol = record(order.option_symbol);
      const quoteSymbol = record(order.quote_universal_symbol);
      const symbol = text(universalSymbol.symbol)
        ?? text(optionSymbol.ticker)
        ?? text(optionSymbol.symbol)
        ?? text(quoteSymbol.symbol)
        ?? text(order.symbol);
      const brokerageOrderId = text(order.brokerage_order_id);
      const orderId = stableId(localAccountId, brokerageOrderId, text(order.time_placed), symbol);
      statements.push({
        sql: `INSERT INTO investment_orders (
                id, account_id, source, brokerage_order_id, status, action,
                symbol, order_type, time_in_force, total_quantity,
                open_quantity, filled_quantity, execution_price, limit_price,
                stop_price, time_placed, time_updated, time_executed, expiry_date)
              VALUES (?, ?, 'snaptrade', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        args: [
          orderId, localAccountId, brokerageOrderId, text(order.status) ?? 'NONE',
          text(order.action), symbol, text(order.order_type), text(order.time_in_force),
          decimal(order.total_quantity), decimal(order.open_quantity), decimal(order.filled_quantity),
          decimal(order.execution_price), decimal(order.limit_price), decimal(order.stop_price),
          text(order.time_placed), text(order.time_updated), text(order.time_executed), text(order.expiry_date),
        ],
      });
      orderCount++;
    }

    // SnapTrade's optional estimated history is deliberately NOT imported.
    //
    // The series it returns for a brokerage account interleaves two different
    // valuations of the same account on alternating dates (observed: ~$38.1k and
    // ~$45.6k for the same week). Because the whole series is rewritten on every
    // sync, importing it fabricated a permanent net-worth sawtooth that no
    // amount of repairing the stored history could survive — the next sync put
    // it straight back.
    //
    // Only the account's current daily value is recorded, below. History
    // therefore accrues from the days AssetScope actually observed, which is
    // slower to build but is never invented.

    // The regular daily account value is generally fresher than the
    // experimental estimated series. Add it last so today's value wins.
    const total = providerBalanceForStorage(identity.accountClass, identity.total ?? computedTotal);
    statements.push({
      // Guarded for the same reason as the estimated series above: a Plaid-owned
      // account must not receive a second provider's idea of its balance.
      sql: `INSERT INTO balance_history (account_id, balance, date)
            SELECT ?, ?, ? WHERE EXISTS (
              SELECT 1 FROM accounts WHERE id = ? AND balance_source = 'snaptrade')
            ON CONFLICT(account_id, date) DO UPDATE SET balance = excluded.balance`,
      args: [localAccountId, Number(total.toFixed(2)), today, localAccountId],
    });
  }

  statements.push({
    sql: `UPDATE investment_sync_runs SET
            status = 'succeeded', completed_at = ?, connection_count = ?,
            account_count = ?, position_count = ?, activity_count = ?, order_count = ?,
            error_code = NULL, error_message_redacted = NULL
          WHERE id = ?`,
    args: [
      now, snapshot.connections.length, snapshot.accounts.length,
      positionCount, activityCount, orderCount, runId,
    ],
  });

  // @libsql batch(..., 'write') is one transaction: either every provider row,
  // account value, and success marker lands, or none of them does.
  await db.batch(statements, 'write');
  return {
    id: runId,
    trigger: 'manual', // replaced from the persisted row below
    status: 'succeeded',
    startedAt: '',
    completedAt: now,
    connectionCount: snapshot.connections.length,
    accountCount: snapshot.accounts.length,
    positionCount,
    activityCount,
    orderCount,
    errorCode: null,
  };
}

function mapRun(row: Record<string, unknown>): InvestmentSyncRun {
  return {
    id: String(row.id),
    trigger: row.trigger as InvestmentSyncTrigger,
    status: row.status as InvestmentSyncRun['status'],
    startedAt: String(row.started_at),
    completedAt: text(row.completed_at),
    connectionCount: Number(row.connection_count ?? 0),
    accountCount: Number(row.account_count ?? 0),
    positionCount: Number(row.position_count ?? 0),
    activityCount: Number(row.activity_count ?? 0),
    orderCount: Number(row.order_count ?? 0),
    errorCode: text(row.error_code),
  };
}

export async function getInvestmentSyncRun(runId: string): Promise<InvestmentSyncRun | null> {
  const result = await db.execute({ sql: 'SELECT * FROM investment_sync_runs WHERE id = ?', args: [runId] });
  return result.rows.length ? mapRun(result.rows[0] as unknown as Record<string, unknown>) : null;
}

export async function latestInvestmentSyncRun(): Promise<InvestmentSyncRun | null> {
  const result = await db.execute('SELECT * FROM investment_sync_runs ORDER BY started_at DESC LIMIT 1');
  return result.rows.length ? mapRun(result.rows[0] as unknown as Record<string, unknown>) : null;
}

export async function recoverInterruptedInvestmentSyncRuns(): Promise<number> {
  const result = await db.execute("SELECT id FROM investment_sync_runs WHERE status = 'running'");
  const rows = result.rows as unknown as { id: string }[];
  if (rows.length === 0) return 0;
  const now = new Date().toISOString();
  await db.batch(rows.map((row) => ({
    sql: `UPDATE investment_sync_runs
          SET status = 'failed', completed_at = ?, error_code = 'INTERRUPTED',
              error_message_redacted = 'The app stopped before the sync completed.'
          WHERE id = ?`,
    args: [now, row.id],
  })), 'write');
  return rows.length;
}

async function beginSnapTradeSync(
  trigger: InvestmentSyncTrigger,
  gateway: SnapTradeGateway,
): Promise<{ initial: InvestmentSyncRun; completion: Promise<InvestmentSyncRun> }> {
  if (activeRun !== null) throw new ApiError('SYNC_IN_PROGRESS', 'An investment sync is already running.');
  const runId = randomUUID();
  const startedAt = new Date().toISOString();
  await db.execute({
    sql: `INSERT INTO investment_sync_runs (id, trigger, status, started_at)
          VALUES (?, ?, 'running', ?)`,
    args: [runId, trigger, startedAt],
  });

  const completion = (async () => {
    try {
      log('info', `SnapTrade sync ${runId} started; trigger=${trigger}`);
      const snapshot = await fetchSnapshot(gateway);
      await commitSnapshot(runId, snapshot);
      const completed = await getInvestmentSyncRun(runId);
      if (!completed) throw new ApiError('DATA_CORRUPT', 'Investment sync result was not persisted.');
      log(
        'info',
        `SnapTrade sync ${runId} succeeded; accounts=${completed.accountCount}, positions=${completed.positionCount}, orders=${completed.orderCount}`,
      );
      return completed;
    } catch (error) {
      const apiError = error instanceof ApiError
        ? error : new ApiError('INTERNAL_ERROR', 'Investment sync failed.', { retryable: true });
      await db.execute({
        sql: `UPDATE investment_sync_runs
              SET status = 'failed', completed_at = ?, error_code = ?, error_message_redacted = ?
              WHERE id = ?`,
        args: [new Date().toISOString(), apiError.code, apiError.message.slice(0, 200), runId],
      });
      log('warn', `SnapTrade sync ${runId} failed; code=${apiError.code}`);
      throw apiError;
    } finally {
      activeRun = null;
    }
  })();
  activeRun = completion;
  return {
    initial: {
      id: runId,
      trigger,
      status: 'running',
      startedAt,
      completedAt: null,
      connectionCount: 0,
      accountCount: 0,
      positionCount: 0,
      activityCount: 0,
      orderCount: 0,
      errorCode: null,
    },
    completion,
  };
}

export async function startSnapTradeSync(
  trigger: InvestmentSyncTrigger,
  gateway: SnapTradeGateway = requireSnapTradeGateway(),
): Promise<InvestmentSyncRun> {
  return (await beginSnapTradeSync(trigger, gateway)).initial;
}

export async function syncSnapTrade(
  trigger: InvestmentSyncTrigger,
  gateway: SnapTradeGateway = requireSnapTradeGateway(),
): Promise<InvestmentSyncRun> {
  return (await beginSnapTradeSync(trigger, gateway)).completion;
}
