import fs from 'node:fs';
import path from 'node:path';
import { DATA_DIR } from '../core/paths.js';

// Data-directory write coordination (SPEC §12.4/§12.5): the desktop sidecar
// holds runtime/sidecar.lock while running; the one-shot SyncHost defers with
// runtime/pending-sync.json when the lock is live, and the desktop sidecar
// picks the marker up. runtime/ never holds secrets (SPEC §7.3).

const RUNTIME_DIR = path.join(DATA_DIR, 'runtime');
const LOCK_FILE = path.join(RUNTIME_DIR, 'sidecar.lock');
const MARKER_FILE = path.join(RUNTIME_DIR, 'pending-sync.json');

const pidAlive = (pid: number): boolean => {
  try { process.kill(pid, 0); return true; }
  catch { return false; }
};

/** True when another live sidecar owns the data directory. */
export function isLockHeldByOther(): boolean {
  try {
    const raw = fs.readFileSync(LOCK_FILE, 'utf8');
    const { pid } = JSON.parse(raw) as { pid: number };
    return pid !== process.pid && pidAlive(pid);
  } catch {
    return false; // missing or stale/corrupt lock
  }
}

export function acquireLock(): void {
  try {
    fs.mkdirSync(RUNTIME_DIR, { recursive: true });
    fs.writeFileSync(LOCK_FILE, JSON.stringify({ pid: process.pid, at: new Date().toISOString() }));
  } catch { /* read-only directory: degraded mode handles it */ }
}

export function releaseLock(): void {
  try {
    const raw = fs.readFileSync(LOCK_FILE, 'utf8');
    if ((JSON.parse(raw) as { pid: number }).pid === process.pid) fs.unlinkSync(LOCK_FILE);
  } catch { /* already gone */ }
}

export function writePendingSyncMarker(): void {
  fs.mkdirSync(RUNTIME_DIR, { recursive: true });
  fs.writeFileSync(MARKER_FILE, JSON.stringify({ requestedAt: new Date().toISOString() }));
}

/** Consume the deferred-sync marker if present. */
export function takePendingSyncMarker(): boolean {
  try {
    fs.unlinkSync(MARKER_FILE);
    return true;
  } catch {
    return false;
  }
}
