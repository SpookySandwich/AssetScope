// The sidecar must never outlive the WinUI parent process (SPEC §6.3).
// Poll the parent PID; when it disappears, exit. `process.kill(pid, 0)` is a
// liveness probe on Windows too — it throws ESRCH once the process is gone.

export function watchParent(parentPid: number, onGone: () => void, intervalMs = 2000): NodeJS.Timeout {
  const timer = setInterval(() => {
    try {
      process.kill(parentPid, 0);
    } catch {
      onGone();
    }
  }, intervalMs);
  timer.unref();
  return timer;
}
