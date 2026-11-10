// Card rewards: which of the user's credit cards earns the most in each
// spending category, and what recent card spending earned versus the best
// card for each purchase. Reward math stays here, never in a page.
//
// Rates are integers in tenths of a point (or percent) per dollar, and point
// values in hundredths of a cent, so their product E is an exact integer:
// E / 1000 is the effective percent and a purchase of A cents earns
// A * E / 100000 cents. Sums stay in integers until the final rounding.
import { db } from '../core/database.js';
import { getSetting, setSetting } from '../core/settings.js';
import { validation, notFound } from '../errors.js';
import { loadAccounts, type AccountRecord } from './networth.js';
import { normalizeCategory } from './categories.js';
import { toCents, centsToAmount } from './money.js';
import {
  CHASE_ACTIVATION_URL, fetchChaseAnnouncement, nextQuarter, quarterOf, quarterStart, usualActivateBy, type AnnouncementCheck,
} from './rotating-categories.js';

export { quarterOf };

export const REWARD_CATEGORIES = [
  'dining', 'groceries', 'gas', 'travel', 'transit', 'drugstores', 'streaming',
  'onlineShopping', 'phoneInternet', 'homeImprovement', 'entertainment', 'other',
] as const;
export type RewardCategory = typeof REWARD_CATEGORIES[number];

type CurrencyId = 'cash' | 'chase-ur' | 'bofa-points';

type CurrencyDef = {
  id: CurrencyId;
  name: string;
  /** Hundredths of a cent per point. */
  defaultValue: number;
  /** Whether the user may change the value (flexible points only). */
  adjustable: boolean;
};

const CURRENCIES: Record<CurrencyId, CurrencyDef> = {
  cash: { id: 'cash', name: 'Cash back', defaultValue: 100, adjustable: false },
  // 1¢ is the cash-out floor every Ultimate Rewards card has. Travel and
  // transfer partners can be worth more, which is the user's call to make.
  'chase-ur': { id: 'chase-ur', name: 'Chase Ultimate Rewards', defaultValue: 100, adjustable: true },
  // Travel Rewards points are worth 1¢ only as a travel or dining credit.
  'bofa-points': { id: 'bofa-points', name: 'Bank of America points', defaultValue: 100, adjustable: false },
};

const POINT_VALUE_MIN = 50;
const POINT_VALUE_MAX = 300;

/** The 3% "choice" groups a Customized Cash card can pick from. */
export const CHOICE_OPTIONS = [
  'gas', 'onlineShopping', 'dining', 'travel', 'drugstores', 'homeImprovement',
] as const;
export type ChoiceOption = typeof CHOICE_OPTIONS[number];

// Bank of America's online shopping includes cable, streaming, internet and
// phone plans; its travel includes rideshare, taxis, transit and tolls.
const CHOICE_COVERS: Record<ChoiceOption, RewardCategory[]> = {
  gas: ['gas'],
  onlineShopping: ['onlineShopping', 'streaming', 'phoneInternet'],
  dining: ['dining'],
  travel: ['travel', 'transit'],
  drugstores: ['drugstores'],
  homeImprovement: ['homeImprovement'],
};

export type ProductNote =
  | { kind: 'quarterlyCap'; amount: string; scope: 'bonus' | 'rotating' }
  | { kind: 'onlineGroceryOnly' }
  | { kind: 'membershipRequired'; membership: string }
  | { kind: 'portal'; label: string; rate: string }
  | { kind: 'pooledPoints' }
  | { kind: 'bofaBoost' }
  | { kind: 'travelDiningRedemption' };

type ProductDef = {
  id: string;
  name: string;
  issuer: string;
  currency: CurrencyId;
  base: number;
  bonus: Partial<Record<RewardCategory, number>>;
  choice?: { rate: number; defaultOption: ChoiceOption };
  rotating?: {
    /** Added to the base rate, which is earned only once per purchase. */
    bonus: number;
    /** Year-round bonuses the issuer says the quarterly bonus adds to. */
    stacksOn?: RewardCategory[];
    activationUrl: string;
    /** Reads the issuer's announcement for one quarter. */
    announcement?: (quarter: string, fetchImpl?: typeof fetch) => Promise<AnnouncementCheck>;
  };
  /** Bonus spending that stops earning the bonus each quarter past this many cents. */
  cap?: { cents: number; scope: 'bonus' | 'rotating' };
  portal?: { rate: number; label: string };
  /** Freedom cards earn cash-value points unless a premium UR card pools them. */
  poolsWithPremium?: boolean;
  premiumPoints?: boolean;
  /** Cents charged each year, for cards with a fee. */
  annualFee?: number;
  notes: ProductNote[];
  detect?: (name: string, institution: string) => boolean;
};

// Terms as published by each issuer, checked Oct 22, 2026. Merchant-specific
// offers (Lyft, card-linked dining offers) are left out: Plaid categories
// cannot tell those merchants apart reliably.
const PRODUCTS: ProductDef[] = [
  {
    id: 'chase-sapphire-preferred',
    name: 'Chase Sapphire Preferred',
    issuer: 'Chase',
    currency: 'chase-ur',
    base: 10,
    // Gas and EV charging became 3x on June 15, 2026. Chase's travel
    // definition includes taxis, transit, tolls and parking.
    bonus: { dining: 30, streaming: 30, gas: 30, travel: 20, transit: 20 },
    portal: { rate: 50, label: 'Chase Travel' },
    premiumPoints: true,
    annualFee: 9_500,
    notes: [{ kind: 'onlineGroceryOnly' }],
    detect: (name) => /sapphire\s+preferred/i.test(name),
  },
  {
    id: 'chase-freedom-flex',
    name: 'Chase Freedom Flex',
    issuer: 'Chase',
    currency: 'chase-ur',
    base: 10,
    bonus: { dining: 30, drugstores: 30 },
    // Chase's Q4 2026 release: dining is 7% in total, 1% base + 2% dining + 4%
    // quarterly. It says nothing of the kind for drugstores, so those do not stack.
    rotating: { bonus: 40, stacksOn: ['dining'], activationUrl: CHASE_ACTIVATION_URL, announcement: fetchChaseAnnouncement },
    cap: { cents: 150_000, scope: 'rotating' },
    portal: { rate: 50, label: 'Chase Travel' },
    poolsWithPremium: true,
    notes: [
      { kind: 'quarterlyCap', amount: '1500.00', scope: 'rotating' },
      { kind: 'pooledPoints' },
    ],
    detect: (name) => /freedom\s+flex/i.test(name),
  },
  {
    id: 'chase-freedom-unlimited',
    name: 'Chase Freedom Unlimited',
    issuer: 'Chase',
    currency: 'chase-ur',
    base: 15,
    bonus: { dining: 30, drugstores: 30 },
    portal: { rate: 50, label: 'Chase Travel' },
    poolsWithPremium: true,
    notes: [{ kind: 'pooledPoints' }],
    detect: (name) => /freedom\s+unlimited/i.test(name),
  },
  {
    id: 'chase-freedom',
    name: 'Chase Freedom',
    issuer: 'Chase',
    currency: 'chase-ur',
    base: 10,
    bonus: {},
    rotating: { bonus: 40, activationUrl: CHASE_ACTIVATION_URL, announcement: fetchChaseAnnouncement },
    cap: { cents: 150_000, scope: 'rotating' },
    poolsWithPremium: true,
    notes: [
      { kind: 'quarterlyCap', amount: '1500.00', scope: 'rotating' },
      { kind: 'pooledPoints' },
    ],
    // Flex and Unlimited are matched above; Rise and Student have no rotating categories.
    detect: (name, institution) => /\bfreedom\b(?!\s+(rise|student))/i.test(name) && /chase/i.test(`${institution} ${name}`),
  },
  {
    id: 'bofa-customized-cash',
    name: 'Customized Cash Rewards',
    issuer: 'Bank of America',
    currency: 'cash',
    base: 10,
    bonus: { groceries: 20 },
    choice: { rate: 30, defaultOption: 'gas' },
    cap: { cents: 250_000, scope: 'bonus' },
    // BofA Rewards tiers add 10–75%; the tier is not visible to the app.
    notes: [{ kind: 'quarterlyCap', amount: '2500.00', scope: 'bonus' }, { kind: 'bofaBoost' }],
    detect: (name) => /customized\s+cash/i.test(name),
  },
  {
    id: 'bofa-travel-rewards',
    name: 'Travel Rewards',
    issuer: 'Bank of America',
    currency: 'bofa-points',
    base: 15,
    bonus: {},
    notes: [{ kind: 'travelDiningRedemption' }, { kind: 'bofaBoost' }],
    detect: (name, institution) => /travel\s+rewards/i.test(name) && /bank of america/i.test(institution),
  },
  {
    // The travel-portal bonus ended with the Sept 1, 2026 terms.
    id: 'robinhood-gold-card',
    name: 'Robinhood Gold Card',
    issuer: 'Robinhood',
    currency: 'cash',
    base: 30,
    bonus: {},
    notes: [{ kind: 'membershipRequired', membership: 'Robinhood Gold' }],
    detect: (name, institution) => /robinhood/i.test(institution) || /robinhood/i.test(name),
  },
  {
    id: 'citi-double-cash',
    name: 'Citi Double Cash',
    issuer: 'Citi',
    currency: 'cash',
    base: 20,
    bonus: {},
    portal: { rate: 50, label: 'Citi Travel' },
    notes: [],
    detect: (name) => /double\s+cash/i.test(name),
  },
  { id: 'flat-2', name: 'Other card, 2% on everything', issuer: '', currency: 'cash', base: 20, bonus: {}, notes: [] },
  { id: 'flat-1-5', name: 'Other card, 1.5% on everything', issuer: '', currency: 'cash', base: 15, bonus: {}, notes: [] },
  { id: 'flat-1', name: 'Other card, 1% on everything', issuer: '', currency: 'cash', base: 10, bonus: {}, notes: [] },
  { id: 'no-rewards', name: 'No rewards', issuer: '', currency: 'cash', base: 0, bonus: {}, notes: [] },
];

const PRODUCT_BY_ID = new Map(PRODUCTS.map((p) => [p.id, p]));

type CalendarEntry = { categories: RewardCategory[]; untracked: string[]; activateBy: string };

const CHASE_FREEDOM_CALENDAR: Record<string, CalendarEntry> = {
  '2026-Q4': { categories: ['groceries', 'dining'], untracked: ['American Red Cross donations'], activateBy: '2026-12-14' },
  // Previewed in Chase's Sept 21, 2026 Freedom Flex release, ahead of the
  // quarter's own page; the deadline is Chase's usual one until that page says.
  '2027-Q1': { categories: ['groceries', 'streaming'], untracked: [], activateBy: '2027-03-14' },
};

// Rotating categories as the issuer announced them. The sync also reads each
// quarter's announcement (see refreshRotatingCalendars), which takes
// precedence; a quarter missing from both reads as "not set" until the user
// ticks its categories on the page.
const ROTATING_CALENDAR: Record<string, Record<string, CalendarEntry>> = {
  'chase-freedom-flex': CHASE_FREEDOM_CALENDAR,
  'chase-freedom': CHASE_FREEDOM_CALENDAR,
};

// ── Purchase categorization ──────────────────────────────────────────────────

const GROCER_RE = /\b(publix|kroger|whole foods|trader joe'?s?|aldi|h-?e-?b|safeway|wegmans|sprouts|h ?mart|lidl|food lion|harris teeter|albertsons|ralphs|meijer|winn-?dixie|99 ranch|supermarket|grocery)\b/i;
// Superstores and warehouse clubs sell groceries but are not grocery stores to issuers.
const NOT_GROCER_RE = /\b(walmart|wal-mart|target|costco|sam'?s club|bj'?s)\b/i;
const STREAMING_RE = /\b(netflix|hulu|disney\s?(\+|plus)|spotify|youtube|hbo|max\.com|peacock|paramount|apple tv|apple music|sling|fubo|crunchyroll|tidal|pandora|siriusxm)\b/i;

/**
 * Map a Plaid category pair to the category issuers pay bonuses on, or null
 * when the row is not a purchase (transfers, income, payments, fees).
 * Issuers decide by merchant code; this is the closest read Plaid allows.
 */
export function rewardCategoryFor(category: string, detailed: string | null, merchant: string): RewardCategory | null {
  const primary = normalizeCategory(category);
  if (['TRANSFER_IN', 'TRANSFER_OUT', 'TRANSFER', 'INCOME', 'LOAN_PAYMENTS', 'BANK_FEES'].includes(primary)) return null;
  const detail = detailed && detailed.startsWith(primary + '_') ? detailed : null;

  if (STREAMING_RE.test(merchant) && ['ENTERTAINMENT', 'GENERAL_SERVICES', 'GENERAL_MERCHANDISE'].includes(primary)) {
    return 'streaming';
  }
  switch (primary) {
    case 'FOOD_AND_DRINK':
      switch (detail) {
        case 'FOOD_AND_DRINK_GROCERIES': return NOT_GROCER_RE.test(merchant) ? 'other' : 'groceries';
        case 'FOOD_AND_DRINK_VENDING_MACHINES': return 'other';
        case null:
          if (NOT_GROCER_RE.test(merchant)) return 'other';
          return GROCER_RE.test(merchant) ? 'groceries' : 'dining';
        default: return 'dining';
      }
    case 'TRANSPORTATION':
      if (detail === 'TRANSPORTATION_GAS') return 'gas';
      return detail === 'TRANSPORTATION_OTHER_TRANSPORTATION' ? 'other' : 'transit';
    case 'TRAVEL': return 'travel';
    case 'MEDICAL': return detail === 'MEDICAL_PHARMACIES_AND_SUPPLEMENTS' ? 'drugstores' : 'other';
    case 'ENTERTAINMENT': return 'entertainment';
    case 'GENERAL_MERCHANDISE':
      return detail === 'GENERAL_MERCHANDISE_ONLINE_MARKETPLACES' ? 'onlineShopping' : 'other';
    case 'HOME_IMPROVEMENT': return 'homeImprovement';
    case 'RENT_AND_UTILITIES':
      return detail === 'RENT_AND_UTILITIES_TELEPHONE' || detail === 'RENT_AND_UTILITIES_INTERNET_AND_CABLE'
        ? 'phoneInternet'
        : 'other';
    default: return 'other';
  }
}

// ── Stored preferences ───────────────────────────────────────────────────────

const PREF_CARDS = 'rewards_cards';
const PREF_POINT_VALUES = 'rewards_point_values';
const PREF_ANNOUNCEMENTS = 'rewards_rotating_announcements';

// A quarter whose page is missing or unreadable is asked for again after this.
const ANNOUNCEMENT_RECHECK_MS = 12 * 60 * 60 * 1000;

/** The last look at one quarter's announcement; categories are set once it was read. */
type AnnouncementRecord = {
  state: 'read' | 'notPosted' | 'unreadable' | 'failed';
  checkedAt: string;
  /** The page, once it is up. */
  url?: string;
} & Partial<CalendarEntry>;

/** Product id → quarter → record. */
type AnnouncementStore = Record<string, Record<string, AnnouncementRecord>>;

type CardPreference = {
  productId?: string;
  choiceCategory?: ChoiceOption;
  rotating?: { quarter: string; categories: RewardCategory[] };
  /** Quarters the user says they activated with the issuer. */
  activated?: string[];
};

async function readJson<T>(key: string): Promise<T> {
  const raw = await getSetting(key);
  if (!raw) return {} as T;
  try {
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? parsed as T : {} as T;
  } catch {
    return {} as T;
  }
}

function localToday(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

function addDays(iso: string, days: number): string {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

// ── Card resolution ──────────────────────────────────────────────────────────

/**
 * Whether a quarter's list earns: "activated" when the user ticked their own
 * list or marked the quarter, "notActivated" when they cleared it, and
 * "unconfirmed" for an issuer list the app cannot tell was activated.
 */
type RotatingStatus = 'activated' | 'notActivated' | 'unconfirmed';

type ResolvedCard = {
  account: AccountRecord;
  product: ProductDef | null;
  productSource: 'chosen' | 'detected' | null;
  /** What a detected product was recognized from. */
  detectedBy: 'name' | 'annualFee' | null;
  choice: ChoiceOption | null;
  rotating: RewardCategory[];
  rotatingSource: 'chosen' | 'calendar' | null;
  rotatingStatus: RotatingStatus | null;
  rotatingActivateBy: string | null;
  rotatingUntracked: string[];
  /** The last look at this quarter's announcement, when the sync has made one. */
  rotatingAnnouncement: AnnouncementRecord | null;
  activatedQuarters: string[];
};

// Chase reports every Ultimate Rewards card under this one account name.
const CHASE_UR_NAME = /ultimate rewards/i;

/** Annual fees charged to each credit card, in cents, by account id. */
async function loadAnnualFees(): Promise<Map<string, Set<number>>> {
  const res = await db.execute(
    `SELECT t.account_id, t.amount FROM transactions t JOIN accounts a ON a.id = t.account_id
      WHERE a.type = 'credit' AND t.pending = 0 AND t.amount > 0 AND t.name LIKE '%annual%fee%'`,
  );
  const fees = new Map<string, Set<number>>();
  for (const row of res.rows as unknown as { account_id: string; amount: number }[]) {
    const charged = fees.get(row.account_id) ?? new Set<number>();
    fees.set(row.account_id, charged.add(toCents(row.amount)));
  }
  return fees;
}

function detectByName(account: AccountRecord): ProductDef | null {
  const institution = account.institution_name ?? '';
  return PRODUCTS.find((p) => p.detect?.(account.name, institution)) ?? null;
}

type Identified = { product: ProductDef; source: 'chosen' | 'detected'; detectedBy: 'name' | 'annualFee' | null };

/** The user's choice, else the account name, else the annual fee the card charges. */
function identifyProduct(account: AccountRecord, pref: CardPreference | undefined, fees: Set<number> | undefined): Identified | null {
  const chosen = pref?.productId ? PRODUCT_BY_ID.get(pref.productId) : undefined;
  if (chosen) return { product: chosen, source: 'chosen', detectedBy: null };
  const named = detectByName(account);
  if (named) return { product: named, source: 'detected', detectedBy: 'name' };
  // The shared Ultimate Rewards name leaves the fee as the only difference the
  // bank reports. A business card with the same fee needs the user's own pick.
  if (fees && CHASE_UR_NAME.test(account.name)) {
    const charged = PRODUCTS.filter((p) => p.currency === 'chase-ur' && p.annualFee !== undefined && fees.has(p.annualFee));
    if (charged.length === 1) return { product: charged[0], source: 'detected', detectedBy: 'annualFee' };
  }
  return null;
}

/** Products that fit a card the bank data cannot pin down, likeliest first. */
function productCandidates(account: AccountRecord): ProductDef[] {
  if (!CHASE_UR_NAME.test(account.name)) return [];
  // A fee would have identified the card, so the cards without one come first.
  const family = PRODUCTS.filter((p) => p.currency === 'chase-ur');
  return [...family.filter((p) => p.annualFee === undefined), ...family.filter((p) => p.annualFee !== undefined)];
}

/**
 * What to call an account: the user's nickname, else the card product when the
 * bank's own name does not say which card it is, else the bank's name.
 */
function displayNameOf(account: AccountRecord, product: ProductDef | null): string {
  if (account.nickname !== null && account.nickname !== undefined) return account.nickname;
  if (!product || !product.issuer) return account.name;
  return detectByName(account)?.id === product.id ? account.name : product.name;
}

export type CardProductRef = { id: string; name: string; source: 'chosen' | 'detected' };
export type AccountIdentity = { displayName: string; cardProduct: CardProductRef | null };

/** The display name of each account and, for credit cards, the identified product. */
export async function accountIdentities(accounts: AccountRecord[]): Promise<Map<string, AccountIdentity>> {
  const hasCards = accounts.some((a) => a.type === 'credit');
  const [prefs, fees] = hasCards
    ? await Promise.all([readJson<Record<string, CardPreference>>(PREF_CARDS), loadAnnualFees()])
    : [{} as Record<string, CardPreference>, new Map<string, Set<number>>()];
  return new Map(accounts.map((account) => {
    const identified = account.type === 'credit' ? identifyProduct(account, prefs[account.id], fees.get(account.id)) : null;
    return [account.id, {
      displayName: displayNameOf(account, identified?.product ?? null),
      cardProduct: identified
        ? { id: identified.product.id, name: identified.product.name, source: identified.source }
        : null,
    }];
  }));
}

/** The issuer's list for one quarter: the announcement the sync read, else the built-in calendar. */
function calendarEntry(product: ProductDef, quarter: string, announcements: AnnouncementStore): CalendarEntry | null {
  const record = announcements[product.id]?.[quarter];
  if (record?.state === 'read' && record.categories && record.activateBy) {
    return { categories: record.categories, untracked: record.untracked ?? [], activateBy: record.activateBy };
  }
  return ROTATING_CALENDAR[product.id]?.[quarter] ?? null;
}

/** Resolves the user's cards for any quarter, so a purchase is judged by that quarter's list. */
async function cardResolver(): Promise<{ cardsFor: (quarter: string) => ResolvedCard[]; announcements: AnnouncementStore }> {
  const [prefs, announcements, accounts, fees] = await Promise.all([
    readJson<Record<string, CardPreference>>(PREF_CARDS),
    readJson<AnnouncementStore>(PREF_ANNOUNCEMENTS),
    loadAccounts().then((all) => all.filter((a) => a.type === 'credit')),
    loadAnnualFees(),
  ]);
  const cache = new Map<string, ResolvedCard[]>();
  const cardsFor = (quarter: string) => {
    let cards = cache.get(quarter);
    if (!cards) {
      cards = resolveCards(accounts, prefs, announcements, fees, quarter);
      cache.set(quarter, cards);
    }
    return cards;
  };
  return { cardsFor, announcements };
}

function resolveCards(
  accounts: AccountRecord[],
  prefs: Record<string, CardPreference>,
  announcements: AnnouncementStore,
  fees: Map<string, Set<number>>,
  quarter: string,
): ResolvedCard[] {
  return accounts.map((account) => {
    const pref = prefs[account.id] ?? {};
    const identified = identifyProduct(account, pref, fees.get(account.id));
    const product = identified?.product ?? null;
    const choice = product?.choice
      ? (pref.choiceCategory && CHOICE_OPTIONS.includes(pref.choiceCategory) ? pref.choiceCategory : product.choice.defaultOption)
      : null;
    // Rotating categories belong to one quarter, so last quarter's bonus never
    // carries into this one. The user's own list (even an empty one, for a
    // quarter they did not activate) wins over the issuer calendar.
    const calendar = product?.rotating ? calendarEntry(product, quarter, announcements) : null;
    const own = product?.rotating && pref.rotating?.quarter === quarter ? pref.rotating.categories : null;
    const activatedQuarters = product?.rotating ? pref.activated ?? [] : [];
    const rotatingStatus: RotatingStatus | null = own
      ? (own.length > 0 ? 'activated' : 'notActivated')
      : calendar
        ? (activatedQuarters.includes(quarter) ? 'activated' : 'unconfirmed')
        : null;
    return {
      account,
      product,
      productSource: identified?.source ?? null,
      detectedBy: identified?.detectedBy ?? null,
      choice,
      rotating: own ?? calendar?.categories ?? [],
      rotatingSource: own ? 'chosen' : calendar ? 'calendar' : null,
      rotatingStatus,
      rotatingActivateBy: calendar?.activateBy ?? null,
      rotatingUntracked: calendar?.untracked ?? [],
      rotatingAnnouncement: product?.rotating ? announcements[product.id]?.[quarter] ?? null : null,
      activatedQuarters,
    };
  });
}

/** Points-per-dollar rate (tenths) a card earns in one category all year. */
function regularRate(card: ResolvedCard, category: RewardCategory): number {
  const p = card.product!;
  let rate = p.bonus[category] ?? p.base;
  if (p.choice && card.choice && CHOICE_COVERS[card.choice].includes(category)) rate = Math.max(rate, p.choice.rate);
  return Math.max(rate, p.base);
}

/** The rate in one of the quarter's categories, while activated and under the cap. */
function rotatingRate(card: ResolvedCard, category: RewardCategory): number {
  const p = card.product!;
  const { bonus, stacksOn } = p.rotating!;
  const regular = regularRate(card, category);
  return stacksOn?.includes(category) ? regular + bonus : Math.max(regular, p.base + bonus);
}

/** The categories paying the quarterly bonus; the issuer's list alone pays nothing. */
function earningRotating(card: ResolvedCard): RewardCategory[] {
  return card.rotatingStatus === 'activated' ? card.rotating : [];
}

/** Points-per-dollar rate (tenths) a card earns in one category, cap aside. */
function earnRate(card: ResolvedCard, category: RewardCategory): number {
  return earningRotating(card).includes(category) ? rotatingRate(card, category) : regularRate(card, category);
}

/** Categories whose bonus counts toward the card's quarterly cap. */
function cappedCategories(card: ResolvedCard): RewardCategory[] {
  const p = card.product!;
  if (!p.cap) return [];
  if (p.cap.scope === 'rotating') return earningRotating(card);
  return REWARD_CATEGORIES.filter((c) => !card.rotating.includes(c) && earnRate(card, c) > p.base);
}

/** The rate once the quarterly cap is used up. */
function overCapRate(card: ResolvedCard, category: RewardCategory): number {
  return card.product!.cap?.scope === 'rotating' ? regularRate(card, category) : card.product!.base;
}

/** The rate on the next purchase, given how much of the cap this quarter's purchases used. */
function rateAfter(card: ResolvedCard, category: RewardCategory, capUsed: number): number {
  const { cap } = card.product!;
  return cap && capUsed >= cap.cents && cappedCategories(card).includes(category)
    ? overCapRate(card, category)
    : earnRate(card, category);
}

/** Cents times rate for one purchase; the part past the cap earns the rate past it. */
function purchaseEarning(card: ResolvedCard, category: RewardCategory, cents: number, capUsed: number): number {
  const { cap } = card.product!;
  if (!cap || !cappedCategories(card).includes(category)) return cents * earnRate(card, category);
  const within = Math.min(cents, Math.max(cap.cents - capUsed, 0));
  return within * earnRate(card, category) + (cents - within) * overCapRate(card, category);
}

function pointValue(card: ResolvedCard, values: Record<string, number>, holdsPremium: boolean): number {
  const p = card.product!;
  const currency = CURRENCIES[p.currency];
  if (!currency.adjustable) return currency.defaultValue;
  if (p.poolsWithPremium && !holdsPremium) return currency.defaultValue;
  return values[p.currency] ?? currency.defaultValue;
}

const formatRate = (tenths: number) => (tenths % 10 === 0 ? String(tenths / 10) : (tenths / 10).toFixed(1));
const effectiveRatio = (e: number) => (e / 100000).toFixed(4);
const rewardCents = (sumAmountTimesE: number) => Math.round(sumAmountTimesE / 100000);

// ── DTOs ─────────────────────────────────────────────────────────────────────

export type RewardRangeKey = '1m' | '3m' | '1y';
const RANGE_DAYS: Record<RewardRangeKey, number> = { '1m': 30, '3m': 90, '1y': 365 };

type CardRateDto = {
  accountId: string;
  rate: string;
  unit: 'points' | 'percent';
  effectiveRate: string;
};

export type RewardsOverviewDto = {
  asOf: string;
  quarter: string;
  window: { range: RewardRangeKey; from: string; to: string };
  pointValues: {
    currency: string;
    name: string;
    centsPerPoint: string;
    defaultCentsPerPoint: string;
    minimum: string;
    maximum: string;
  }[];
  products: {
    id: string;
    name: string;
    issuer: string;
    unit: 'points' | 'percent';
    hasChoice: boolean;
    hasRotating: boolean;
  }[];
  choiceOptions: ChoiceOption[];
  cards: {
    accountId: string;
    displayName: string;
    institutionName: string | null;
    mask: string | null;
    productId: string | null;
    productName: string | null;
    productSource: 'chosen' | 'detected' | null;
    /** What a detected product was recognized from: the account name or the annual fee charged. */
    productDetectedBy: 'name' | 'annualFee' | null;
    /** For a card not identified yet, the products that fit what the bank reports, likeliest first. */
    productCandidates: string[];
    choiceCategory: ChoiceOption | null;
    rotatingCategories: RewardCategory[];
    rotatingSource: 'chosen' | 'calendar' | null;
    /** Only "activated" earns the bonus; "unconfirmed" means the app cannot tell. */
    rotatingStatus: RotatingStatus | null;
    /** What each of this quarter's categories pays while activated and under the cap. */
    rotatingRates: { category: RewardCategory; rate: string }[];
    rotatingActivateBy: string | null;
    /** The user said they activated this quarter's categories with the issuer. */
    rotatingActivated: boolean;
    /** This quarter's picks with no spending category here, such as one store or a charity. */
    rotatingUntracked: string[];
    /** The issuer's page for this quarter once posted; "unreadable" when its list could not be read. */
    rotatingAnnouncement: { url: string; state: 'read' | 'unreadable' } | null;
    /** Next quarter's list once named; open once its own page is up and activation has begun. */
    rotatingNext: {
      quarter: string;
      categories: RewardCategory[];
      untracked: string[];
      activateBy: string;
      open: boolean;
      activated: boolean;
    } | null;
    /** Where to activate rotating categories while a quarter is open for it and not yet marked activated. */
    activation: { url: string; quarter: string; activateBy: string } | null;
    /** This quarter's use of the shared cap, while a capped bonus is earning. */
    capUsage: { cap: string; used: string; remaining: string } | null;
    unit: 'points' | 'percent' | null;
    baseRate: string | null;
    bonuses: { category: RewardCategory; rate: string }[];
    notes: ProductNote[];
    spend: string;
    earned: string | null;
    transactionCount: number;
  }[];
  categories: {
    category: RewardCategory;
    spend: string;
    transactionCount: number;
    best: CardRateDto[];
    runnerUp: CardRateDto | null;
    portal: { accountId: string; label: string; rate: string; unit: 'points' | 'percent'; effectiveRate: string } | null;
    /** The best card's bonus stops at a quarterly cap this spending pace would pass. */
    capWarning: { accountId: string; cap: string; quarterlySpend: string } | null;
  }[];
  review: {
    spend: string;
    earned: string;
    bestPossible: string;
    missed: string;
    transactionCount: number;
    unidentifiedSpend: string;
    byCategory: {
      category: RewardCategory;
      spend: string;
      earned: string;
      bestPossible: string;
      missed: string;
      mostUsedAccountId: string | null;
      bestAccountId: string | null;
    }[];
    topMisses: {
      transactionId: string;
      date: string;
      name: string;
      merchantName: string | null;
      amount: string;
      category: RewardCategory;
      accountId: string;
      bestAccountId: string;
      earned: string;
      bestPossible: string;
      missed: string;
    }[];
  };
};

type PurchaseRow = {
  id: string;
  account_id: string;
  date: string;
  name: string;
  merchant_name: string | null;
  amount: number;
  category: string;
  detailed: string | null;
};

const unitOf = (p: ProductDef): 'points' | 'percent' => (p.currency === 'cash' ? 'percent' : 'points');

export async function rewardsOverview(opts: { range?: RewardRangeKey; today?: string } = {}): Promise<RewardsOverviewDto> {
  const range = opts.range ?? '3m';
  const today = opts.today ?? localToday();
  const from = addDays(today, -(RANGE_DAYS[range] - 1));
  const quarter = quarterOf(today);

  const [{ cardsFor, announcements }, storedValues] = await Promise.all([
    cardResolver(),
    readJson<Record<string, number>>(PREF_POINT_VALUES),
  ]);
  const cards = cardsFor(quarter);
  const values: Record<string, number> = {};
  for (const [currency, value] of Object.entries(storedValues)) {
    if (Number.isInteger(value) && value >= POINT_VALUE_MIN && value <= POINT_VALUE_MAX) values[currency] = value;
  }
  const known = cards.filter((c) => c.product !== null);
  const holdsPremium = known.some((c) => c.product!.premiumPoints);
  const value = (card: ResolvedCard) => pointValue(card, values, holdsPremium);

  // Each card's quarterly cap as the purchases really used it.
  const capUsed = new Map<string, number>();
  const capKey = (q: string, card: ResolvedCard) => `${q}:${card.account.id}`;
  const countTowardCap = (tally: Map<string, number>, q: string, card: ResolvedCard, category: RewardCategory, cents: number) => {
    if (cappedCategories(card).includes(category)) tally.set(capKey(q, card), (tally.get(capKey(q, card)) ?? 0) + cents);
  };

  const rateNow = (card: ResolvedCard, category: RewardCategory) =>
    rateAfter(card, category, capUsed.get(capKey(quarter, card)) ?? 0);
  const effective = (card: ResolvedCard, category: RewardCategory) => rateNow(card, category) * value(card);

  const rateDto = (card: ResolvedCard, category: RewardCategory): CardRateDto => ({
    accountId: card.account.id,
    rate: formatRate(rateNow(card, category)),
    unit: unitOf(card.product!),
    effectiveRate: effectiveRatio(effective(card, category)),
  });

  // Ties go to cash, whose value is certain, then to the higher stated rate.
  const byWorth = (worth: (c: ResolvedCard) => number, rate: (c: ResolvedCard) => number) =>
    (a: ResolvedCard, b: ResolvedCard) =>
      worth(b) - worth(a)
      || (a.product!.currency === 'cash' ? 0 : 1) - (b.product!.currency === 'cash' ? 0 : 1)
      || rate(b) - rate(a)
      || a.account.name.localeCompare(b.account.name);

  // Read only once every purchase has been tallied against the caps.
  const rankings = new Map<RewardCategory, ResolvedCard[]>();
  const ranked = (category: RewardCategory) => {
    let order = rankings.get(category);
    if (!order) {
      order = [...known].sort(byWorth((c) => effective(c, category), (c) => rateNow(c, category)));
      rankings.set(category, order);
    }
    return order;
  };

  const res = await db.execute({
    sql: `SELECT t.id, t.account_id, t.date, t.name, t.merchant_name, t.amount,
                 COALESCE(t.manual_category, t.category, t.raw_category, 'OTHER') AS category,
                 CASE WHEN t.manual_category IS NULL THEN t.raw_category END AS detailed
            FROM transactions t JOIN accounts a ON a.id = t.account_id
           WHERE t.date >= ? AND t.date <= ? AND t.pending = 0 AND t.ignored = 0 AND t.amount > 0
             AND a.type = 'credit' AND a.archived_at IS NULL AND a.excluded = 0
           ORDER BY t.date, t.id`,
    // Caps run from the start of the quarter, even when the window starts later.
    args: [quarterStart(quarterOf(from)), today],
  });

  const cardIndex = new Map<string, Map<string, ResolvedCard>>();
  const cardIn = (q: string, accountId: string) => {
    let byId = cardIndex.get(q);
    if (!byId) {
      byId = new Map(cardsFor(q).map((c) => [c.account.id, c]));
      cardIndex.set(q, byId);
    }
    return byId.get(accountId);
  };
  const perCard = new Map<string, { cents: number; e: number; count: number }>();
  // `byAccount` and the known* sums cover identified cards only, like the review totals.
  const perCategory = new Map<RewardCategory, {
    cents: number; count: number; e: number; bestE: number; knownCents: number; byAccount: Map<string, number>;
  }>();
  const misses: (RewardsOverviewDto['review']['topMisses'][number] & { missedCents: number })[] = [];
  const reviewed: { row: PurchaseRow; quarter: string; card: ResolvedCard; category: RewardCategory; cents: number; usedSum: number }[] = [];
  let totalCents = 0;
  let totalE = 0;
  let totalBestE = 0;
  let totalCount = 0;
  let unidentifiedCents = 0;

  for (const row of res.rows as unknown as PurchaseRow[]) {
    const purchaseQuarter = quarterOf(row.date);
    const card = cardIn(purchaseQuarter, row.account_id);
    if (!card) continue;
    const merchant = row.merchant_name ?? row.name;
    const category = rewardCategoryFor(row.category, row.detailed, merchant);
    if (!category) continue;
    const cents = toCents(row.amount);

    // What the purchase earned, worked out before it uses up any of the caps.
    let usedSum = 0;
    if (card.product) {
      usedSum = purchaseEarning(card, category, cents, capUsed.get(capKey(purchaseQuarter, card)) ?? 0) * value(card);
      countTowardCap(capUsed, purchaseQuarter, card, category, cents);
    }
    if (row.date < from) continue;

    const cat = perCategory.get(category)
      ?? { cents: 0, count: 0, e: 0, bestE: 0, knownCents: 0, byAccount: new Map<string, number>() };
    perCategory.set(category, cat);
    cat.cents += cents;
    cat.count += 1;

    const stats = perCard.get(card.account.id) ?? { cents: 0, e: 0, count: 0 };
    perCard.set(card.account.id, stats);
    stats.cents += cents;
    stats.count += 1;

    if (!card.product) {
      unidentifiedCents += cents;
      continue;
    }
    stats.e += usedSum;
    cat.e += usedSum;
    cat.knownCents += cents;
    cat.byAccount.set(card.account.id, (cat.byAccount.get(card.account.id) ?? 0) + cents);
    totalCents += cents;
    totalE += usedSum;
    totalCount += 1;
    reviewed.push({ row, quarter: purchaseQuarter, card, category, cents, usedSum });
  }

  // The comparison is an estimate that starts from the purchases as they happened,
  // so it can only match or beat them. A purchase in the window moves only if another
  // card pays more on what is free: no cap, or room left after every real purchase.
  // Nothing gives up a bonus it really earned, so per-purchase gains add up to the totals.
  const bestCapUsed = new Map(capUsed);
  const worthOn = (p: typeof reviewed[number], c: ResolvedCard) =>
    purchaseEarning(c, p.category, p.cents, bestCapUsed.get(capKey(p.quarter, c)) ?? 0) * value(c);
  const bestCard = (p: typeof reviewed[number]) => cardsFor(p.quarter).filter((c) => c.product !== null)
    .sort(byWorth((c) => worthOn(p, c), (c) => earnRate(c, p.category)))[0];
  // Cap room goes first to the purchases gaining the most per dollar.
  const gainRate = new Map(reviewed.map((p) => [p, (worthOn(p, bestCard(p)) - p.usedSum) / p.cents || 0]));

  for (const p of [...reviewed].sort((a, b) => gainRate.get(b)! - gainRate.get(a)!)) {
    const { row, card, category, cents, usedSum } = p;
    const best = bestCard(p);
    const moved = worthOn(p, best) > usedSum;
    const bestSum = moved ? worthOn(p, best) : usedSum;
    if (moved) countTowardCap(bestCapUsed, p.quarter, best, category, cents);
    perCategory.get(category)!.bestE += bestSum;
    totalBestE += bestSum;

    const missedCents = rewardCents(bestSum) - rewardCents(usedSum);
    if (missedCents > 0) {
      misses.push({
        transactionId: row.id,
        date: row.date,
        name: row.name,
        merchantName: row.merchant_name,
        amount: centsToAmount(cents),
        category,
        accountId: card.account.id,
        bestAccountId: best.account.id,
        earned: centsToAmount(rewardCents(usedSum)),
        bestPossible: centsToAmount(rewardCents(bestSum)),
        missed: centsToAmount(missedCents),
        missedCents,
      });
    }
  }

  const categoryOrder = [...REWARD_CATEGORIES].sort((a, b) =>
    (perCategory.get(b)?.cents ?? 0) - (perCategory.get(a)?.cents ?? 0)
    || REWARD_CATEGORIES.indexOf(a) - REWARD_CATEGORIES.indexOf(b));

  const categories = categoryOrder.map((category) => {
    const order = ranked(category);
    const top = order.length ? effective(order[0], category) : 0;
    const best = order.filter((c) => effective(c, category) === top);
    const next = order.find((c) => effective(c, category) < top) ?? null;
    let portal: RewardsOverviewDto['categories'][number]['portal'] = null;
    if (category === 'travel') {
      const portals = known
        .filter((c) => c.product!.portal)
        .map((c) => ({ card: c, e: c.product!.portal!.rate * pointValue(c, values, holdsPremium) }))
        .filter((p) => p.e > top)
        .sort((a, b) => b.e - a.e || (a.card.product!.currency === 'cash' ? -1 : 1));
      if (portals.length) {
        const { card, e } = portals[0];
        portal = {
          accountId: card.account.id,
          label: card.product!.portal!.label,
          rate: formatRate(card.product!.portal!.rate),
          unit: unitOf(card.product!),
          effectiveRate: effectiveRatio(e),
        };
      }
    }
    let capWarning: RewardsOverviewDto['categories'][number]['capWarning'] = null;
    const leader = top > 0 ? order[0] : null;
    if (leader?.product!.cap && cappedCategories(leader).includes(category)) {
      // Scale the window's pace to a quarter; every category sharing the cap counts.
      const windowCents = cappedCategories(leader).reduce((sum, c) => sum + (perCategory.get(c)?.cents ?? 0), 0);
      const quarterlyCents = Math.round(windowCents * 91 / RANGE_DAYS[range]);
      if (quarterlyCents > leader.product!.cap.cents) {
        capWarning = {
          accountId: leader.account.id,
          cap: centsToAmount(leader.product!.cap.cents),
          quarterlySpend: centsToAmount(quarterlyCents),
        };
      }
    }
    const stats = perCategory.get(category);
    return {
      category,
      spend: centsToAmount(stats?.cents ?? 0),
      transactionCount: stats?.count ?? 0,
      best: top > 0 ? best.map((c) => rateDto(c, category)) : [],
      runnerUp: top > 0 && next && effective(next, category) > 0 ? rateDto(next, category) : null,
      portal,
      capWarning,
    };
  });

  const byCategory = categoryOrder
    .filter((category) => (perCategory.get(category)?.knownCents ?? 0) > 0)
    .map((category) => {
      const stats = perCategory.get(category)!;
      const mostUsed = [...stats.byAccount.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? null;
      const best = known.length ? ranked(category)[0].account.id : null;
      const earned = rewardCents(stats.e);
      const bestPossible = rewardCents(stats.bestE);
      return {
        category,
        spend: centsToAmount(stats.knownCents),
        earned: centsToAmount(earned),
        bestPossible: centsToAmount(bestPossible),
        missed: centsToAmount(bestPossible - earned),
        mostUsedAccountId: mostUsed,
        bestAccountId: best,
      };
    });

  misses.sort((a, b) => b.missedCents - a.missedCents || b.date.localeCompare(a.date));

  const earnedTotal = rewardCents(totalE);
  const bestTotal = rewardCents(totalBestE);

  const usedCurrencies = new Set(known.map((c) => c.product!.currency));
  const pointValues = Object.values(CURRENCIES)
    .filter((c) => c.adjustable && usedCurrencies.has(c.id))
    .map((c) => ({
      currency: c.id,
      name: c.name,
      centsPerPoint: (((values[c.id] ?? c.defaultValue)) / 100).toFixed(2),
      defaultCentsPerPoint: (c.defaultValue / 100).toFixed(2),
      minimum: (POINT_VALUE_MIN / 100).toFixed(2),
      maximum: (POINT_VALUE_MAX / 100).toFixed(2),
    }));

  return {
    asOf: today,
    quarter,
    window: { range, from, to: today },
    pointValues,
    products: PRODUCTS.map((p) => ({
      id: p.id,
      name: p.name,
      issuer: p.issuer,
      unit: unitOf(p),
      hasChoice: Boolean(p.choice),
      hasRotating: Boolean(p.rotating),
    })),
    choiceOptions: [...CHOICE_OPTIONS],
    cards: cards.map((card) => {
      const p = card.product;
      const stats = perCard.get(card.account.id);
      const bonuses = p
        ? REWARD_CATEGORIES
          .filter((category) => category !== 'other')
          .map((category) => ({ category, tenths: rateNow(card, category) }))
          .filter((b) => b.tenths > p.base)
          .sort((a, b) => b.tenths - a.tenths)
          .map((b) => ({ category: b.category, rate: formatRate(b.tenths) }))
        : [];
      const notes = p ? [...p.notes] : [];
      if (p?.portal) notes.push({ kind: 'portal', label: p.portal.label, rate: formatRate(p.portal.rate) });
      const posted = card.rotatingAnnouncement;
      const next = p?.rotating ? upcomingQuarter(card, quarter, announcements) : null;
      const used = capUsed.get(capKey(quarter, card)) ?? 0;
      return {
        accountId: card.account.id,
        displayName: displayNameOf(card.account, p),
        institutionName: card.account.institution_name,
        mask: card.account.mask,
        productId: p?.id ?? null,
        productName: p?.name ?? null,
        productSource: card.productSource,
        productDetectedBy: card.detectedBy,
        productCandidates: p ? [] : productCandidates(card.account).map((c) => c.id),
        choiceCategory: card.choice,
        rotatingCategories: card.rotating,
        rotatingSource: card.rotatingSource,
        rotatingStatus: card.rotatingStatus,
        rotatingRates: p?.rotating
          ? card.rotating.map((category) => ({ category, rate: formatRate(rotatingRate(card, category)) }))
          : [],
        rotatingActivateBy: card.rotatingActivateBy,
        rotatingActivated: card.activatedQuarters.includes(quarter),
        rotatingUntracked: card.rotatingSource === 'calendar' ? card.rotatingUntracked : [],
        rotatingAnnouncement: posted?.url && (posted.state === 'read' || posted.state === 'unreadable')
          ? { url: posted.url, state: posted.state }
          : null,
        rotatingNext: next,
        activation: activationFor(card, quarter, today, next),
        capUsage: p?.cap && cappedCategories(card).length > 0
          ? {
            cap: centsToAmount(p.cap.cents),
            used: centsToAmount(Math.min(used, p.cap.cents)),
            remaining: centsToAmount(Math.max(p.cap.cents - used, 0)),
          }
          : null,
        unit: p ? unitOf(p) : null,
        baseRate: p ? formatRate(p.base) : null,
        bonuses,
        notes,
        spend: centsToAmount(stats?.cents ?? 0),
        earned: p ? centsToAmount(rewardCents(stats?.e ?? 0)) : null,
        transactionCount: stats?.count ?? 0,
      };
    }),
    categories,
    review: {
      spend: centsToAmount(totalCents),
      earned: centsToAmount(earnedTotal),
      bestPossible: centsToAmount(bestTotal),
      missed: centsToAmount(bestTotal - earnedTotal),
      transactionCount: totalCount,
      unidentifiedSpend: centsToAmount(unidentifiedCents),
      byCategory,
      topMisses: misses.slice(0, 8).map(({ missedCents: _, ...miss }) => miss),
    },
  };
}

type UpcomingQuarter = RewardsOverviewDto['cards'][number]['rotatingNext'];

function upcomingQuarter(card: ResolvedCard, quarter: string, announcements: AnnouncementStore): UpcomingQuarter {
  const product = card.product!;
  const next = nextQuarter(quarter);
  const entry = calendarEntry(product, next, announcements);
  if (!entry) return null;
  const state = announcements[product.id]?.[next]?.state;
  return {
    quarter: next,
    categories: entry.categories,
    untracked: entry.untracked,
    activateBy: entry.activateBy,
    open: state === 'read' || state === 'unreadable',
    activated: card.activatedQuarters.includes(next),
  };
}

/**
 * This quarter until its deadline (unless the user cleared it), then next
 * quarter once it opens; a quarter the user marked activated drops out.
 */
function activationFor(card: ResolvedCard, quarter: string, today: string, next: UpcomingQuarter): RewardsOverviewDto['cards'][number]['activation'] {
  const rotating = card.product?.rotating;
  if (!rotating) return null;
  const skipped = card.rotatingSource === 'chosen' && card.rotating.length === 0;
  const until = card.rotatingActivateBy ?? usualActivateBy(quarter);
  if (!skipped && !card.activatedQuarters.includes(quarter) && today <= until) {
    return { url: rotating.activationUrl, quarter, activateBy: until };
  }
  if (next?.open && !next.activated && today <= next.activateBy) {
    return { url: rotating.activationUrl, quarter: next.quarter, activateBy: next.activateBy };
  }
  return null;
}

// ── Issuer announcements ─────────────────────────────────────────────────────

/**
 * Reads the issuer's rotating-category announcement for this quarter and the
 * next, for each rotating card the user holds. Runs with every sync; a quarter
 * already read is never fetched again, and a missing or unreadable one at most
 * every 12 hours. Past quarters are left alone: whether the user activated
 * them is unknown, so they keep only what was on file at the time.
 */
export async function refreshRotatingCalendars(opts: { today?: string; now?: number; fetch?: typeof fetch } = {}): Promise<void> {
  const today = opts.today ?? localToday();
  const now = opts.now ?? Date.now();
  const quarter = quarterOf(today);
  const { cardsFor, announcements } = await cardResolver();
  const products = new Set(cardsFor(quarter)
    .map((c) => c.product)
    .filter((p): p is ProductDef => Boolean(p?.rotating?.announcement)));
  if (products.size === 0) return;

  let changed = false;
  for (const product of products) {
    const byQuarter = announcements[product.id] ??= {};
    for (const q of [quarter, nextQuarter(quarter)]) {
      const last = byQuarter[q];
      if (last?.state === 'read') continue;
      if (last && now - Date.parse(last.checkedAt) < ANNOUNCEMENT_RECHECK_MS) continue;
      const checkedAt = new Date(now).toISOString();
      try {
        const result = await product.rotating!.announcement!(q, opts.fetch);
        byQuarter[q] = result.state === 'read'
          ? { state: 'read', checkedAt, url: result.url, ...result.announcement }
          : result.state === 'unreadable'
            ? { state: 'unreadable', checkedAt, url: result.url }
            : { state: 'notPosted', checkedAt };
      } catch {
        byQuarter[q] = { state: 'failed', checkedAt };
      }
      changed = true;
    }
    // Ended quarters keep only what was read; misses there are no longer worth a look.
    for (const [q, record] of Object.entries(byQuarter)) {
      if (q < quarter && record.state !== 'read') {
        delete byQuarter[q];
        changed = true;
      }
    }
  }
  if (changed) await setSetting(PREF_ANNOUNCEMENTS, JSON.stringify(announcements));
}

// ── Writes ───────────────────────────────────────────────────────────────────

export async function putRewardCard(accountId: string, input: {
  productId?: string | null;
  choiceCategory?: string | null;
  rotatingCategories?: string[] | null;
  activation?: { quarter?: string; activated?: boolean } | null;
}, opts: { today?: string } = {}): Promise<{ updated: true }> {
  const allowed = new Set(['productId', 'choiceCategory', 'rotatingCategories', 'activation']);
  for (const key of Object.keys(input ?? {})) {
    if (!allowed.has(key)) throw validation(`Unknown field: ${key}`, { field: key });
  }
  const account = (await loadAccounts()).find((a) => a.id === accountId);
  if (!account) throw notFound('Account');
  if (account.type !== 'credit') throw validation('Only credit card accounts earn card rewards.', { field: 'accountId' });

  const prefs = await readJson<Record<string, CardPreference>>(PREF_CARDS);
  const pref: CardPreference = { ...(prefs[accountId] ?? {}) };

  if ('productId' in input) {
    if (input.productId === null || input.productId === undefined || input.productId === '') {
      delete pref.productId;
    } else {
      if (!PRODUCT_BY_ID.has(input.productId)) throw validation('Unknown card product.', { field: 'productId' });
      if (pref.productId !== input.productId) {
        delete pref.choiceCategory;
        delete pref.rotating;
        delete pref.activated;
      }
      pref.productId = input.productId;
    }
  }
  const product = identifyProduct(account, pref, (await loadAnnualFees()).get(accountId))?.product ?? null;

  if ('choiceCategory' in input) {
    if (input.choiceCategory === null || input.choiceCategory === undefined || input.choiceCategory === '') {
      delete pref.choiceCategory;
    } else {
      if (!product?.choice) throw validation('This card has no choice category.', { field: 'choiceCategory' });
      if (!CHOICE_OPTIONS.includes(input.choiceCategory as ChoiceOption)) {
        throw validation(`choiceCategory must be ${CHOICE_OPTIONS.join('|')}.`, { field: 'choiceCategory' });
      }
      pref.choiceCategory = input.choiceCategory as ChoiceOption;
    }
  }

  if ('rotatingCategories' in input) {
    const list = input.rotatingCategories ?? [];
    if (!Array.isArray(list)) throw validation('rotatingCategories must be an array.', { field: 'rotatingCategories' });
    const valid = new Set<string>(REWARD_CATEGORIES.filter((c) => c !== 'other'));
    for (const c of list) {
      if (!valid.has(c)) throw validation(`Unknown reward category: ${c}`, { field: 'rotatingCategories' });
    }
    if (!product?.rotating) {
      if (list.length) throw validation('This card has no rotating categories.', { field: 'rotatingCategories' });
      delete pref.rotating;
    } else {
      // An empty list is kept: it records a quarter the user did not activate.
      pref.rotating = {
        quarter: quarterOf(opts.today ?? localToday()),
        categories: REWARD_CATEGORIES.filter((c) => list.includes(c)),
      };
    }
  }

  if ('activation' in input) {
    const { quarter, activated } = input.activation ?? {};
    if (!product?.rotating) throw validation('This card has no rotating categories.', { field: 'activation' });
    if (typeof activated !== 'boolean') throw validation('activation.activated must be true or false.', { field: 'activation' });
    // Activation opens about two weeks before a quarter starts, so the next one counts too.
    const current = quarterOf(opts.today ?? localToday());
    if (quarter !== current && quarter !== nextQuarter(current)) {
      throw validation(`activation.quarter must be ${current} or ${nextQuarter(current)}.`, { field: 'activation' });
    }
    // The review still needs ended quarters, as far back as its longest window.
    const oldest = quarterOf(addDays(opts.today ?? localToday(), -RANGE_DAYS['1y']));
    const kept = (pref.activated ?? []).filter((q) => q >= oldest && q !== quarter);
    pref.activated = activated ? [...kept, quarter].sort() : kept;
    if (pref.activated.length === 0) delete pref.activated;
  }

  if (Object.keys(pref).length === 0) delete prefs[accountId];
  else prefs[accountId] = pref;
  await setSetting(PREF_CARDS, JSON.stringify(prefs));
  return { updated: true };
}

export async function putRewardPreferences(input: {
  pointValues?: Record<string, string | null>;
}): Promise<{ updated: true }> {
  for (const key of Object.keys(input ?? {})) {
    if (key !== 'pointValues') throw validation(`Unknown field: ${key}`, { field: key });
  }
  if (!input.pointValues || typeof input.pointValues !== 'object') return { updated: true };
  const values = await readJson<Record<string, number>>(PREF_POINT_VALUES);
  for (const [currency, raw] of Object.entries(input.pointValues)) {
    const def = CURRENCIES[currency as CurrencyId];
    if (!def || !def.adjustable) throw validation(`Points currency ${currency} has a fixed value.`, { field: 'pointValues' });
    if (raw === null || raw === '') {
      delete values[currency];
      continue;
    }
    if (typeof raw !== 'string' || !/^\d{1,2}(\.\d{1,2})?$/.test(raw.trim())) {
      throw validation('Point values are cents per point, e.g. "1.25".', { field: 'pointValues' });
    }
    const hundredths = Math.round(Number(raw.trim()) * 100);
    if (hundredths < POINT_VALUE_MIN || hundredths > POINT_VALUE_MAX) {
      throw validation(`Point values must be between ${(POINT_VALUE_MIN / 100).toFixed(2)} and ${(POINT_VALUE_MAX / 100).toFixed(2)} cents.`, { field: 'pointValues' });
    }
    values[currency] = hundredths;
  }
  await setSetting(PREF_POINT_VALUES, JSON.stringify(values));
  return { updated: true };
}
