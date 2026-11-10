import { Configuration, CountryCode, PlaidApi, PlaidEnvironments, Products } from 'plaid';

export function isPlaidConfigured(): boolean {
  return Boolean(process.env.PLAID_CLIENT_ID && process.env.PLAID_SECRET);
}

export function getPlaidClient(): PlaidApi {
  const clientId = process.env.PLAID_CLIENT_ID;
  const secret = process.env.PLAID_SECRET;
  if (!clientId || !secret) throw new Error('Plaid credentials are not configured.');
  const environment = process.env.PLAID_ENV ?? 'sandbox';
  const basePath = PlaidEnvironments[environment as keyof typeof PlaidEnvironments];
  if (!basePath) throw new Error(`Unknown Plaid environment: ${environment}`);
  return new PlaidApi(new Configuration({
    basePath,
    baseOptions: { headers: { 'PLAID-CLIENT-ID': clientId, 'PLAID-SECRET': secret } },
  }));
}

export async function createLinkToken(userId: string, daysRequested?: number): Promise<string> {
  const response = await getPlaidClient().linkTokenCreate({
    user: { client_user_id: userId },
    client_name: 'AssetScope',
    products: [Products.Transactions],
    country_codes: [CountryCode.Us],
    language: 'en',
    ...(daysRequested ? { transactions: { days_requested: daysRequested } } : {}),
  });
  return response.data.link_token;
}

export async function exchangePublicToken(publicToken: string) {
  const response = await getPlaidClient().itemPublicTokenExchange({ public_token: publicToken });
  return { accessToken: response.data.access_token, itemId: response.data.item_id };
}

export function plaidErrorMessage(error: unknown): string {
  const data = (error as { response?: { data?: Record<string, string> } })?.response?.data;
  if (data?.error_code) return data.display_message || `${data.error_code}: ${data.error_message}`;
  return error instanceof Error ? error.message : String(error);
}

