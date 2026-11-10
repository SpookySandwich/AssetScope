import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';

// Keep the existing data directory as a compatibility boundary while code
// ownership moves out of the upstream project. Users keep their connected
// accounts and encrypted tokens; new callers may use the product-specific env.
export const DATA_DIR = process.env.ASSETSCOPE_DATA_DIR
  ?? process.env.FUNGIBLE_DATA_DIR
  ?? path.join(os.homedir(), '.assetscope');

const currentDatabase = path.join(DATA_DIR, 'assetscope.db');
const legacyDatabase = path.join(DATA_DIR, 'fungible.db');
// An explicitly selected legacy directory remains usable without mutating it.
export const DATABASE_PATH = fs.existsSync(currentDatabase) || !fs.existsSync(legacyDatabase)
  ? currentDatabase
  : legacyDatabase;
