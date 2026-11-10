import fs from 'node:fs';
import path from 'node:path';

const CURRENT_DATABASE = 'assetscope.db';
const LEGACY_DATABASE = 'fungible.db';

/**
 * Copy a legacy data directory into the AssetScope location exactly once.
 * The source is intentionally retained as a rollback copy. Migration stages
 * into a sibling directory and renames it only after the database is present,
 * so a failed copy never leaves a half-populated live directory.
 */
export function migrateLegacyDataDirectory(target: string, legacy: string): void {
  const currentDatabase = path.join(target, CURRENT_DATABASE);
  const legacyDatabase = path.join(legacy, LEGACY_DATABASE);
  if (isUsableFile(currentDatabase) || !isUsableFile(legacyDatabase)) return;

  const staging = `${target}.migrating-${process.pid}`;
  try {
    fs.cpSync(legacy, staging, { recursive: true, errorOnExist: true });
    const stagedLegacyDb = path.join(staging, LEGACY_DATABASE);
    const stagedCurrentDb = path.join(staging, CURRENT_DATABASE);
    fs.copyFileSync(stagedLegacyDb, stagedCurrentDb, fs.constants.COPYFILE_EXCL);
    if (!fs.existsSync(target)) {
      fs.renameSync(staging, target);
      return;
    }

    copyMissing(staging, target);
    if (fs.existsSync(currentDatabase))
      fs.renameSync(currentDatabase, `${currentDatabase}.pre-migration-empty`);
    fs.renameSync(stagedCurrentDb, currentDatabase);
    fs.rmSync(staging, { recursive: true, force: true });
  } catch (error) {
    fs.rmSync(staging, { recursive: true, force: true });
    throw error;
  }
}

function isUsableFile(file: string): boolean {
  try { return fs.statSync(file).size > 0; }
  catch { return false; }
}

function copyMissing(source: string, target: string): void {
  fs.mkdirSync(target, { recursive: true });
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    const sourcePath = path.join(source, entry.name);
    const targetPath = path.join(target, entry.name);
    if (entry.isDirectory()) copyMissing(sourcePath, targetPath);
    else if (!fs.existsSync(targetPath)) fs.copyFileSync(sourcePath, targetPath);
  }
}
