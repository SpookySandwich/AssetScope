import {
  Snaptrade,
  SnaptradeAuth,
  SnaptradeError,
} from 'snaptrade-typescript-sdk';
import { ApiError } from '../errors.js';

// Keep the rest of the app independent from the generated SDK. The generated
// model is intentionally permissive (`[key: string]: any`) and changes often;
// this narrow gateway is the stable boundary we mock in persistence tests.
export type SnapTradeRecord = Record<string, unknown>;

export type SnapTradeBalanceHistory = {
  history: SnapTradeRecord[];
  currency: string | null;
};

export interface SnapTradeGateway {
  check(): Promise<void>;
  partnerInfo(): Promise<SnapTradeRecord>;
  createPortal(input?: { reconnect?: string; darkMode?: boolean }): Promise<{ redirectURI: string; sessionId: string | null }>;
  listConnections(): Promise<SnapTradeRecord[]>;
  listAccounts(): Promise<SnapTradeRecord[]>;
  listPositions(accountId: string): Promise<{ results: SnapTradeRecord[]; asOf: string | null }>;
  listBalances(accountId: string): Promise<SnapTradeRecord[]>;
  listActivities(accountId: string): Promise<SnapTradeRecord[]>;
  listOrders(accountId: string): Promise<SnapTradeRecord[]>;
  listBalanceHistory(accountId: string): Promise<SnapTradeBalanceHistory | null>;
}

export type SnapTradeCredentials = { clientId: string; consumerKey: string };

function object(value: unknown): SnapTradeRecord {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as SnapTradeRecord
    : {};
}

function array(value: unknown): SnapTradeRecord[] {
  return Array.isArray(value) ? value.filter((v): v is SnapTradeRecord =>
    v !== null && typeof v === 'object' && !Array.isArray(v)) : [];
}

function optionalHistoryUnavailable(error: unknown): boolean {
  // SnapTrade documents this experimental endpoint as disabled by default.
  // Feature-not-enabled is currently 403; 404/501 cover broker/account and
  // rollout variants without turning an optional enrichment into sync failure.
  return error instanceof SnaptradeError
    && [403, 404, 501].includes(error.status ?? 0);
}

export function mapSnapTradeError(error: unknown): ApiError {
  if (error instanceof ApiError) return error;
  if (error instanceof SnaptradeError) {
    if (error.status === 401 || error.status === 403) {
      return new ApiError('AUTH_REQUIRED', 'SnapTrade rejected the configured API credentials.');
    }
    if (error.status === 429) {
      return new ApiError('RATE_LIMITED', 'SnapTrade rate limit reached. Try again later.', { retryable: true });
    }
    if (error.status !== undefined && error.status >= 500) {
      return new ApiError('NETWORK_UNAVAILABLE', 'SnapTrade is temporarily unavailable.', { retryable: true });
    }
    if (['ECONNABORTED', 'ECONNRESET', 'ENOTFOUND', 'ETIMEDOUT'].includes(error.code ?? '')) {
      return new ApiError('NETWORK_UNAVAILABLE', 'Could not reach SnapTrade.', { retryable: true });
    }
  }
  return new ApiError('INTERNAL_ERROR', 'SnapTrade returned an unexpected response.', { retryable: true });
}

export function createSnapTradeGateway(credentials: SnapTradeCredentials): SnapTradeGateway {
  const auth = SnaptradeAuth.personalApiKey(credentials);
  const sdk = new Snaptrade({ auth });

  return {
    async check() {
      try { await sdk.apiStatus.check(); }
      catch (error) { throw mapSnapTradeError(error); }
    },

    async partnerInfo() {
      try { return object((await sdk.referenceData.getPartnerInfo()).data); }
      catch (error) { throw mapSnapTradeError(error); }
    },

    async createPortal(input = {}) {
      try {
        const response = await sdk.authentication.loginSnapTradeUser({
          reconnect: input.reconnect,
          connectionType: 'read',
          showCloseButton: true,
          darkMode: input.darkMode ?? false,
          connectionPortalVersion: 'v4',
        });
        const data = object(response.data);
        const redirectURI = typeof data.redirectURI === 'string' ? data.redirectURI : '';
        if (!redirectURI) throw new ApiError('DATA_CORRUPT', 'SnapTrade did not return a Connection Portal URL.');
        return {
          redirectURI,
          sessionId: typeof data.sessionId === 'string' ? data.sessionId : null,
        };
      } catch (error) { throw mapSnapTradeError(error); }
    },

    async listConnections() {
      try { return array((await sdk.connections.listBrokerageAuthorizations()).data); }
      catch (error) { throw mapSnapTradeError(error); }
    },

    async listAccounts() {
      try { return array((await sdk.accountInformation.listUserAccounts()).data); }
      catch (error) { throw mapSnapTradeError(error); }
    },

    async listPositions(accountId: string) {
      try {
        const data = object((await sdk.accountInformation.getAllAccountPositions({ accountId })).data);
        const freshness = object(data.data_freshness);
        return {
          results: array(data.results),
          asOf: typeof freshness.as_of === 'string' ? freshness.as_of : null,
        };
      } catch (error) { throw mapSnapTradeError(error); }
    },

    async listBalances(accountId: string) {
      try { return array((await sdk.accountInformation.getUserAccountBalance({ accountId })).data); }
      catch (error) { throw mapSnapTradeError(error); }
    },

    async listActivities(accountId: string) {
      const all: SnapTradeRecord[] = [];
      const limit = 1000;
      let offset = 0;
      try {
        for (;;) {
          const page = object((await sdk.accountInformation.getAccountActivities({ accountId, limit, offset })).data);
          const records = array(page.data);
          all.push(...records);
          const pagination = object(page.pagination);
          const total = typeof pagination.total === 'number' ? pagination.total : null;
          if (records.length < limit || (total !== null && all.length >= total)) break;
          offset += records.length;
          // A defensive ceiling prevents a malformed pagination response from
          // turning a desktop sync into an unbounded request loop.
          if (offset >= 100_000) break;
        }
        return all;
      } catch (error) { throw mapSnapTradeError(error); }
    },

    async listOrders(accountId: string) {
      try {
        // Use the ordinary cached holdings endpoint, not `recentOrders`, which
        // SnapTrade documents as always real-time. Ninety days is the maximum
        // supported range and does not trigger a manual brokerage refresh.
        return array((await sdk.accountInformation.getUserAccountOrders({
          accountId,
          state: 'all',
          days: 90,
        })).data);
      } catch (error) { throw mapSnapTradeError(error); }
    },

    async listBalanceHistory(accountId: string) {
      try {
        const data = object((await sdk.accountInformation.getAccountBalanceHistory({ accountId })).data);
        // A missing/non-array history field is a malformed optional response.
        // Returning null lets the atomic sync retain its daily snapshot fallback.
        if (!Array.isArray(data.history)) return null;
        return {
          history: array(data.history),
          currency: typeof data.currency === 'string' && data.currency.trim()
            ? data.currency.trim().toUpperCase()
            : null,
        };
      } catch (error) {
        if (optionalHistoryUnavailable(error)) return null;
        throw mapSnapTradeError(error);
      }
    },
  };
}
