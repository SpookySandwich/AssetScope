import { validation, ApiError } from '../errors.js';
import { createSnapTradeGateway, type SnapTradeCredentials } from './snaptrade-client.js';

const CLIENT_ID_ENV = 'SNAPTRADE_CLIENT_ID';
const CONSUMER_KEY_ENV = 'SNAPTRADE_CONSUMER_KEY';

export type SnapTradeConfigInput = { clientId: string; consumerKey: string };

export function getSnapTradeCredentials(): SnapTradeCredentials | null {
  const clientId = process.env[CLIENT_ID_ENV]?.trim();
  const consumerKey = process.env[CONSUMER_KEY_ENV]?.trim();
  return clientId && consumerKey ? { clientId, consumerKey } : null;
}

export function isSnapTradeConfigured(): boolean {
  return getSnapTradeCredentials() !== null;
}

export function snapTradeConfigStatus() {
  const credentials = getSnapTradeCredentials();
  return {
    configured: credentials !== null,
    clientIdSuffix: credentials ? credentials.clientId.slice(-4) : null,
    consumerKeyConfigured: credentials !== null,
    authMode: 'personal',
  };
}

export async function applyAndTestSnapTradeConfig(input: SnapTradeConfigInput) {
  const clientId = String(input.clientId ?? '').trim();
  const consumerKey = String(input.consumerKey ?? '').trim();
  if (clientId.length < 4) throw validation('SnapTrade Client ID is required.', { field: 'clientId' });
  if (consumerKey.length < 8) throw validation('SnapTrade Consumer Key is required.', { field: 'consumerKey' });

  const gateway = createSnapTradeGateway({ clientId, consumerKey });
  await gateway.check();
  const partner = await gateway.partnerInfo();
  if (partner.can_access_holdings === false) {
    throw new ApiError('CONFLICT', 'This SnapTrade key does not include holdings access.');
  }

  // Only accept the new credentials after both validation calls succeed. They
  // remain process-local; WinUI persists the key in Credential Manager.
  process.env[CLIENT_ID_ENV] = clientId;
  process.env[CONSUMER_KEY_ENV] = consumerKey;
  return {
    ...snapTradeConfigStatus(),
    canAccessHoldings: partner.can_access_holdings !== false,
    canAccessAccountHistory: partner.can_access_account_history === true,
    partnerName: typeof partner.name === 'string' ? partner.name : null,
  };
}

export function requireSnapTradeGateway() {
  const credentials = getSnapTradeCredentials();
  if (!credentials) {
    throw new ApiError('NOT_CONFIGURED', 'Configure a SnapTrade Personal API Key first.');
  }
  return createSnapTradeGateway(credentials);
}

