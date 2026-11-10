import fs from 'node:fs';
import path from 'node:path';
import { db } from './database.js';
import { DATA_DIR, DATABASE_PATH } from './paths.js';

export async function backupDb(): Promise<void> {
  if (!fs.existsSync(DATABASE_PATH)) return;
  const directory = path.join(DATA_DIR, 'backups');
  fs.mkdirSync(directory, { recursive: true });
  const target = path.join(directory, `assetscope.${new Date().toISOString().slice(0, 10)}.bak`);
  if (!fs.existsSync(target)) await db.execute({ sql: 'VACUUM INTO ?', args: [target] });
}
