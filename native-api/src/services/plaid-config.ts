import fs from 'node:fs';
import path from 'node:path';
import { DATA_DIR } from '../core/paths.js';
import { isPlaidConfigured, createLinkToken, plaidErrorMessage } from '../core/plaid.js';
import { ApiError, validation } from '../errors.js';

// Plaid credential configuration (SPEC §13.1/§13.2). Credentials live in
// process.env for the lifetime of this sidecar only — the WinUI app owns
// persistence (Client ID in local settings, Secret in Windows Credential
// Manager) and supplies them at launch or via PUT /v1/plaid/config. The
// sidecar never writes the secret to the settings table, disk, or logs.

const VALID_ENVIRONMENTS = new Set(['sandbox', 'development', 'production']);

export type PlaidConfigInput = {
  environment?: string;
  clientId?: string;
  secret?: string;
};

export type PlaidConfigStatus = {
  configured: boolean;
  environment: string;
  clientIdSuffix: string | null;
  secretConfigured: boolean;
  envFileDetected: boolean;
};

export function configStatus(): PlaidConfigStatus {
  const clientId = process.env.PLAID_CLIENT_ID;
  return {
    configured: isPlaidConfigured(),
    environment: process.env.PLAID_ENV ?? 'sandbox',
    clientIdSuffix: clientId ? clientId.slice(-4) : null,
    secretConfigured: !!process.env.PLAID_SECRET,
    envFileDetected: fs.existsSync(path.join(DATA_DIR, '.env')),
  };
}

/**
 * Apply new credentials, validate them by creating a link token, and roll back
 * on failure. Never logs or returns the secret.
 */
export async function applyAndTestConfig(input: PlaidConfigInput): Promise<PlaidConfigStatus> {
  if (input.environment !== undefined && !VALID_ENVIRONMENTS.has(input.environment)) {
    throw validation('environment must be sandbox, development, or production.', { field: 'environment' });
  }
  if (input.clientId !== undefined && !/^[A-Za-z0-9]{8,64}$/.test(input.clientId)) {
    throw validation('clientId does not look like a Plaid client id.', { field: 'clientId' });
  }
  if (input.secret !== undefined && (input.secret.length < 8 || input.secret.length > 128)) {
    throw validation('secret has an unexpected length.', { field: 'secret' });
  }

  const previous = {
    PLAID_ENV: process.env.PLAID_ENV,
    PLAID_CLIENT_ID: process.env.PLAID_CLIENT_ID,
    PLAID_SECRET: process.env.PLAID_SECRET,
  };

  if (input.environment !== undefined) process.env.PLAID_ENV = input.environment;
  if (input.clientId !== undefined) process.env.PLAID_CLIENT_ID = input.clientId;
  if (input.secret !== undefined) process.env.PLAID_SECRET = input.secret;

  if (!isPlaidConfigured()) {
    restore(previous);
    throw new ApiError('NOT_CONFIGURED', 'Both a client id and a secret are required.');
  }

  try {
    // A successful link-token creation proves the credentials work for this
    // environment (SPEC §15.4 — it does not guarantee any institution connects).
    await createLinkToken('assetscope-config-test');
  } catch (err) {
    restore(previous);
    throw new ApiError('AUTH_REQUIRED', `Plaid rejected the credentials: ${plaidErrorMessage(err)}`);
  }
  return configStatus();
}

function restore(previous: Record<string, string | undefined>): void {
  for (const [key, value] of Object.entries(previous)) {
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
}
