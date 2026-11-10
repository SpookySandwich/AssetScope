# Troubleshooting

Data and settings locations are in [architecture.md](architecture.md#data).
`app.log` sits beside `settings.json` in `%LOCALAPPDATA%\AssetScope` and is
replaced at each launch by the main instance, so it always describes the current
session. Settings > Diagnostics shows a redacted tail of it without leaving the
app.

## The app will not start

Start with `app.log`. Its last-write time tells you how far the launch got.

**The log has entries from the failed launch.** The failure is after startup;
read the `[ERROR]`/`[FATAL]` line.

**The log is untouched.** The process died before `AppLog.Initialize()`, which
runs early in `App.OnLaunched`. The failure is in the `App` constructor or
earlier, and there will be no error dialog. Read the crash out of Windows:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddDays(-1)} | Where-Object { $_.Message -match 'assetscope' } | Select-Object -First 5 TimeCreated, Id, Message | Format-List
```

Exit code `0xC000027B` with a faulting module of `Microsoft.UI.Xaml.dll` is an
unhandled managed exception escaping XAML startup. If `combase.dll` and
`0x8000FFFF` (`E_UNEXPECTED`) also appear, something touched WinUI state that is
not ready yet — reading `Application.Resources` from the `App` constructor does
exactly this.

### Isolate settings from data

A bad `settings.json` reproduces on every launch. Point the app at a throwaway
settings directory; if it starts, the problem is a setting, not the install:

```powershell
$env:ASSETSCOPE_APPDATA_DIR = "$env:TEMP\assetscope-clean"; & "$env:LOCALAPPDATA\Programs\AssetScope\assetscope.exe"
```

This redirects settings and the log only. Financial data still comes from
`%USERPROFILE%\.assetscope`, so this is safe to run and shows real balances.

## "The local data engine could not be started"

The app could not launch its Node sidecar.

An **installed** build ships its own `node.exe` under
`%LOCALAPPDATA%\Programs\AssetScope\sidecar` and never uses `PATH`. If an
installed build reports this, the install is incomplete — reinstall.

A build run from `winui/AssetScope.App/bin` is *not* installed: it resolves the
sidecar from the repo and needs Node 22+ on `PATH`. From a dev run this usually
just means `node` is missing from that shell.

## A launch appears to do nothing

AssetScope registers the single-instance key `assetscope-main`. A second launch
hands its activation to the running process and exits, which is also how the
scheduled `--sync-once` run avoids two sidecars writing one database. Check
whether the window is hidden in the notification area before assuming it failed.

## Balances look wrong

Sync failures never overwrite good data — a failed refresh keeps the last
complete snapshot, so stale values mean sync is failing, not that data is lost.
Check Settings > Diagnostics for the last sync result.

Before disputing a computed figure, check the rules it follows in
[architecture.md](architecture.md#financial-semantics): loans count as
liabilities, net-worth series carry balances forward across gaps, gain/loss is
cost-basis only, and realized P/L excludes sales whose shares predate the synced
history.

## Account or credit card names look generic

Financial institutions (e.g. Chase) frequently export generic labels over Open
Banking/Plaid feeds (such as `CREDIT CARD` or `CHASE FREEDOM` instead of specific
sub-brands like `Chase Freedom Flex`).

AssetScope preserves the upstream institution name for sync integrity, but
allows overriding it locally:
- On the **Accounts** page, right-click the card (or click `...`) and select
  **Edit Nickname**.
- Nicknames immediately take precedence as the display name across all views,
  charts, and exports, and persist across future syncs.
