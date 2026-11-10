import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { DATA_DIR } from './paths.js';

const KEY_PATH = path.join(DATA_DIR, 'key');
const ALGORITHM = 'aes-256-gcm';
let cachedKey: Buffer | null = null;

function key(): Buffer {
  if (cachedKey) return cachedKey;
  fs.mkdirSync(DATA_DIR, { recursive: true });
  if (fs.existsSync(KEY_PATH)) {
    cachedKey = Buffer.from(fs.readFileSync(KEY_PATH, 'utf8').trim(), 'base64');
  } else {
    cachedKey = randomBytes(32);
    fs.writeFileSync(KEY_PATH, cachedKey.toString('base64'), { mode: 0o600 });
  }
  if (cachedKey.length !== 32) throw new Error('The local credential encryption key is invalid.');
  return cachedKey;
}

export function encryptToken(plaintext: string): string {
  const iv = randomBytes(12);
  const cipher = createCipheriv(ALGORITHM, key(), iv);
  const ciphertext = Buffer.concat([cipher.update(plaintext, 'utf8'), cipher.final()]);
  return `${iv.toString('base64')}:${cipher.getAuthTag().toString('base64')}:${ciphertext.toString('base64')}`;
}

export function decryptToken(value: string): string {
  const parts = value.split(':');
  if (parts.length !== 3) return value;
  const decipher = createDecipheriv(ALGORITHM, key(), Buffer.from(parts[0], 'base64'));
  decipher.setAuthTag(Buffer.from(parts[1], 'base64'));
  return decipher.update(Buffer.from(parts[2], 'base64')).toString('utf8') + decipher.final('utf8');
}

