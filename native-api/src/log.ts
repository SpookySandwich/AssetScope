// Redacted in-memory log ring for /v1/diagnostics/logs (SPEC §21.6, §23.3).
// Entries are structured and secret-free by construction: request ids, status
// codes, counts, institution names — never tokens, amounts, or note bodies.

export type LogEntry = { at: string; level: 'info' | 'warn' | 'error'; message: string };

const MAX_ENTRIES = 200;
const ring: LogEntry[] = [];

export function log(level: LogEntry['level'], message: string): void {
  const entry: LogEntry = {
    at: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
    level,
    message: message.slice(0, 500),
  };
  ring.push(entry);
  if (ring.length > MAX_ENTRIES) ring.shift();
  // stderr keeps the parent's pipe drained and aids dev; stdout stays reserved
  // for the single handshake line.
  console.error(`[native-api] ${entry.at} ${level} ${entry.message}`);
}

export function recentLogs(limit = 50): LogEntry[] {
  return ring.slice(-Math.min(Math.max(limit, 1), MAX_ENTRIES));
}

export function clearLogs(): void {
  ring.length = 0;
}
