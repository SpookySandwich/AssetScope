import type { IncomingMessage, ServerResponse } from 'node:http';
import { ApiError, validation } from './errors.js';

// Minimal method+pattern router. Patterns use `:name` segments, e.g.
// GET /v1/accounts/:accountId. Kept dependency-free on purpose — the sidecar
// ships inside a desktop app and every runtime dependency is packaging surface.

export type RouteContext = {
  req: IncomingMessage;
  res: ServerResponse;
  requestId: string;
  params: Record<string, string>;
  query: URLSearchParams;
  body: unknown;
};

export type Handler = (ctx: RouteContext) => Promise<unknown>;

type Route = {
  method: string;
  segments: string[];
  handler: Handler;
};

const MAX_BODY_BYTES = 256 * 1024; // SPEC §23.1: limit request-body size

export class Router {
  private routes: Route[] = [];

  on(method: string, pattern: string, handler: Handler): this {
    this.routes.push({ method, segments: pattern.split('/').filter(Boolean), handler });
    return this;
  }

  get(pattern: string, handler: Handler): this { return this.on('GET', pattern, handler); }
  post(pattern: string, handler: Handler): this { return this.on('POST', pattern, handler); }
  patch(pattern: string, handler: Handler): this { return this.on('PATCH', pattern, handler); }
  put(pattern: string, handler: Handler): this { return this.on('PUT', pattern, handler); }
  delete(pattern: string, handler: Handler): this { return this.on('DELETE', pattern, handler); }

  /** Registered routes, for the OpenAPI contract test. */
  list(): { method: string; pattern: string }[] {
    return this.routes.map((r) => ({ method: r.method, pattern: '/' + r.segments.join('/') }));
  }

  match(method: string, pathname: string): { handler: Handler; params: Record<string, string> } | null {
    const parts = pathname.split('/').filter(Boolean);
    for (const route of this.routes) {
      if (route.method !== method || route.segments.length !== parts.length) continue;
      const params: Record<string, string> = {};
      let ok = true;
      for (let i = 0; i < parts.length; i++) {
        const seg = route.segments[i];
        if (seg.startsWith(':')) params[seg.slice(1)] = decodeURIComponent(parts[i]);
        else if (seg !== parts[i]) { ok = false; break; }
      }
      if (ok) return { handler: route.handler, params };
    }
    return null;
  }
}

export async function readJsonBody(req: IncomingMessage): Promise<unknown> {
  const method = req.method ?? 'GET';
  if (method === 'GET' || method === 'DELETE') return undefined;

  const contentType = req.headers['content-type'] ?? '';
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    size += (chunk as Buffer).length;
    if (size > MAX_BODY_BYTES) throw validation('Request body too large.');
    chunks.push(chunk as Buffer);
  }
  if (size === 0) return undefined;

  // SPEC §23.1: validate Content-Type on state-changing requests that carry a body.
  if (!contentType.includes('application/json')) {
    throw validation('Content-Type must be application/json.');
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch {
    throw new ApiError('VALIDATION_ERROR', 'Request body is not valid JSON.');
  }
}
