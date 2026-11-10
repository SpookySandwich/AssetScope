import { db } from '../core/database.js';

export async function listSnapTradeConnections() {
  const result = await db.execute(`
    SELECT sc.*,
           (SELECT COUNT(*) FROM account_provider_links apl
             JOIN accounts a ON a.id = apl.account_id
             WHERE apl.provider = 'snaptrade'
               AND apl.provider_connection_id = sc.connection_id
               AND a.archived_at IS NULL) AS account_count
      FROM snaptrade_connections sc
     ORDER BY sc.disabled DESC, sc.brokerage_name, sc.display_name`);
  return (result.rows as unknown as Record<string, unknown>[]).map((row) => ({
    connectionId: String(row.connection_id),
    brokerageId: row.brokerage_id === null ? null : String(row.brokerage_id),
    brokerageSlug: row.brokerage_slug === null ? null : String(row.brokerage_slug),
    brokerageName: String(row.brokerage_name),
    brokerageLogoUrl: row.brokerage_logo_url === null ? null : String(row.brokerage_logo_url),
    displayName: row.display_name === null ? null : String(row.display_name),
    connectionType: String(row.connection_type ?? 'read'),
    disabled: Number(row.disabled ?? 0) === 1,
    disabledAt: row.disabled_at === null ? null : String(row.disabled_at),
    createdAt: row.created_at === null ? null : String(row.created_at),
    lastSuccessAt: row.last_success_at === null ? null : String(row.last_success_at),
    lastErrorCode: row.last_error_code === null ? null : String(row.last_error_code),
    accountCount: Number(row.account_count ?? 0),
  }));
}
