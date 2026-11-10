import type { ServerResponse } from 'node:http';
import { randomUUID } from 'node:crypto';
import { ApiError } from './errors.js';

export const PROTOCOL_VERSION = '1.0';
export const SIDECAR_VERSION = '0.2.0';

// SPEC §10.2 response envelope. Every /v1 response — success or failure —
// uses this shape so the C# client can deserialize uniformly.

type Meta = { requestId: string; generatedAt: string; protocolVersion: string };

const meta = (requestId: string): Meta => ({
  requestId,
  generatedAt: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
  protocolVersion: PROTOCOL_VERSION,
});

export const newRequestId = (): string => randomUUID();

export function sendData(res: ServerResponse, requestId: string, data: unknown, status = 200): void {
  const json = JSON.stringify({ data, meta: meta(requestId), error: null });
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(json),
    'Cache-Control': 'no-store',
  });
  res.end(json);
}

export function sendError(res: ServerResponse, requestId: string, err: ApiError): void {
  const json = JSON.stringify({
    data: null,
    meta: meta(requestId),
    error: {
      code: err.code,
      message: err.message,
      retryable: err.retryable,
      details: err.details,
    },
  });
  res.writeHead(err.status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(json),
    'Cache-Control': 'no-store',
  });
  res.end(json);
}
