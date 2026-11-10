// Sidecar entry point (SPEC §6.2). Argument parsing and data-dir resolution
// happen BEFORE database import: core/database.ts captures ASSETSCOPE_DATA_DIR at
// module load, so the dynamic import of app.js below is deliberate and
// load-bearing. Do not convert it to a static import.
import path from 'node:path';
import os from 'node:os';
import { migrateLegacyDataDirectory } from './core/data-migration.js';

type Args = {
  port: number;
  parentPid: number | null;
  dataDir: string | null;
  mode: 'real' | 'demo';
  syncOnce: boolean;
};

function parseArgs(argv: string[]): Args {
  const args: Args = { port: 0, parentPid: null, dataDir: null, mode: 'real', syncOnce: false };
  for (let i = 0; i < argv.length; i++) {
    switch (argv[i]) {
      case '--port': args.port = Number(argv[++i]) || 0; break;
      case '--parent-pid': args.parentPid = Number(argv[++i]) || null; break;
      case '--data-dir': args.dataDir = argv[++i] ?? null; break;
      case '--mode': args.mode = argv[++i] === 'demo' ? 'demo' : 'real'; break;
      case '--sync-once': args.syncOnce = true; break;
    }
  }
  return args;
}

const args = parseArgs(process.argv.slice(2));

const dataDir = args.dataDir
  ?? process.env.ASSETSCOPE_DATA_DIR
  ?? process.env.FUNGIBLE_DATA_DIR
  ?? (args.mode === 'demo'
    ? path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'AssetScope', 'Demo')
    : path.join(os.homedir(), '.assetscope'));

if (args.mode !== 'demo' && !args.dataDir && !process.env.ASSETSCOPE_DATA_DIR && !process.env.FUNGIBLE_DATA_DIR)
  migrateLegacyDataDirectory(dataDir, path.join(os.homedir(), '.fungible'));
process.env.ASSETSCOPE_DATA_DIR = dataDir;

// Load Plaid credentials the same way the upstream API server does: from
// <dataDir>/.env. Demo Mode never loads real credentials (SPEC §15.6).
if (args.mode !== 'demo') {
  const { config } = await import('dotenv');
  config({ path: path.join(dataDir, '.env'), quiet: true });
}

if (args.syncOnce) {
  // Headless scheduled sync (SPEC §12.4): no server, no handshake.
  const { runScheduledSync } = await import('./sync-host.js');
  process.exit(await runScheduledSync());
}

const { buildApp } = await import('./app.js');
const { generateSessionToken } = await import('./security/auth.js');
const { watchParent } = await import('./security/parent-watch.js');
const { PROTOCOL_VERSION } = await import('./envelope.js');

const sessionToken = generateSessionToken();

const { server } = await buildApp({
  mode: args.mode,
  dataDir,
  sessionToken,
  onShutdownRequested: () => shutdown(0),
});

let closing = false;
function shutdown(code: number): void {
  if (closing) return;
  closing = true;
  server.close(() => process.exit(code));
  // Escape hatch if a connection lingers past close.
  setTimeout(() => process.exit(code), 2000).unref();
}

if (args.parentPid !== null) {
  watchParent(args.parentPid, () => shutdown(0));
}
process.on('SIGINT', () => shutdown(0));
process.on('SIGTERM', () => shutdown(0));

// Loopback only — never 0.0.0.0 (SPEC §5.2).
server.listen(args.port, '127.0.0.1', () => {
  const addr = server.address();
  const port = typeof addr === 'object' && addr ? addr.port : 0;
  // Exactly one handshake line on stdout; the parent stops recording after this.
  process.stdout.write(JSON.stringify({
    event: 'ready',
    protocolVersion: PROTOCOL_VERSION,
    port,
    token: sessionToken,
    pid: process.pid,
    dataDir,
    mode: args.mode,
  }) + '\n');
});

server.on('error', (err) => {
  process.stdout.write(JSON.stringify({ event: 'error', message: String(err) }) + '\n');
  process.exit(1);
});
