import { describe, it, expect, beforeAll, vi } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});
vi.mock('../../native-api/src/core/backup.js', () => ({ backupDb: async () => {} }));

import { buildApp } from '../../native-api/src/app.js';

// Contract drift guard (SPEC §10.1): every route the sidecar actually registers
// must appear in openapi.yaml, and every documented path+method must exist in
// the router. Runs in CI so the contract cannot silently rot.

const OPENAPI_PATH = path.join(path.dirname(fileURLToPath(import.meta.url)), '../../native-api/openapi.yaml');
const HTTP_METHODS = new Set(['get', 'post', 'put', 'patch', 'delete']);

/** Minimal parser for our openapi.yaml layout: two-space-indented path keys with nested method keys. */
function parseOpenApiRoutes(yaml: string): Set<string> {
  const routes = new Set<string>();
  const lines = yaml.split('\n');
  let inPaths = false;
  let currentPath: string | null = null;
  for (const line of lines) {
    if (/^paths:\s*$/.test(line)) { inPaths = true; continue; }
    if (inPaths && /^[a-zA-Z]/.test(line)) inPaths = false; // left the paths block
    if (!inPaths) continue;
    const pathMatch = line.match(/^  (\/[^\s:]*):\s*$/);
    if (pathMatch) { currentPath = pathMatch[1]; continue; }
    const methodMatch = line.match(/^    ([a-z]+):\s*$/);
    if (methodMatch && currentPath && HTTP_METHODS.has(methodMatch[1])) {
      // openapi {param} → router :param, and the /v1 prefix lives in the server url.
      const routerPath = '/v1' + currentPath.replace(/\{([^}]+)\}/g, ':$1');
      routes.add(`${methodMatch[1].toUpperCase()} ${routerPath}`);
    }
  }
  return routes;
}

let registered: Set<string>;
let documented: Set<string>;

beforeAll(async () => {
  const app = await buildApp({
    mode: 'demo',
    dataDir: 'C:\\test\\contract',
    sessionToken: 'contract-test-token-00000000000000000000',
    onShutdownRequested: () => {},
  });
  registered = new Set(app.router.list().map((r) => `${r.method} ${r.pattern}`));
  documented = parseOpenApiRoutes(fs.readFileSync(OPENAPI_PATH, 'utf8'));
});

describe('openapi.yaml ↔ router synchronization', () => {
  it('parses a plausible number of documented routes', () => {
    expect(documented.size).toBeGreaterThan(20);
  });

  it('every registered route is documented in openapi.yaml', () => {
    const undocumented = [...registered].filter((r) => !documented.has(r));
    expect(undocumented).toEqual([]);
  });

  it('every documented route is registered in the sidecar', () => {
    const unimplemented = [...documented].filter((r) => !registered.has(r));
    expect(unimplemented).toEqual([]);
  });
});
