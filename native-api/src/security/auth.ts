import { randomBytes, timingSafeEqual } from 'node:crypto';
import type { IncomingMessage } from 'node:http';
import { ApiError } from '../errors.js';

// Session token: ≥256 bits of entropy, generated per sidecar launch, delivered
// to the parent process once over stdout, held in memory only (SPEC §23.2).

export function generateSessionToken(): string {
  return randomBytes(32).toString('base64url');
}

export function assertAuthorized(req: IncomingMessage, expectedToken: string): void {
  const header = req.headers['authorization'];
  const provided = typeof header === 'string' && header.startsWith('Bearer ')
    ? header.slice('Bearer '.length)
    : '';
  const a = Buffer.from(provided);
  const b = Buffer.from(expectedToken);
  const ok = a.length === b.length && timingSafeEqual(a, b);
  if (!ok) throw new ApiError('UNAUTHORIZED', 'Missing or invalid session token.');
}
