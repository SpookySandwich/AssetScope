import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { migrateLegacyDataDirectory } from '../../native-api/src/core/data-migration.js';

const temporaryRoots: string[] = [];

afterEach(() => {
  for (const root of temporaryRoots.splice(0))
    fs.rmSync(root, { recursive: true, force: true });
});

describe('legacy product data migration', () => {
  it('copies data into the new directory, renames the database, and retains the source', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'assetscope-migration-'));
    temporaryRoots.push(root);
    const legacy = path.join(root, '.fungible');
    const current = path.join(root, '.assetscope');
    fs.mkdirSync(path.join(legacy, 'backups'), { recursive: true });
    fs.writeFileSync(path.join(legacy, 'fungible.db'), 'database');
    fs.writeFileSync(path.join(legacy, 'key'), 'key-material');
    fs.writeFileSync(path.join(legacy, 'backups', 'fungible.2026-01-01.bak'), 'backup');

    migrateLegacyDataDirectory(current, legacy);

    expect(fs.readFileSync(path.join(current, 'assetscope.db'), 'utf8')).toBe('database');
    expect(fs.readFileSync(path.join(current, 'key'), 'utf8')).toBe('key-material');
    expect(fs.existsSync(path.join(current, 'backups', 'fungible.2026-01-01.bak'))).toBe(true);
    expect(fs.existsSync(path.join(legacy, 'fungible.db'))).toBe(true);
  });

  it('does not overwrite an existing AssetScope directory', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'assetscope-migration-'));
    temporaryRoots.push(root);
    const legacy = path.join(root, '.fungible');
    const current = path.join(root, '.assetscope');
    fs.mkdirSync(legacy);
    fs.mkdirSync(current);
    fs.writeFileSync(path.join(legacy, 'fungible.db'), 'legacy');
    fs.writeFileSync(path.join(current, 'assetscope.db'), 'current');

    migrateLegacyDataDirectory(current, legacy);

    expect(fs.readFileSync(path.join(current, 'assetscope.db'), 'utf8')).toBe('current');
  });

  it('repairs a pre-created empty database without discarding other new files', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'assetscope-migration-'));
    temporaryRoots.push(root);
    const legacy = path.join(root, '.fungible');
    const current = path.join(root, '.assetscope');
    fs.mkdirSync(legacy);
    fs.mkdirSync(current);
    fs.writeFileSync(path.join(legacy, 'fungible.db'), 'legacy database');
    fs.writeFileSync(path.join(legacy, 'key'), 'legacy key');
    fs.writeFileSync(path.join(current, 'assetscope.db'), '');
    fs.writeFileSync(path.join(current, 'new-runtime-state'), 'keep me');

    migrateLegacyDataDirectory(current, legacy);

    expect(fs.readFileSync(path.join(current, 'assetscope.db'), 'utf8')).toBe('legacy database');
    expect(fs.readFileSync(path.join(current, 'assetscope.db.pre-migration-empty'), 'utf8')).toBe('');
    expect(fs.readFileSync(path.join(current, 'key'), 'utf8')).toBe('legacy key');
    expect(fs.readFileSync(path.join(current, 'new-runtime-state'), 'utf8')).toBe('keep me');
  });
});
