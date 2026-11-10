---
name: assetscope
description: "This skill should be used when the user asks about their personal finances, net worth, balances, accounts, assets, liabilities, allocation, investments, brokerage positions, realized P/L, spending, transactions, cash flow, credit cards, card rewards, cash back, points, portfolio snapshot, or AssetScope data — including phrases like \"how much do I have\", \"what's my net worth\", \"where is my money\", \"what did I spend\", \"show my accounts\", \"which card should I use\", \"what cards do I have\", or \"sync my banks\". Query the local AssetScope CLI; do not invent numbers or ask for facts the app already holds."
license: MIT
compatibility: Windows; Node.js 22+; local AssetScope database at %USERPROFILE%\\.assetscope
---

# AssetScope

Query the user's local AssetScope database through the headless CLI. AssetScope is a read-only net-worth app: bank/credit via Plaid, brokerage via SnapTrade, plus manual balances. The CLI wraps the same TypeScript services as the WinUI app. It cannot trade, transfer money, or place orders.

## Invoke

Run via the skill launcher so the installed sidecar, this repo, or `ASSETSCOPE_CLI` is found without depending on `PATH`.

PowerShell (preferred):

```powershell
& "$env:USERPROFILE\.claude\skills\assetscope\scripts\run.ps1" <command> [flags]
```

If the user-level copy is missing, read `config.json` `repo` and run `<repo>\bin\assetscope.cmd`.

From Git bash:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$USERPROFILE/.claude/skills/assetscope/scripts/run.ps1" <command> [flags]
```

Stdout is JSON. Stderr errors are `{"error":{"code","message"}}`. Exit `0` success, `1` runtime/partial sync, `2` validation, `3` not configured, `4` not found.

Print the command catalog without opening the database:

```powershell
& "$env:USERPROFILE\.claude\skills\assetscope\scripts\run.ps1" help
& "$env:USERPROFILE\.claude\skills\assetscope\scripts\run.ps1" help accounts
```

Do not use `npm run cli` as the primary invocation — npm prepends a script banner to stdout and breaks JSON parse.

## Which command

| Question | Command |
| --- | --- |
| Totals / "how much am I worth" | `summary` |
| List accounts and balances | `accounts` |
| One account's recent history | `account <id>` |
| Net worth over time | `net-worth --range 1y` |
| Where assets sit | `allocation` |
| Category trends | `trends --range 1y` |
| Brokerage overview | `investments --range 1y` |
| Holdings | `positions` |
| Trades / cash / card at the broker | `activities` |
| Cached orders | `orders` |
| Realized P/L | `realized` |
| Spend by category | `spending --by-category --range 1y` |
| Monthly in/out | `spending --by-month --range 1y` |
| Recent charges | `transactions --limit 50` |
| Leftover / cook vs dine / repeated rides | `living` |
| Which cards the user holds / which card to use for a purchase / what each card earns / rewards missed | `rewards --range 3m` |
| Last sync / freshness | `sync-status` then `doctor` if broken |
| One document covering everything | `snapshot` |

Prefer the narrow command. `snapshot` is large; use it for a first orientation or when several sections are needed at once.

Default data is real (`%USERPROFILE%\.assetscope`). Use `--demo` only when the user asks for demo/sample data. Use `--table` only when a human is reading a list in the terminal.

Flags and argument details live in `help` / `help <command>`. Do not guess them.

## How to read the numbers

- Money is a two-decimal string (`"1234.56"`), usually `{ "amount", "currencyCode" }`. Quote CLI totals. Do not re-derive net worth from a subset of accounts unless the user asked for a subset and the limitation is stated.
- Net worth uses as-of carry-forward: an account with no snapshot on a date keeps its last known balance. Non-USD accounts are excluded from net-worth math.
- Account `category` is `bank|investment|retirement|credit|loan|cash|property|vehicle|other`. `accountClass` is `asset` or `liability`. Liability balances are amount owed.
- Transaction `amount` > 0 is money leaving the account (spend). Amount < 0 is money in. Pending and ignored rows are omitted. Default `transactions` limit is 100.
- `spending --by-category` excludes `TRANSFER_IN`, `TRANSFER_OUT`, `INCOME`, and `LOAN_PAYMENTS`. `spending --by-month` includes all posted movement.
- Investment gain/loss is cost-basis P/L only. A balance increase can be a deposit — never label it a return. `realized` rows with `basisComplete: false` have incomplete lots (history starts after the buy); exclude them from totals and say so.
- `insights` is a small rule engine. An empty `items` array means nothing qualified, not an error.
- `freshness` on accounts: `fresh` (≤24h), `stale` (≤7d), `very_stale`, `attention` (provider error), `manual`, `never`.

## Credit cards and rewards

The app knows which card each credit account is and what it earns. Read that; do not ask the user which cards they hold, and do not infer a card from the bank's account name (Chase reports every Ultimate Rewards card as "Ultimate Rewards®").

- `accounts`: each credit account has `cardProduct` (`{ id, name, source }`, where `source` is `chosen` in the app or `detected`), or `null` when the app has not identified the card. `displayName` is already the product name when the bank's name is generic.
- `rewards`: start here for "which card should I use for X", "am I using the right card", and anything about cash back, points, caps, or quarterly categories.
  - `cards[]`: the product, `baseRate`, and `bonuses`, which are today's rates per category and already count this quarter's activated categories and used-up caps. `unit` is `percent` cash back or `points` per dollar. `capUsage` and `notes` carry caps, required memberships, and travel portals.
  - `categories[]`: the `best` card or cards per spending category and the `runnerUp`. `accountId` refers to `cards[].accountId`. `effectiveRate` is value per dollar as a ratio (`0.0300` is 3%), with points counted at `pointValues[].centsPerPoint`. Ties list cash back first.
  - Rotating categories earn only when `rotatingStatus` is `activated`. `unconfirmed` means the issuer's list is known but the user has not marked the quarter activated in the app, so its rates are left out. Say so and give `activation.url`.
  - `review`: what purchases in the window earned against the best card for each (`missed`, `topMisses`). It is an estimate built from bank categories.
- The categories are `dining`, `groceries`, `gas`, `travel`, `transit`, `drugstores`, `streaming`, `onlineShopping`, `phoneInternet`, `homeImprovement`, `entertainment`, and `other`. A purchase that fits none of them, such as software, an AI subscription, or most services, is `other`. Issuers decide by merchant code, so name the category you assumed.
- Rates are the issuer terms built into the app. Welcome bonuses and card-linked offers are not included. Look up issuer terms only when the user asks to verify them or the app has no product for a card.
- A card with `productId: null` is not identified and is left out of the rankings. `productCandidates` lists the products that fit it. The user sets the card in the app under Card rewards, Your cards; the CLI cannot write it.

## Privacy

This is the user's live financial identity.

- For questions to the user about their own money, read unredacted data.
- Pass `--redact` when writing a file that might be shared, pasted elsewhere, or committed. Redaction replaces institution, account, owner, mask, merchant, and transaction names with stable pseudonyms and leaves every number and every same-institution relationship intact. Only `snapshot`, `transactions`, and `living` can redact; every other command rejects the flag.
- Do not commit snapshots, export CSVs, or backups. Do not write balances into repo files.
- Do not print a full unredacted `snapshot` into a long reply when a targeted command would do.

## Writes

Only `sync`, `backup`, and `export` write. Confirm before running them unless the user already named that action.

`sync` cannot read Windows Credential Manager. The usual result is `{ "status": "queued" }` — a pending-sync marker the desktop app honors on its next check or launch. Say that; do not claim providers were contacted.

## Failures

If the launcher exits `3`, the CLI was not found. Check `config.json` `repo` and `ASSETSCOPE_CLI`.

If a read fails with a lock / "database is locked" message, retry once. If it still fails, say the WinUI app is likely mid-write and try again after a moment.

If `doctor` shows `accountCount: 0`, the real database is empty — do not fall through to `--demo` unless asked.

If `sidecarLockHeldByAnotherProcess` is true, reads should still work; only `sync` defers to the running app.
