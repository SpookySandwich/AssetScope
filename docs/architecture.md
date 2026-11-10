# Architecture

How AssetScope is built and why it behaves the way it does. This describes the
current program, not how it got here.

## Shape

```text
WinUI 3 app (winui/AssetScope.App)        unpackaged, self-contained, x64
        |
        |  HTTP on 127.0.0.1, random port, per-launch bearer token
        v
TypeScript sidecar (native-api)  ->  SQLite (libsql)
```

The app launches the sidecar as a child process and reads a handshake line from
its stdout carrying the port and session token. Nothing listens on a fixed port
and no credential is written to disk for the transport.

Money crosses the API as fixed two-decimal strings and is parsed as `decimal` in
C#. The sidecar aggregates in integer cents, because the database stores REAL
and float drift is not acceptable in balances. Never introduce a `double` into a
monetary path.

## Layout

| Path | Owns |
| --- | --- |
| `native-api/src/core` | Storage, encryption, backup, atomic writes |
| `native-api/src/services` | Financial semantics and provider sync |
| `native-api/src/cli.ts` | Headless CLI over those same services |
| `native-api/openapi.yaml` | The API contract |
| `winui/AssetScope.Core` | Typed API client, settings, platform services |
| `winui/AssetScope.App` | Pages, controls, WinUI shell |
| `.claude/skills/assetscope` | Claude Code skill that invokes the CLI |
| `tests/native-api` | Sidecar tests (vitest) |
| `winui/AssetScope.Tests` | Core and markup tests (xUnit) |
| `build/` | Installer and UI Automation scripts |

The C# client is hand-written rather than generated; a contract test asserts
every route the router registers appears in `openapi.yaml` and vice versa, so
the two cannot drift apart silently.

`native-api/src/cli.ts` is a second client of the same services: no HTTP, no
new financial math. JSON on stdout. It sets `ASSETSCOPE_DATA_DIR` before any
service import, the same constraint `main.ts` has. Read commands skip schema
writes when the database already exists so they can share the file with a
running sidecar. `sync` delegates to `sync-host.ts` and cannot read Credential
Manager.

## Data

| What | Where |
| --- | --- |
| Financial database | `%USERPROFILE%\.assetscope\assetscope.db` |
| Backups | `%USERPROFILE%\.assetscope\backups` |
| App settings and log | `%LOCALAPPDATA%\AssetScope` |
| Demo data | `%LOCALAPPDATA%\AssetScope\Demo` |
| Secrets | Windows Credential Manager only |

Overrides: `ASSETSCOPE_DATA_DIR` for the database, `ASSETSCOPE_APPDATA_DIR` for
settings and log. `DATA_DIR` is captured at module import in the sidecar, so it
must be set before any core module loads — `main.ts` parses `--data-dir`, sets
the variable, then dynamically imports the app.

Secrets never enter `settings.json`. Plaid keeps its Client ID and environment
there; the Secret, and SnapTrade's Consumer Key, live only in Credential
Manager.

On first run an existing `%USERPROFILE%\.fungible` directory is copied forward —
database, settings, and Credential Manager targets — and the original is left
untouched as a rollback copy. Legacy environment overrides are still accepted.
Nothing is ever deleted automatically during migration.

## Financial semantics

All account classification and signed net-worth math live in the sidecar, never
in a page. A page that computed its own totals would eventually disagree with
another page.

- **Classification** is centralized in `services/classify.ts`. Loans are
  liabilities. Manual accounts with a negative latest balance are normalized to
  a liability class with the history flipped to amount-owed.
- **Net worth** series use as-of carry-forward: an account with no snapshot in a
  period keeps its last known balance rather than vanishing from that period.
- **Gain/loss** is only ever cost-basis P/L. A balance increase can be caused by
  a deposit, so it is never labelled an investment return.
- **Realized P/L** is matched FIFO per account and symbol from cached BUY/SELL
  activity. Cost is carried in integer cents and consumed proportionally, so a
  partially sold lot cannot drift. Proceeds come from the activity's own cash
  `amount`, not units × price. Activity with no symbol — SnapTrade reports card
  and cash movement through the same table — never touches a lot.

Cached brokerage history is finite, so shares bought before it begins have no
cost basis. Those sales are returned with `basisComplete: false`, excluded from
totals, and surfaced in the UI. Inventing a basis would misstate a financial
number.

## Providers

Plaid synchronizes bank and credit accounts. SnapTrade synchronizes brokerage
accounts, balances, positions, activities and cached orders. Manual balances are
a first-class path: both providers are optional.

Exactly one provider owns each account's balance series, recorded in
`accounts.balance_source` and enforced in the INSERT itself so no sync path can
bypass it. The authority follows who actually knows the number: SnapTrade for
brokerage accounts, where it reports positions plus the cash sweep Plaid omits,
and Plaid for deposit and credit accounts, where SnapTrade's account total can
bundle balances that are not that account's. A Plaid-discovered brokerage hands
ownership over the first time SnapTrade syncs it.

Without this, both providers wrote snapshots for the same Robinhood accounts and
whichever synced last owned that date; the stored series alternated between two
views of the same money and sawtoothed net worth by tens of thousands. SnapTrade
also back-fills history, so the damage reached back a year.

Provider data is cached atomically — a failed refresh preserves the last
complete snapshot rather than leaving a half-written one. The app never calls
trading, transfer, or real-time order endpoints. That read-only guarantee is
stated to the user once, in Settings, next to the SnapTrade connection.

## UI

Pages are Home, Accounts, Transactions, Investments, Living, Card rewards,
Trends and Settings, built from native
`NavigationView`, `SelectorBar`, `Expander`, `ToggleSwitch`, `ComboBox`,
`TimePicker`, `InfoBar`, dialogs and flyouts. Custom controls are limited to
financial visualizations that WinUI has no equivalent for.

Pages share one small visual vocabulary. The density dictionaries define the
type ramp (`PageTitleStyle`, `CardTitleStyle`, `SectionLabelStyle`,
`StatAmountStyle`, `StatValueStyle`, `CaptionStyle`, `CaptionSecondaryStyle`)
and the inset `StatTileStyle`; `App.xaml` holds `DividerStyle` and
`ReferenceBlueTextBrush`, the per-theme blue for text and glyphs (the brand
blue is too dim on dark cards and stays for fills). Rows assembled in code use
`Controls/Ui.cs` (icon chips, dividers, captions, meters, badges, notes), which
reads the same resources, so code-built content matches markup and follows the
density setting. Text never drops below the caption size except on chart axes.

Accounts, Transactions and Investments size to the window rather than to their data: summary
cards take their natural height, list cards divide the remainder and scroll
inside their own borders, so more rows lengthen a list and never the page. Below
the stacked breakpoint, or on a window too short to hold the summary cards and a
usable list, the page scrolls instead — clipping rows the user cannot reach is
worse than a scrollbar.

Note that a `ScrollViewer` measures its content against infinite height. A star
row inside one resolves to its natural size and silently overflows unless the
content is pinned to the viewport height.

### How settings take effect

No setting asks the user to restart. Where a preference can only be read at
process start, the app restarts *itself* and reopens Settings.

| Applies | Settings |
| --- | --- |
| **Live** | Animations, show cents, display name, run at login, sync on launch, daily sync, provider IDs, Demo Mode, Privacy Mode |
| **Auto-restart** | Theme, language, compact density |
| **Next launch** | Always start on Home, start in Privacy Mode |

`Application.RequestedTheme` and `PrimaryLanguageOverride` are process-start
only, and density is a resource dictionary chosen in `OnLaunched`; all three go
through `SettingsPage.RestartToApplyPresentation`, which sets
`resumeLastPageOnce` so the user lands back on Settings.

Two traps worth knowing before touching startup:

- **Never read `Application.Resources` in the `App` constructor.** It throws
  `E_UNEXPECTED` there, and an exception escaping the constructor kills the
  process at `0xC000027B` before logging exists — an app that will not start and
  leaves no trace. Anything touching app resources belongs in `OnLaunched`.
- **`XamlControlsResources.UseCompactResources` is unavailable** outside
  Experimental builds of the Windows App SDK; it throws. Compact density is
  therefore our own dictionary, `Themes/Density.Compact.xaml`, merged after
  `Density.Comfortable.xaml`. Density-dependent styles live in those files, not
  in `App.xaml`, because a resource dictionary resolves its own keys before its
  merged ones and inline keys could never be overridden.

A cosmetic preference must never be able to block startup, so density failures
are logged and the app continues at the default.

## Install

```powershell
powershell -ExecutionPolicy Bypass -File build/install-winui.ps1
```

Per-user and unpackaged, until a signing or store distribution path is needed.
The script bundles the sidecar with a private `node.exe`, smoke-tests the
handshake before shipping it, publishes the self-contained app, and registers
the Start-menu shortcut, background startup, scheduled sync and Installed Apps
entry. The installed app never depends on `PATH`.

Reinstalling and uninstalling both replace only the program directory. Data and
settings survive.

## Validation

```powershell
npm ci
npm run typecheck
npm test
dotnet test winui/AssetScope.Tests/AssetScope.Tests.csproj -c Release -p:Platform=x64
dotnet build winui/AssetScope.App/AssetScope.App.csproj -c Release -p:Platform=x64
```

Unit, integration, type, contract, build, bundle and install validation are
mandatory. The UI Automation scripts under `build/` run only when explicitly
requested.

### Page actions

Every page action is a visible control: toolbar buttons (Accounts: Add
account, Sort), in-card tabs (Investments: Holdings / Activity / Orders), or
a Settings card (the Living savings goal and merchant labels). `PageHeader` still exposes a
three-dot menu API, but no page uses it. Forms and ledgers never open in
code-built dialogs; only confirmations do.

### Living

The Living page's yardstick is a *typical month*: the sidecar averages the
last three full months that had spending (skipping the first synced month
when history starts after the 5th, since it is partial). Income, rent and
bills, day-to-day spending, and what is kept are all monthly averages, so the
page reads the same on the 2nd as on the 28th. The current month is then
scored against that yardstick (spent so far, confirmed bills still expected,
day-to-day pace once a few days are in, and a projection that assumes the
rest of the month is typical), and bank plus cash balances are expressed as
months of typical spending. A charge seen only once has no known monthly
amount, so it shows N/A and stays out of every number until it repeats.

What a charge counts as (rent, a bill, income, …) comes from the bank's
category and from generic wording. No particular landlord or employer is
named in code. When the wording misses one, the user labels that merchant in
Settings → Bills and income, optionally with what a month of it comes to. The
card shows the user's labels, searches every other merchant, and folds the
automatically detected ones into an expander since those already work
(`GET /v1/living/merchants`). A label is stored per merchant in
`living_merchant_defaults` (`PUT /v1/living/merchant-defaults/{merchantKey}`)
and wins over the automatic kind. The Living page itself stays read-only.

### Card rewards

The sidecar keeps a small catalog of card products (issuer terms as of
October 2026) and matches each credit account to one by the user's choice on
the page, else by name, else by the annual fee it charges: Chase reports every
Ultimate Rewards card as "Ultimate Rewards®", and among those on file only
Sapphire Preferred charges $95. A no-fee card under that name stays
unidentified, with the cards that fit offered first. Accounts, transactions and
the CLI call a card by its product whenever the bank's name does not say which
card it is, and every credit account carries `cardProduct`. Plaid categories are mapped to the categories
issuers pay bonuses on, and each card's value per dollar is its rate times
the cents-per-point value, so 3x points at 1¢ ties 3% cash (ties go to cash).
Rotating categories belong to one quarter: the issuer calendar fills them in
until the user ticks their own (an empty list means "not activated"), and
every past purchase is judged by the list of its own quarter. The app cannot
see activation, so an issuer list earns nothing until the user confirms it
("I've activated it", or ticking their own list); until then the card reports
`rotatingStatus: unconfirmed` and the rates it would pay. The quarterly bonus
is added to the base rate, which counts once. It stacks on a year-round bonus
only where the issuer says so: Freedom Flex dining is 7% (1% + 2% + 4%) and
the original Freedom 5%. Other categories are not stacked; that is a modeling
default for what the releases on file leave unsaid, not a verified issuer rule.

Quarterly caps run from the start of each quarter, even when the review window
starts later. Purchases are tallied in date order: one crossing the cap is
split, and each category then falls to its rate past the cap (the regular rate
for a rotating cap, the base rate for a bonus cap). The plan, the category
table and each card's rates are today's, so a used-up cap moves the category
to the next card. The review's "best possible" is an estimate that starts from
the real purchases: one in the window moves only if another card pays more
with no cap in the way or with room left after every real purchase that quarter,
the largest gain per dollar first. Purchases before the window stay on their
cards, and nothing gives up a bonus it really earned, so the estimate never
falls below what was earned and per-purchase gains add up to the category and
review totals. It is a lower bound: it will not suggest swapping which purchases
used a cap. Cards not identified yet are reported separately instead of counted.
The page also warns when the spending pace would pass the best card's cap.

The issuer calendar has two sources. A built-in table holds quarters checked
by hand, and every sync (`refreshRotatingCalendars`, run alongside the Plaid
items) reads Chase's own quarterly release at its fixed address,
`media.chase.com/news/chase-freedom-<year>-q<n>-categories`, for this quarter
and the next. This is a plain GET with no user data, made only when a card
with rotating categories is on file. The picks come from the page title, the
deadline from the "until <date> to activate" sentence (else the 14th of the
quarter's last month), and picks with no spending category here (one store, a
charity) are kept as "not tracked". A read quarter is final and wins over
the built-in table; a missing, unreadable or failed one is retried at most
every 12 hours, and a failure never fails the sync. Past quarters are never
backfilled, since whether the user activated them is unknown. The page links
to ChaseBonus.com to activate (it needs the user's own sign-in, so the app
never does it) and shows next quarter's list once known.

The page leads with the short answer, built in `AssetScope.Core.RewardsPlan`:
categories grouped under the card to use. A tie goes to the card that already
covers the most categories, and the everyday card's base-rate categories fold
into "everything else". The plan only groups the sidecar's ranking; it never
computes a rate. Each card to carry is one tile (as many to a row as fit at
220px); below it, the review shows three figures and a collected-share meter,
then a category table (category, best card, missed). Card terms sit behind an
info button on each card under Your cards, and how the numbers are worked out
behind the one beside the plan title.
