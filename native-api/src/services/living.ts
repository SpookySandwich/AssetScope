// Cost-of-living analysis. Every monetary total is integer cents; the page
// only renders this DTO. Transfer rows are ignored. Loan payments are cash
// leaving the account and count as outflow. Recurring stable charges are
// treated as fixed; everything else that can change this month is flexible.

import { randomUUID } from 'node:crypto';
import { db } from '../core/database.js';
import { getSetting, setSetting } from '../core/settings.js';
import { toCents, centsToAmount, parseAmountToCents, centsToDbValue } from './money.js';
import { notFound, validation } from '../errors.js';
// Shared with transactions.ts so pace rows and the Transactions page agree on
// which category a legacy-spelled row belongs to.
import { normalizeCategory } from './categories.js';
import { accountDisplayNames, accountSummaries } from './networth.js';

const CATEGORY_EXPR = "COALESCE(t.manual_category, t.category, t.raw_category, 'OTHER')";
const TRANSFER_CATEGORIES = new Set(['TRANSFER_IN', 'TRANSFER_OUT', 'TRANSFER']);
const LIVING_KINDS = new Set([
  'dining', 'grocery', 'rideshare', 'income', 'rent', 'utilities',
  'phone', 'subscription', 'tuition', 'transfer', 'other',
]);
const TRIP_KINDS = new Set(['campus', 'airport', 'late_night', 'commute', 'one_off', 'other']);
const OBLIGATION_KINDS = new Set(['rent', 'utilities', 'phone', 'subscription', 'tuition', 'other']);
const OBLIGATION_INTERVALS = new Set(['weekly', 'monthly', 'yearly']);
const GOAL_KINDS = new Set(['none', 'contribution', 'target']);
const PREF_DINING = 'living_dining_meals_per_week';
const PREF_GOAL_KIND = 'living_goal_kind';
const PREF_GOAL_LABEL = 'living_goal_label';
const PREF_GOAL_MONTHLY = 'living_goal_monthly_contribution';
const PREF_GOAL_TARGET = 'living_goal_target_amount';

export type LivingKind =
  | 'dining' | 'grocery' | 'rideshare' | 'income' | 'rent' | 'utilities'
  | 'phone' | 'subscription' | 'tuition' | 'transfer' | 'other';

export type TripKind = 'campus' | 'airport' | 'late_night' | 'commute' | 'one_off' | 'other';
export type ObligationKind = 'rent' | 'utilities' | 'phone' | 'subscription' | 'tuition' | 'other';
export type ObligationInterval = 'weekly' | 'monthly' | 'yearly';
export type GoalKind = 'none' | 'contribution' | 'target';
export type TradeoffKind =
  | 'cookMore' | 'repeatedRide' | 'categoryDrift' | 'fixedShare';

type LivingRow = {
  id: string;
  account_id: string;
  account_name: string;
  date: string;
  name: string;
  merchant_name: string | null;
  amount: number;
  category: string;
  party_size: number | null;
  trip_origin: string | null;
  trip_destination: string | null;
  trip_kind: string | null;
  living_kind: string | null;
  raw_category: string | null;
};

type MerchantDefault = {
  partySize: number | null;
  tripOrigin: string | null;
  tripDestination: string | null;
  tripKind: string | null;
  livingKind: LivingKind | null;
  // Merchant-level expected monthly amount, set manually. Wins over both the
  // recurring median and a single last charge (e.g. rent that sometimes
  // includes a prepaid buffer).
  monthlyCents: number | null;
};

export type LivingTransactionDto = {
  id: string;
  accountId: string;
  accountName: string;
  date: string;
  name: string;
  merchantName: string | null;
  amount: string;
  category: string;
  livingKind: LivingKind;
  partySize: number | null;
  effectivePartySize: number;
  tripOrigin: string | null;
  tripDestination: string | null;
  tripKind: string | null;
  needsAnnotation: boolean;
};

export type LivingTradeoffDto = {
  id: string;
  kind: TradeoffKind;
  monthlyImpact: string;
  yearlyImpact: string;
  category: string | null;
  extraCookedMealsPerWeek: number | null;
  diningCostPerMeal: string | null;
  cookingCostPerMeal: string | null;
  origin: string | null;
  destination: string | null;
  rideCount: number | null;
  rideMonthlyCost: string | null;
  alternativeLabel: string | null;
  alternativeMonthlyCost: string | null;
  baseline: string | null;
  current: string | null;
  fixedShareOfIncome: string | null;
  goalLabel: string | null;
  goalMonths: number | null;
  goalYearly: string | null;
};

export type LivingOverviewDto = {
  period: {
    month: string;
    from: string;
    to: string;
    today: string;
    daysElapsed: number;
    daysInMonth: number;
    daysRemaining: number;
    reviewFrom: string;
    reviewTo: string;
    usingPriorMonth: boolean;
  };
  hasHistory: boolean;
  detected: Array<{
    key: string;
    name: string;
    kind: LivingKind;
    monthlyAmount: string | null;
    lastDate: string;
    postedThisMonth: boolean;
    confidence: 'series' | 'single';
    overridden: boolean;
  }>;
  income: { posted: string; expectedRemaining: string; projected: string };
  split: {
    fixedPosted: string;
    fixedRemaining: string;
    fixedProjected: string;
    flexiblePosted: string;
    flexibleProjected: string;
    fixedShareOfIncome: string | null;
    flexibleShareOfIncome: string | null;
  };
  leftover: { posted: string; projected: string };
  meals: {
    diningTransactions: number;
    diningMeals: number;
    diningSpend: string;
    diningCostPerMeal: string | null;
    grocerySpend: string;
    cookedMeals: number;
    cookingCostPerMeal: string | null;
    currentDiningMealsPerWeek: string;
  };
  diningOutcomes: Array<{
    mealsPerWeek: number;
    monthlyDining: string;
    monthlyCooking: string | null;
    monthlySavingsVsCurrent: string;
    leftover: string;
  }>;
  selectedDiningMealsPerWeek: number;
  tradeoffs: LivingTradeoffDto[];
  routes: Array<{
    origin: string;
    destination: string;
    rideCount: number;
    monthlyCost: string;
    tripKind: string | null;
    alternativeLabel: string | null;
    alternativeMonthlyCost: string | null;
  }>;
  categoryTrends: Array<{
    category: string;
    flexibility: 'fixed' | 'flexible';
    currentMonth: string;
    baseline: string | null;
    drifted: boolean;
    points: { month: string; amount: string }[];
  }>;
  obligations: Array<{
    id: string;
    name: string;
    amount: string;
    interval: ObligationInterval;
    kind: ObligationKind;
    monthlyAmount: string;
  }>;
  preferences: {
    diningMealsPerWeek: number | null;
    goalKind: GoalKind;
    goalLabel: string | null;
    goalMonthlyContribution: string | null;
    goalTargetAmount: string | null;
  };
  /** Averages over the last full months with spending: the page's yardstick. */
  typical: {
    months: string[];
    income: string;
    spend: string;
    fixed: string;
    flexible: string;
    leftover: string;
    categories: Array<{
      category: string;
      flexibility: 'fixed' | 'flexible';
      monthlyAverage: string;
      share: string | null;
      /** Largest merchants in the category, so "Services" can say "HUB, Anthropic". */
      topMerchants: string[];
    }>;
    diningMealsPerWeek: string | null;
    diningCostPerMeal: string | null;
    goal: { label: string; months: number | null; yearly: string } | null;
  };
  /** The current calendar month measured against that yardstick. */
  thisMonth: {
    spent: string;
    expectedFixedRemaining: string;
    flexibleExpectedByNow: string;
    flexiblePace: string | null;
    projectedLeftoverIfTypical: string;
  };
  /** Bank and cash balances expressed in typical months of spending. */
  runway: { liquidCash: string; months: string | null };
  transactions: LivingTransactionDto[];
};

const GROCERY_RE = /\b(whole foods|trader joe|kroger|publix|aldi|costco|walmart|safeway|food lion|harris teeter|sprouts|instacart|fresh market|supermarket|grocery|wegmans|meijer|winco|lidl|h-?e-?b|h mart|shoprite|shop rite|stop & shop|giant|target)\b/i;
const DINING_RE = /\b(starbucks|coffee|cafe|café|restaurant|chipotle|mcdonald|burger|pizza|sushi|taqueria|diner|bistro|grill|bbq|kitchen|wendy|taco bell|panera|dunkin|doordash|door dash|uber eats|ubereats|grubhub|postmates|sweetgreen|shake shack|chick-fil-a|chickfila|popeyes|kfc|ihop|applebee|olive garden)\b/i;
const RIDESHARE_RE = /\b(uber|lyft|taxi|via ride)\b/i;
const UBER_EATS_RE = /\b(uber\s*eats|ubereats)\b/i;
const RENT_RE = /\b(rent|apartment|lease|property management|greystar|avalon|udr|coliving|co-living|student housing|campus living|residences|realty)\b/i;
const UTIL_RE = /\b(electric|electricity|power company|pg&e|coned|con ed|water bill|sewer|xfinity|comcast|spectrum|internet|utility|utilities|gas company|bills & utilities)\b/i;
const PHONE_RE = /\b(verizon|t-mobile|tmobile|at&t|att wireless|mint mobile|google fi|\bfi\b|visible wireless)\b/i;
const SUB_RE = /\b(netflix|spotify|hulu|disney\+|disney plus|apple\.com\/bill|youtube|amazon prime|patreon|icloud|microsoft 365|adobe|github|new york times|nytimes|wsj|claude|openai|anthropic|subscription)\b/i;
const TUITION_RE = /\b(tuition|university|college|payment plan|nelnet)\b/i;
const INCOME_RE = /\b(payroll|paycheck|adp |gusto|stipend|direct dep|salary|edi pymnts)\b/i;
const PAYMENT_RE = /\b(automatic payment|autopay|payment - thank|credit crd|online scheduled payment|online\/mobile recurring|^payment$)\b/i;

export function merchantKey(merchantName: string | null, name: string): string {
  return (merchantName?.trim() || name)
    // Bank ACH reference ids (PPD ID: …, WEB ID: …) vary per payment; keeping
    // them in the key splits one recurring merchant into single-use variants.
    .replace(/\b(ppd|ccd|ctx|web|tel)\s+id:?\s*[\w-]+/gi, '')
    .trim().toLowerCase().replace(/\s+/g, ' ');
}

function hay(merchantName: string | null, name: string, category: string): string {
  return `${merchantName ?? ''} ${name} ${category}`.toLowerCase();
}

// Generic wording only. A particular landlord or employer is the user's own
// label, stored per merchant in living_merchant_defaults, and wins over this.
export function classifyLivingKind(
  category: string,
  name: string,
  merchantName: string | null,
  amount: number,
  rawCategory: string | null = null,
): LivingKind {
  const cat = `${category ?? ''} ${rawCategory ?? ''}`.toUpperCase();
  const text = hay(merchantName, name, `${category} ${rawCategory ?? ''}`);
  if (TRANSFER_CATEGORIES.has((category ?? 'OTHER').toUpperCase()) || cat.includes('TRANSFER')) return 'transfer';
  if (PAYMENT_RE.test(text) || cat.includes('LOAN')) return 'transfer';
  if (cat.includes('INCOME') || INCOME_RE.test(text)) {
    return amount <= 0 ? 'income' : 'transfer';
  }
  if (UBER_EATS_RE.test(text)) return 'dining';
  if ((RIDESHARE_RE.test(text) && !UBER_EATS_RE.test(text)) || cat.includes('TAXI') || cat.includes('RIDE_SHARE')) {
    return 'rideshare';
  }
  if (cat.includes('GROCER') || GROCERY_RE.test(text)) return 'grocery';
  if (cat.includes('RESTAURANT') || cat.includes('COFFEE') || cat.includes('FOOD_AND_DRINK') || DINING_RE.test(text)) {
    return 'dining';
  }
  if (PHONE_RE.test(text)) return 'phone';
  if (RENT_RE.test(text) || cat.includes('RENT_AND_UTILITIES_RENT')) return 'rent';
  if (cat.includes('UTILIT') || UTIL_RE.test(text)) return 'utilities';
  if (cat.includes('TUITION') || TUITION_RE.test(text)) return 'tuition';
  if (SUB_RE.test(text)) return 'subscription';
  return 'other';
}

const FIXED_KINDS = new Set<LivingKind>(['rent', 'utilities', 'phone', 'subscription', 'tuition']);

export function utcToday(): string {
  const d = new Date();
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

function parseYmd(ymd: string): Date {
  return new Date(`${ymd}T00:00:00Z`);
}

function ymd(d: Date): string {
  return d.toISOString().slice(0, 10);
}

export function monthStart(ymdValue: string): string {
  return `${ymdValue.slice(0, 7)}-01`;
}

export function monthEnd(ymdValue: string): string {
  const d = parseYmd(ymdValue);
  return ymd(new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 0)));
}

export function daysInMonth(ymdValue: string): number {
  const d = parseYmd(ymdValue);
  return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 0)).getUTCDate();
}

function addMonths(ymdValue: string, delta: number): string {
  const d = parseYmd(ymdValue);
  d.setUTCMonth(d.getUTCMonth() + delta);
  return ymd(d);
}

function daysBetween(from: string, to: string): number {
  return Math.round((parseYmd(to).getTime() - parseYmd(from).getTime()) / 86_400_000);
}

function median(values: number[]): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1 ? sorted[mid]! : Math.round((sorted[mid - 1]! + sorted[mid]!) / 2);
}

function stdev(values: number[]): number {
  if (values.length < 2) return 0;
  const mean = values.reduce((s, v) => s + v, 0) / values.length;
  const variance = values.reduce((s, v) => s + (v - mean) ** 2, 0) / (values.length - 1);
  return Math.sqrt(variance);
}

function ratio(numer: number, denom: number): string | null {
  if (denom === 0) return null;
  return (numer / Math.abs(denom)).toFixed(4);
}

function moneyOrNull(cents: number | null): string | null {
  return cents === null ? null : centsToAmount(cents);
}

function clampInt(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, Math.trunc(value)));
}

function monthlyFromInterval(amountCents: number, interval: ObligationInterval): number {
  if (interval === 'weekly') return Math.round(amountCents * 52 / 12);
  if (interval === 'yearly') return Math.round(amountCents / 12);
  return amountCents;
}

type Enriched = LivingRow & {
  cents: number;
  kind: LivingKind;
  key: string;
  effectivePartySize: number;
  tripOrigin: string | null;
  tripDestination: string | null;
  tripKind: string | null;
  month: string;
};

function asLivingKind(value: string | null | undefined): LivingKind | null {
  return value && LIVING_KINDS.has(value) ? value as LivingKind : null;
}

function enrich(
  row: LivingRow,
  defaults: Map<string, MerchantDefault>,
): Enriched {
  const key = merchantKey(row.merchant_name, row.name);
  const def = defaults.get(key);
  const kind = asLivingKind(row.living_kind)
    ?? def?.livingKind
    ?? classifyLivingKind(row.category, row.name, row.merchant_name, row.amount, row.raw_category);
  const party = row.party_size ?? def?.partySize ?? null;
  const origin = row.trip_origin ?? def?.tripOrigin ?? null;
  const destination = row.trip_destination ?? def?.tripDestination ?? null;
  const tripKind = row.trip_kind ?? def?.tripKind ?? null;
  return {
    ...row,
    category: normalizeCategory(row.category),
    cents: toCents(row.amount),
    kind,
    key,
    effectivePartySize: party && party > 0 ? party : 1,
    tripOrigin: origin,
    tripDestination: destination,
    tripKind,
    month: row.date.slice(0, 7),
  };
}

type Recurring = {
  key: string;
  kind: LivingKind;
  interval: 'weekly' | 'monthly';
  typicalCents: number;
  expectedMonthlyCents: number;
};

export function detectRecurring(rows: Enriched[], today: string): Recurring[] {
  const lookback = addMonths(monthStart(today), -4);
  const groups = new Map<string, Enriched[]>();
  for (const row of rows) {
    // Income carries negative amounts; it still forms recurring series.
    if (row.date < lookback || row.kind === 'transfer') continue;
    if (row.cents <= 0 && row.kind !== 'income') continue;
    const list = groups.get(row.key) ?? [];
    list.push(row);
    groups.set(row.key, list);
  }
  const found: Recurring[] = [];
  for (const [key, list] of groups) {
    const uniqueDays = [...new Set(list.map((r) => r.date))].sort();
    if (uniqueDays.length < 2) continue;
    const intervals: number[] = [];
    for (let i = 1; i < uniqueDays.length; i++) {
      intervals.push(daysBetween(uniqueDays[i - 1]!, uniqueDays[i]!));
    }
    const med = median(intervals);
    const interval: 'weekly' | 'monthly' | null =
      med >= 6 && med <= 9 ? 'weekly' : med >= 25 && med <= 36 ? 'monthly' : null;
    if (!interval) continue;
    // Income rows are negative (money in); compare magnitudes either way.
    const amounts = list.map((r) => Math.abs(r.cents));
    const mean = amounts.reduce((s, v) => s + v, 0) / amounts.length;
    if (mean <= 0) continue;
    const cv = stdev(amounts) / mean;
    if (cv > 0.2) continue;
    const typicalCents = median(amounts);
    const kind = list[list.length - 1]!.kind;
    const expectedMonthlyCents = interval === 'weekly'
      ? Math.round(typicalCents * 52 / 12)
      : typicalCents;
    found.push({ key, kind, interval, typicalCents, expectedMonthlyCents });
  }
  return found;
}

async function loadRows(from: string, to: string): Promise<LivingRow[]> {
  const res = await db.execute({
    sql: `SELECT t.id, t.account_id, a.name AS account_name, t.date, t.name, t.merchant_name,
                 t.amount, ${CATEGORY_EXPR} AS category, t.raw_category, t.party_size, t.trip_origin,
                 t.trip_destination, t.trip_kind, t.living_kind
            FROM transactions t JOIN accounts a ON a.id = t.account_id
           WHERE t.date >= ? AND t.date <= ? AND t.pending = 0 AND t.ignored = 0
             AND a.archived_at IS NULL AND a.excluded = 0
           ORDER BY t.date DESC, t.id DESC`,
    args: [from, to],
  });
  return res.rows as unknown as LivingRow[];
}

async function loadDefaults(): Promise<Map<string, MerchantDefault>> {
  const res = await db.execute(
    'SELECT merchant_key, party_size, trip_origin, trip_destination, trip_kind, living_kind, monthly_amount FROM living_merchant_defaults',
  );
  const map = new Map<string, MerchantDefault>();
  for (const row of res.rows as unknown as {
    merchant_key: string; party_size: number | null; trip_origin: string | null;
    trip_destination: string | null; trip_kind: string | null; living_kind: string | null;
    monthly_amount: number | null;
  }[]) {
    map.set(row.merchant_key, {
      partySize: row.party_size,
      tripOrigin: row.trip_origin,
      tripDestination: row.trip_destination,
      tripKind: row.trip_kind,
      livingKind: asLivingKind(row.living_kind),
      monthlyCents: row.monthly_amount === null ? null : toCents(row.monthly_amount),
    });
  }
  return map;
}

async function loadObligations() {
  const res = await db.execute(
    'SELECT id, name, amount, interval, kind FROM living_obligations ORDER BY name COLLATE NOCASE',
  );
  return (res.rows as unknown as {
    id: string; name: string; amount: number; interval: string; kind: string;
  }[]).map((row) => ({
    id: row.id,
    name: row.name,
    amountCents: toCents(row.amount),
    interval: row.interval as ObligationInterval,
    kind: row.kind as ObligationKind,
  }));
}

async function loadRouteAlternatives() {
  const res = await db.execute(
    'SELECT origin, destination, label, monthly_amount FROM living_route_alternatives',
  );
  const map = new Map<string, { label: string; monthlyCents: number }>();
  for (const row of res.rows as unknown as {
    origin: string; destination: string; label: string; monthly_amount: number;
  }[]) {
    map.set(routeKey(row.origin, row.destination), {
      label: row.label,
      monthlyCents: toCents(row.monthly_amount),
    });
  }
  return map;
}

export function routeKey(origin: string, destination: string): string {
  return `${origin.trim().toLowerCase()}|${destination.trim().toLowerCase()}`;
}

async function loadPreferences() {
  const [diningRaw, goalKindRaw, goalLabel, goalMonthly, goalTarget] = await Promise.all([
    getSetting(PREF_DINING),
    getSetting(PREF_GOAL_KIND),
    getSetting(PREF_GOAL_LABEL),
    getSetting(PREF_GOAL_MONTHLY),
    getSetting(PREF_GOAL_TARGET),
  ]);
  const dining = diningRaw !== null && /^\d+$/.test(diningRaw) ? Number(diningRaw) : null;
  const goalKind = goalKindRaw && GOAL_KINDS.has(goalKindRaw) ? goalKindRaw as GoalKind : 'none';
  return {
    diningMealsPerWeek: dining !== null ? clampInt(dining, 0, 14) : null,
    goalKind,
    goalLabel: goalLabel?.trim() || null,
    goalMonthlyContribution: goalMonthly && parseAmountToCents(goalMonthly) !== null ? goalMonthly : null,
    goalTargetAmount: goalTarget && parseAmountToCents(goalTarget) !== null ? goalTarget : null,
  };
}

function emptyTradeoff(partial: Partial<LivingTradeoffDto> & Pick<LivingTradeoffDto, 'id' | 'kind' | 'monthlyImpact'>): LivingTradeoffDto {
  const monthly = toCents(Number(partial.monthlyImpact));
  return {
    id: partial.id,
    kind: partial.kind,
    monthlyImpact: partial.monthlyImpact,
    yearlyImpact: centsToAmount(monthly * 12),
    category: partial.category ?? null,
    extraCookedMealsPerWeek: partial.extraCookedMealsPerWeek ?? null,
    diningCostPerMeal: partial.diningCostPerMeal ?? null,
    cookingCostPerMeal: partial.cookingCostPerMeal ?? null,
    origin: partial.origin ?? null,
    destination: partial.destination ?? null,
    rideCount: partial.rideCount ?? null,
    rideMonthlyCost: partial.rideMonthlyCost ?? null,
    alternativeLabel: partial.alternativeLabel ?? null,
    alternativeMonthlyCost: partial.alternativeMonthlyCost ?? null,
    baseline: partial.baseline ?? null,
    current: partial.current ?? null,
    fixedShareOfIncome: partial.fixedShareOfIncome ?? null,
    goalLabel: partial.goalLabel ?? null,
    goalMonths: partial.goalMonths ?? null,
    goalYearly: partial.goalYearly ?? null,
  };
}

function buildDetected(
  lastByKey: Map<string, Enriched>,
  thisMonth: Enriched[],
  recurring: Recurring[],
  defaults: Map<string, MerchantDefault>,
): LivingOverviewDto['detected'] {
  const items = new Map<string, LivingOverviewDto['detected'][number]>();
  for (const rec of recurring) {
    if (rec.kind === 'grocery' || rec.kind === 'dining' || rec.kind === 'rideshare') continue;
    const last = lastByKey.get(rec.key) ?? thisMonth.find((r) => r.key === rec.key);
    const override = defaults.get(rec.key)?.monthlyCents ?? null;
    items.set(rec.key, {
      key: rec.key,
      name: last?.merchant_name || last?.name || rec.key,
      kind: rec.kind,
      monthlyAmount: centsToAmount(override ?? rec.expectedMonthlyCents),
      lastDate: last?.date ?? '',
      postedThisMonth: thisMonth.some((r) => r.key === rec.key),
      confidence: 'series',
      overridden: override !== null,
    });
  }
  for (const [key, last] of lastByKey) {
    if (items.has(key)) continue;
    const override = defaults.get(key)?.monthlyCents ?? null;
    items.set(key, {
      key,
      name: last.merchant_name || last.name,
      kind: last.kind,
      // Single observation without an override: the amount is unknown (N/A),
      // not the face value of that one charge.
      monthlyAmount: override !== null ? centsToAmount(override) : null,
      lastDate: last.date,
      postedThisMonth: thisMonth.some((r) => r.key === key),
      confidence: 'single',
      overridden: override !== null,
    });
  }
  return [...items.values()].sort((a, b) => a.name.localeCompare(b.name));
}

export async function livingOverview(opts: {
  today?: string;
  diningMealsPerWeek?: number;
} = {}): Promise<LivingOverviewDto> {
  const today = opts.today ?? utcToday();
  const fromMonth = monthStart(today);
  const toMonth = monthEnd(today);
  const daysMonth = daysInMonth(today);
  const daysElapsed = Math.min(Number(today.slice(8, 10)), daysMonth);
  const daysRemaining = daysMonth - daysElapsed;
  const historyFrom = monthStart(addMonths(fromMonth, -5));

  const [rawRows, defaults, obligations, alternatives, preferences] = await Promise.all([
    loadRows(historyFrom, today),
    loadDefaults(),
    loadObligations(),
    loadRouteAlternatives(),
    loadPreferences(),
  ]);
  const rows = rawRows.map((row) => enrich(row, defaults));
  const recurring = detectRecurring(rows, today);
  const recurringByKey = new Map(recurring.map((r) => [r.key, r]));

  const thisMonth = rows.filter((r) => r.date >= fromMonth && r.date <= today);
  const spendThisMonth = thisMonth.filter((r) => r.kind !== 'transfer' && r.kind !== 'income');
  const priorMonthStart = monthStart(addMonths(fromMonth, -1));
  const priorMonthEnd = monthEnd(priorMonthStart);
  const usingPriorMonth = spendThisMonth.length === 0 && rows.some((r) => r.date < fromMonth);
  const reviewFrom = usingPriorMonth ? priorMonthStart : fromMonth;
  const reviewTo = usingPriorMonth ? priorMonthEnd : today;
  const reviewRows = rows.filter((r) => r.date >= reviewFrom && r.date <= reviewTo);
  const reviewDays = usingPriorMonth
    ? daysInMonth(priorMonthStart)
    : daysElapsed;

  const isFixedTxn = (row: Enriched): boolean => {
    if (row.kind === 'transfer' || row.kind === 'income') return false;
    if (FIXED_KINDS.has(row.kind)) return true;
    const rec = recurringByKey.get(row.key);
    return rec !== undefined && rec.kind !== 'grocery' && rec.kind !== 'dining' && rec.kind !== 'rideshare';
  };

  let incomePosted = 0;
  let fixedPosted = 0;
  let flexiblePosted = 0;
  const postedRecurring = new Set<string>();
  const postedFixedKinds = new Set<LivingKind>();
  const flexibleByCategory = new Map<string, number>();

  for (const row of thisMonth) {
    if (row.kind === 'transfer') continue;
    if (row.kind === 'income') {
      incomePosted += Math.abs(row.cents);
      continue;
    }
    if (isFixedTxn(row)) {
      fixedPosted += row.cents;
      postedRecurring.add(row.key);
      postedFixedKinds.add(row.kind);
    } else {
      flexiblePosted += row.cents;
      flexibleByCategory.set(row.category, (flexibleByCategory.get(row.category) ?? 0) + row.cents);
    }
  }

  let fixedRemaining = 0;
  let incomeRemaining = 0;
  const countedKeys = new Set<string>(postedRecurring);
  const countedObligationKind = new Set<string>();
  for (const rec of recurring) {
    const posted = thisMonth.filter((r) => r.key === rec.key && r.cents > 0)
      .reduce((s, r) => s + r.cents, 0);
    countedKeys.add(rec.key);
    const expected = defaults.get(rec.key)?.monthlyCents ?? rec.expectedMonthlyCents;
    if (rec.kind === 'income') {
      incomeRemaining += Math.max(0, expected - posted);
      continue;
    }
    if (rec.kind === 'grocery' || rec.kind === 'dining' || rec.kind === 'rideshare') continue;
    const remaining = Math.max(0, expected - posted);
    fixedRemaining += remaining;
    if (remaining > 0 || posted > 0) countedObligationKind.add(rec.kind);
  }
  // One-off housing/phone/stipend that hasn't built a 2-interval series yet:
  // without a confirmed override the amount is UNKNOWN — shown as N/A in the
  // UI and deliberately excluded from projections rather than guessed at.
  const lastByKey = new Map<string, Enriched>();
  for (const row of rows) {
    if (row.date >= fromMonth) continue;
    if (row.kind !== 'income' && !FIXED_KINDS.has(row.kind)) continue;
    const prev = lastByKey.get(row.key);
    if (!prev || row.date > prev.date) lastByKey.set(row.key, row);
  }
  for (const [key, last] of lastByKey) {
    if (countedKeys.has(key)) continue;
    if (daysBetween(last.date, today) > 60) continue;
    const posted = thisMonth.some((r) => r.key === key);
    if (posted) continue;
    const override = defaults.get(key)?.monthlyCents ?? null;
    if (override === null) continue;
    countedKeys.add(key);
    if (last.kind === 'income') incomeRemaining += override;
    else {
      fixedRemaining += override;
      countedObligationKind.add(last.kind);
    }
  }
  for (const ob of obligations) {
    const monthly = monthlyFromInterval(ob.amountCents, ob.interval);
    if (postedFixedKinds.has(ob.kind) || countedObligationKind.has(ob.kind)) continue;
    const nameKey = ob.name.trim().toLowerCase();
    const posted = thisMonth.some((r) =>
      r.kind === ob.kind || r.key.includes(nameKey) || r.name.toLowerCase().includes(nameKey));
    if (!posted) {
      fixedRemaining += monthly;
      countedObligationKind.add(ob.kind);
    }
  }

  const incomeProjected = incomePosted + incomeRemaining;
  const fixedProjected = fixedPosted + fixedRemaining;
  const flexibleProjected = daysElapsed <= 0
    ? flexiblePosted
    : Math.round(flexiblePosted / daysElapsed * daysMonth);
  const leftoverPosted = incomePosted - fixedPosted - flexiblePosted;
  const leftoverProjected = incomeProjected - fixedProjected - flexibleProjected;
  const shareDenom = incomeProjected > 0 ? incomeProjected : (fixedProjected + flexibleProjected);

  const dining = reviewRows.filter((r) => r.kind === 'dining' && r.cents > 0);
  const grocery = reviewRows.filter((r) => r.kind === 'grocery');
  const diningSpend = dining.reduce((s, r) => s + r.cents, 0);
  const grocerySpend = grocery.reduce((s, r) => s + r.cents, 0);
  const diningMeals = dining.reduce((s, r) => s + r.effectivePartySize, 0);
  const diningTxnCount = dining.length;
  const cookedMeals = Math.max(0, reviewDays - diningTxnCount);
  const diningCostPerMeal = diningMeals > 0 ? Math.round(diningSpend / diningMeals) : null;
  const cookingCostPerMeal = cookedMeals > 0 && grocerySpend > 0
    ? Math.round(grocerySpend / cookedMeals)
    : null;
  const currentDiningMealsPerWeek = reviewDays > 0 ? diningMeals * 7 / reviewDays : 0;
  const selectedDining = opts.diningMealsPerWeek !== undefined
    ? clampInt(opts.diningMealsPerWeek, 0, 14)
    : preferences.diningMealsPerWeek ?? clampInt(Math.round(currentDiningMealsPerWeek), 0, 14);

  const currentMonthlyDiningMeals = Math.round(currentDiningMealsPerWeek * daysMonth / 7);
  const diningOutcomes = [];
  for (let mealsPerWeek = 0; mealsPerWeek <= 14; mealsPerWeek++) {
    const targetMeals = Math.round(mealsPerWeek * daysMonth / 7);
    const monthlyDining = diningCostPerMeal === null ? 0 : targetMeals * diningCostPerMeal;
    const cooked = Math.max(0, daysMonth - targetMeals);
    const monthlyCooking = cookingCostPerMeal === null ? null : cooked * cookingCostPerMeal;
    const currentDining = diningCostPerMeal === null ? 0 : currentMonthlyDiningMeals * diningCostPerMeal;
    const currentCooking = cookingCostPerMeal === null ? 0 : Math.max(0, daysMonth - currentMonthlyDiningMeals) * cookingCostPerMeal;
    const targetCooking = monthlyCooking ?? 0;
    const savings = diningCostPerMeal === null
      ? 0
      : (currentDining - monthlyDining) + (currentCooking - targetCooking);
    diningOutcomes.push({
      mealsPerWeek,
      monthlyDining: centsToAmount(monthlyDining),
      monthlyCooking: monthlyCooking === null ? null : centsToAmount(monthlyCooking),
      monthlySavingsVsCurrent: centsToAmount(savings),
      leftover: centsToAmount(leftoverProjected + savings),
    });
  }

  const ridesThisMonth = reviewRows.filter((r) => r.kind === 'rideshare' && r.cents > 0);
  const routeGroups = new Map<string, Enriched[]>();
  for (const ride of ridesThisMonth) {
    if (!ride.tripOrigin || !ride.tripDestination) continue;
    const key = routeKey(ride.tripOrigin, ride.tripDestination);
    const list = routeGroups.get(key) ?? [];
    list.push(ride);
    routeGroups.set(key, list);
  }
  const routes = [...routeGroups.entries()]
    .map(([key, list]) => {
      const [origin, destination] = [list[0]!.tripOrigin!, list[0]!.tripDestination!];
      const monthlyCost = list.reduce((s, r) => s + r.cents, 0);
      const alt = alternatives.get(key);
      const kinds = list.map((r) => r.tripKind).filter((k): k is string => !!k);
      return {
        origin,
        destination,
        rideCount: list.length,
        monthlyCost: centsToAmount(monthlyCost),
        monthlyCostCents: monthlyCost,
        tripKind: kinds[0] ?? null,
        alternativeLabel: alt?.label ?? null,
        alternativeMonthlyCost: alt ? centsToAmount(alt.monthlyCents) : null,
        alternativeCents: alt?.monthlyCents ?? null,
      };
    })
    .sort((a, b) => b.monthlyCostCents - a.monthlyCostCents)
    .slice(0, 3);

  const trendMonths: string[] = [];
  for (let i = 5; i >= 0; i--) trendMonths.push(addMonths(fromMonth, -i).slice(0, 7));
  const spendByMonthCategory = new Map<string, Map<string, number>>();
  const flexibilityByCategory = new Map<string, 'fixed' | 'flexible'>();
  for (const row of rows) {
    if (row.kind === 'transfer' || row.kind === 'income' || row.cents <= 0) continue;
    const byCat = spendByMonthCategory.get(row.category) ?? new Map<string, number>();
    byCat.set(row.month, (byCat.get(row.month) ?? 0) + row.cents);
    spendByMonthCategory.set(row.category, byCat);
    if (!flexibilityByCategory.has(row.category)) {
      flexibilityByCategory.set(row.category, isFixedTxn(row) ? 'fixed' : 'flexible');
    }
  }
  // When the current month is empty the page reviews the prior one; the pace
  // card must then score the review month against the months before it, not
  // show a row of zeros for a month that just started.
  const currentTrendMonth = usingPriorMonth ? priorMonthStart.slice(0, 7) : today.slice(0, 7);
  const baselineMonths = trendMonths.filter((m) => m < currentTrendMonth).slice(-3);
  const categoryTrends = [...spendByMonthCategory.entries()]
    .map(([category, byMonth]) => {
      const points = trendMonths.map((month) => ({
        month,
        amount: centsToAmount(byMonth.get(month) ?? 0),
        cents: byMonth.get(month) ?? 0,
      }));
      const currentCents = currentTrendMonth === today.slice(0, 7)
        ? (flexibilityByCategory.get(category) === 'flexible'
          ? (daysElapsed <= 0 ? (byMonth.get(currentTrendMonth) ?? 0)
            : Math.round((byMonth.get(currentTrendMonth) ?? 0) / daysElapsed * daysMonth))
          : (byMonth.get(currentTrendMonth) ?? 0))
        : (byMonth.get(currentTrendMonth) ?? 0);
      const baselineValues = baselineMonths.map((m) => byMonth.get(m) ?? 0);
      const baseline = baselineValues.length === 3
        ? Math.round(baselineValues.reduce((s, v) => s + v, 0) / 3)
        : null;
      const drifted = baseline !== null
        && baseline >= 5000
        && currentCents - baseline >= 5000
        && currentCents >= baseline * 1.25;
      return {
        category,
        flexibility: flexibilityByCategory.get(category) ?? 'flexible',
        currentMonth: centsToAmount(currentCents),
        currentCents,
        baseline: baseline === null ? null : centsToAmount(baseline),
        baselineCents: baseline,
        drifted,
        points: points.map(({ month, amount }) => ({ month, amount })),
      };
    })
    .sort((a, b) => b.currentCents - a.currentCents);

  // ── A typical month: the last three full months that had spending. The
  // first synced month is usually partial (history starts mid-month), so it
  // is skipped when the earliest transaction lands after the 5th.
  const currentMonthKey = today.slice(0, 7);
  const earliestDate = rows.reduce((min, r) => (r.date < min ? r.date : min), today);
  const partialStartMonth = Number(earliestDate.slice(8, 10)) > 5 ? earliestDate.slice(0, 7) : null;
  const monthsWithSpend = [...new Set(rows
    .filter((r) => r.kind !== 'transfer' && r.kind !== 'income' && r.cents > 0 && r.month < currentMonthKey)
    .map((r) => r.month))].sort();
  let typicalMonths = monthsWithSpend.filter((m) => m !== partialStartMonth);
  if (typicalMonths.length === 0) typicalMonths = monthsWithSpend;
  typicalMonths = typicalMonths.slice(-3);
  const typicalSet = new Set(typicalMonths);
  let typicalIncomeTotal = 0;
  let typicalFixedTotal = 0;
  let typicalFlexibleTotal = 0;
  let typicalDiningSpend = 0;
  let typicalDiningMeals = 0;
  const merchantsByCategory = new Map<string, Map<string, number>>();
  for (const row of rows) {
    if (!typicalSet.has(row.month) || row.kind === 'transfer') continue;
    if (row.kind === 'income') {
      typicalIncomeTotal += Math.abs(row.cents);
      continue;
    }
    if (row.cents <= 0) continue;
    if (isFixedTxn(row)) typicalFixedTotal += row.cents;
    else typicalFlexibleTotal += row.cents;
    if (row.kind === 'dining') {
      typicalDiningSpend += row.cents;
      typicalDiningMeals += row.effectivePartySize;
    }
    const merchants = merchantsByCategory.get(row.category) ?? new Map<string, number>();
    const merchant = row.merchant_name ?? row.name;
    merchants.set(merchant, (merchants.get(merchant) ?? 0) + row.cents);
    merchantsByCategory.set(row.category, merchants);
  }
  const typicalCount = typicalMonths.length;
  const perMonth = (cents: number): number => (typicalCount === 0 ? 0 : Math.round(cents / typicalCount));
  const typicalIncome = perMonth(typicalIncomeTotal);
  const typicalFixed = perMonth(typicalFixedTotal);
  const typicalFlexible = perMonth(typicalFlexibleTotal);
  const typicalSpend = typicalFixed + typicalFlexible;
  const typicalLeftover = typicalIncome - typicalSpend;
  const typicalDays = typicalMonths.reduce((s, m) => s + daysInMonth(`${m}-01`), 0);
  const typicalCategories = [...spendByMonthCategory.entries()]
    .map(([category, byMonth]) => {
      const monthly = perMonth(typicalMonths.reduce((s, m) => s + (byMonth.get(m) ?? 0), 0));
      const topMerchants = [...(merchantsByCategory.get(category) ?? new Map<string, number>()).entries()]
        .sort((a, b) => b[1] - a[1])
        .slice(0, 2)
        .map(([name]) => name);
      return {
        category,
        flexibility: (flexibilityByCategory.get(category) ?? 'flexible') as 'fixed' | 'flexible',
        monthlyAverage: centsToAmount(monthly),
        monthlyCents: monthly,
        share: typicalSpend > 0 ? ratio(monthly, typicalSpend) : null,
        topMerchants,
      };
    })
    .filter((c) => c.monthlyCents > 0)
    .sort((a, b) => b.monthlyCents - a.monthlyCents);
  const typicalGoal = (() => {
    if (typicalLeftover <= 0 || preferences.goalKind === 'none' || !preferences.goalLabel) return null;
    const yearly = centsToAmount(typicalLeftover * 12);
    if (preferences.goalKind === 'target' && preferences.goalTargetAmount) {
      const target = parseAmountToCents(preferences.goalTargetAmount) ?? 0;
      return {
        label: preferences.goalLabel,
        months: target > 0 ? Math.ceil(target / typicalLeftover) : null,
        yearly,
      };
    }
    return { label: preferences.goalLabel, months: null, yearly };
  })();

  // This month against that yardstick. Pace is only scored once a few days
  // are in, and the projection assumes the rest of the month looks typical.
  const spentThisMonth = fixedPosted + flexiblePosted;
  const flexibleExpectedByNow = daysMonth > 0 ? Math.round(typicalFlexible * daysElapsed / daysMonth) : 0;
  const flexiblePace = flexibleExpectedByNow > 0 && daysElapsed >= 3
    ? ratio(flexiblePosted, flexibleExpectedByNow)
    : null;
  const projectedLeftoverIfTypical = (incomeProjected > 0 ? incomeProjected : typicalIncome)
    - Math.max(fixedProjected, typicalFixed)
    - Math.max(flexiblePosted, typicalFlexible);

  // Cash runway: what the bank and cash accounts hold, in typical months.
  const liquidCents = (await accountSummaries({ includeExcluded: false }))
    .filter((a) => a.accountClass === 'asset'
      && (a.category === 'bank' || a.category === 'cash')
      && a.balance !== null)
    .reduce((s, a) => s + (parseAmountToCents(a.balance!.amount) ?? 0), 0);
  const runwayMonths = typicalSpend > 0 ? (liquidCents / typicalSpend).toFixed(1) : null;

  const prefs = preferences;

  const attachGoal = (impactCents: number, dto: LivingTradeoffDto): LivingTradeoffDto => {
    if (impactCents <= 0) return dto;
    if (prefs.goalKind === 'contribution' && prefs.goalLabel) {
      return { ...dto, goalLabel: prefs.goalLabel, goalYearly: centsToAmount(impactCents * 12) };
    }
    if (prefs.goalKind === 'target' && prefs.goalTargetAmount && prefs.goalLabel) {
      const target = parseAmountToCents(prefs.goalTargetAmount) ?? 0;
      const months = target > 0 ? Math.max(1, Math.ceil(target / impactCents)) : null;
      return { ...dto, goalLabel: prefs.goalLabel, goalMonths: months, goalYearly: centsToAmount(impactCents * 12) };
    }
    return { ...dto, goalYearly: centsToAmount(impactCents * 12) };
  };

  const tradeoffs: LivingTradeoffDto[] = [];
  if (diningCostPerMeal !== null && currentDiningMealsPerWeek >= 1) {
    const extra = Math.min(2, Math.max(1, Math.round(currentDiningMealsPerWeek * 10) / 10 >= 2 ? 2 : 1));
    const extraMealsMonth = Math.round(extra * daysMonth / 7);
    const saveEach = diningCostPerMeal - (cookingCostPerMeal ?? 0);
    const monthly = extraMealsMonth * Math.max(0, saveEach);
    if (monthly >= 1000) {
      tradeoffs.push(attachGoal(monthly, emptyTradeoff({
        id: `cook-more-${today.slice(0, 7)}`,
        kind: 'cookMore',
        monthlyImpact: centsToAmount(monthly),
        extraCookedMealsPerWeek: extra,
        diningCostPerMeal: centsToAmount(diningCostPerMeal),
        cookingCostPerMeal: moneyOrNull(cookingCostPerMeal),
      })));
    }
  }
  for (const route of routes) {
    const altSave = route.alternativeCents !== null
      ? Math.max(0, route.monthlyCostCents - route.alternativeCents)
      : route.monthlyCostCents;
    if (route.rideCount >= 2 && altSave >= 1000) {
      tradeoffs.push(attachGoal(altSave, emptyTradeoff({
        id: `ride-${routeKey(route.origin, route.destination)}`,
        kind: 'repeatedRide',
        monthlyImpact: centsToAmount(altSave),
        origin: route.origin,
        destination: route.destination,
        rideCount: route.rideCount,
        rideMonthlyCost: route.monthlyCost,
        alternativeLabel: route.alternativeLabel,
        alternativeMonthlyCost: route.alternativeMonthlyCost,
      })));
    }
  }
  const drift = categoryTrends.find((c) => c.drifted && c.flexibility === 'flexible');
  if (drift && drift.baselineCents !== null) {
    const increase = drift.currentCents - drift.baselineCents;
    tradeoffs.push(attachGoal(increase, emptyTradeoff({
      id: `drift-${drift.category}-${today.slice(0, 7)}`,
      kind: 'categoryDrift',
      monthlyImpact: centsToAmount(increase),
      category: drift.category,
      current: drift.currentMonth,
      baseline: drift.baseline,
    })));
  }
  const fixedShare = shareDenom > 0 ? fixedProjected / shareDenom : 0;
  if (fixedShare >= 0.5 && incomeProjected > 0) {
    tradeoffs.push(emptyTradeoff({
      id: `fixed-share-${today.slice(0, 7)}`,
      kind: 'fixedShare',
      monthlyImpact: centsToAmount(flexibleProjected),
      fixedShareOfIncome: ratio(fixedProjected, incomeProjected),
    }));
  }

  tradeoffs.sort((a, b) => {
    const impact = toCents(Number(b.monthlyImpact)) - toCents(Number(a.monthlyImpact));
    if (impact !== 0) return impact;
    return a.id.localeCompare(b.id);
  });

  const accountNames = await accountDisplayNames();
  const toTxn = (row: Enriched): LivingTransactionDto => ({
    id: row.id,
    accountId: row.account_id,
    accountName: accountNames.get(row.account_id) ?? row.account_name,
    date: row.date,
    name: row.name,
    merchantName: row.merchant_name,
    amount: centsToAmount(row.cents),
    category: row.category,
    livingKind: row.kind,
    partySize: row.party_size,
    effectivePartySize: row.effectivePartySize,
    tripOrigin: row.tripOrigin,
    tripDestination: row.tripDestination,
    tripKind: row.tripKind,
    needsAnnotation: (row.kind === 'dining' && row.party_size === null && !defaults.get(row.key)?.partySize)
      || (row.kind === 'rideshare' && (!row.tripOrigin || !row.tripDestination)),
  });

  return {
    period: {
      month: today.slice(0, 7),
      from: fromMonth,
      to: toMonth,
      today,
      daysElapsed,
      daysInMonth: daysMonth,
      daysRemaining,
      reviewFrom,
      reviewTo,
      usingPriorMonth,
    },
    hasHistory: rows.length > 0,
    income: {
      posted: centsToAmount(incomePosted),
      expectedRemaining: centsToAmount(incomeRemaining),
      projected: centsToAmount(incomeProjected),
    },
    split: {
      fixedPosted: centsToAmount(fixedPosted),
      fixedRemaining: centsToAmount(fixedRemaining),
      fixedProjected: centsToAmount(fixedProjected),
      flexiblePosted: centsToAmount(flexiblePosted),
      flexibleProjected: centsToAmount(flexibleProjected),
      fixedShareOfIncome: ratio(fixedProjected, shareDenom),
      flexibleShareOfIncome: ratio(flexibleProjected, shareDenom),
    },
    leftover: {
      posted: centsToAmount(leftoverPosted),
      projected: centsToAmount(leftoverProjected),
    },
    meals: {
      diningTransactions: diningTxnCount,
      diningMeals,
      diningSpend: centsToAmount(diningSpend),
      diningCostPerMeal: moneyOrNull(diningCostPerMeal),
      grocerySpend: centsToAmount(grocerySpend),
      cookedMeals,
      cookingCostPerMeal: moneyOrNull(cookingCostPerMeal),
      currentDiningMealsPerWeek: currentDiningMealsPerWeek.toFixed(2),
    },
    diningOutcomes,
    selectedDiningMealsPerWeek: selectedDining,
    tradeoffs: tradeoffs.slice(0, 5),
    routes: routes.map(({ monthlyCostCents: _c, alternativeCents: _a, ...rest }) => rest),
    categoryTrends: categoryTrends.map(({ currentCents: _c, baselineCents: _b, ...rest }) => rest),
    obligations: obligations.map((ob) => ({
      id: ob.id,
      name: ob.name,
      amount: centsToAmount(ob.amountCents),
      interval: ob.interval,
      kind: ob.kind,
      monthlyAmount: centsToAmount(monthlyFromInterval(ob.amountCents, ob.interval)),
    })),
    preferences: prefs,
    detected: buildDetected(lastByKey, thisMonth, recurring, defaults),
    typical: {
      months: typicalMonths,
      income: centsToAmount(typicalIncome),
      spend: centsToAmount(typicalSpend),
      fixed: centsToAmount(typicalFixed),
      flexible: centsToAmount(typicalFlexible),
      leftover: centsToAmount(typicalLeftover),
      categories: typicalCategories.map(({ monthlyCents: _m, ...rest }) => rest),
      diningMealsPerWeek: typicalDays > 0 && typicalDiningMeals > 0
        ? (typicalDiningMeals * 7 / typicalDays).toFixed(1)
        : null,
      diningCostPerMeal: typicalDiningMeals > 0
        ? centsToAmount(Math.round(typicalDiningSpend / typicalDiningMeals))
        : null,
      goal: typicalGoal,
    },
    thisMonth: {
      spent: centsToAmount(spentThisMonth),
      expectedFixedRemaining: centsToAmount(fixedRemaining),
      flexibleExpectedByNow: centsToAmount(flexibleExpectedByNow),
      flexiblePace,
      projectedLeftoverIfTypical: centsToAmount(projectedLeftoverIfTypical),
    },
    runway: { liquidCash: centsToAmount(liquidCents), months: runwayMonths },
    transactions: reviewRows.filter((r) => r.kind !== 'transfer').slice(0, 150).map(toTxn),
  };
}

function clipPlace(value: string | null): string | null {
  if (value === null) return null;
  const trimmed = value.trim().replace(/\s+/g, ' ');
  if (!trimmed) return null;
  if (trimmed.length > 80) throw validation('Place names must be 80 characters or fewer.');
  return trimmed;
}

export async function patchTransaction(id: string, patch: {
  partySize?: number | null;
  tripOrigin?: string | null;
  tripDestination?: string | null;
  tripKind?: string | null;
  livingKind?: string | null;
}): Promise<{ updated: true }> {
  const allowed = new Set(['partySize', 'tripOrigin', 'tripDestination', 'tripKind', 'livingKind']);
  for (const key of Object.keys(patch)) {
    if (!allowed.has(key)) throw validation(`Unknown field: ${key}`, { field: key });
  }
  const existing = await db.execute({
    sql: `SELECT id, name, merchant_name FROM transactions WHERE id = ?`,
    args: [id],
  });
  if (!existing.rows.length) throw notFound('Transaction');
  const row = existing.rows[0] as unknown as { id: string; name: string; merchant_name: string | null };

  const sets: string[] = [];
  const args: (string | number | null)[] = [];
  if ('partySize' in patch) {
    if (patch.partySize !== null && patch.partySize !== undefined) {
      if (!Number.isInteger(patch.partySize) || patch.partySize < 1 || patch.partySize > 12) {
        throw validation('partySize must be an integer from 1 to 12.', { field: 'partySize' });
      }
    }
    sets.push('party_size = ?');
    args.push(patch.partySize ?? null);
  }
  if ('tripOrigin' in patch) {
    sets.push('trip_origin = ?');
    args.push(clipPlace(patch.tripOrigin ?? null));
  }
  if ('tripDestination' in patch) {
    sets.push('trip_destination = ?');
    args.push(clipPlace(patch.tripDestination ?? null));
  }
  if ('tripKind' in patch) {
    if (patch.tripKind !== null && patch.tripKind !== undefined && !TRIP_KINDS.has(patch.tripKind)) {
      throw validation('Unknown trip kind.', { field: 'tripKind' });
    }
    sets.push('trip_kind = ?');
    args.push(patch.tripKind ?? null);
  }
  if ('livingKind' in patch) {
    if (patch.livingKind !== null && patch.livingKind !== undefined && !LIVING_KINDS.has(patch.livingKind)) {
      throw validation('Unknown living kind.', { field: 'livingKind' });
    }
    sets.push('living_kind = ?');
    args.push(patch.livingKind ?? null);
  }
  if (sets.length === 0) throw validation('No editable fields supplied.');
  args.push(id);
  await db.execute({ sql: `UPDATE transactions SET ${sets.join(', ')} WHERE id = ?`, args });

  const key = merchantKey(row.merchant_name, row.name);
  const defSets: string[] = [];
  const defArgs: (string | number | null)[] = [];
  if ('partySize' in patch) { defSets.push('party_size = ?'); defArgs.push(patch.partySize ?? null); }
  if ('tripOrigin' in patch) { defSets.push('trip_origin = ?'); defArgs.push(clipPlace(patch.tripOrigin ?? null)); }
  if ('tripDestination' in patch) { defSets.push('trip_destination = ?'); defArgs.push(clipPlace(patch.tripDestination ?? null)); }
  if ('tripKind' in patch) { defSets.push('trip_kind = ?'); defArgs.push(patch.tripKind ?? null); }
  if ('livingKind' in patch) { defSets.push('living_kind = ?'); defArgs.push(patch.livingKind ?? null); }
  if (defSets.length) {
    const now = new Date().toISOString();
    await db.execute({
      sql: `INSERT INTO living_merchant_defaults (merchant_key, party_size, trip_origin, trip_destination, trip_kind, living_kind, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(merchant_key) DO UPDATE SET ${defSets.join(', ')}, updated_at = excluded.updated_at`,
      args: [
        key,
        'partySize' in patch ? (patch.partySize ?? null) : null,
        'tripOrigin' in patch ? clipPlace(patch.tripOrigin ?? null) : null,
        'tripDestination' in patch ? clipPlace(patch.tripDestination ?? null) : null,
        'tripKind' in patch ? (patch.tripKind ?? null) : null,
        'livingKind' in patch ? (patch.livingKind ?? null) : null,
        now,
      ],
    });
  }
  return { updated: true };
}

// The user's own facts about one merchant: what its charges are (rent, income,
// …) and what a month of it costs. The two fields are independent: an absent
// one is left alone, a null one goes back to automatic detection.
export async function putMerchantDefault(input: {
  merchantKey: string;
  monthlyAmount?: string | null;
  kind?: string | null;
}): Promise<{ updated: true }> {
  const key = input.merchantKey.trim().toLowerCase().replace(/\s+/g, ' ');
  if (!key) throw validation('Merchant key is required.', { field: 'merchantKey' });
  const hasAmount = input.monthlyAmount !== undefined;
  const hasKind = input.kind !== undefined;
  if (!hasAmount && !hasKind) throw validation('Supply monthlyAmount, kind, or both.');
  let dbAmount: number | null = null;
  if (typeof input.monthlyAmount === 'string') {
    const cents = parseAmountToCents(input.monthlyAmount);
    if (cents === null || cents <= 0) {
      throw validation('Monthly amount must be a positive amount like 1800.00.', { field: 'monthlyAmount' });
    }
    dbAmount = centsToDbValue(cents);
  }
  const kind = input.kind ?? null;
  if (kind !== null && !LIVING_KINDS.has(kind)) {
    throw validation('Unknown living kind.', { field: 'kind' });
  }
  const sets = [
    ...(hasAmount ? ['monthly_amount = excluded.monthly_amount'] : []),
    ...(hasKind ? ['living_kind = excluded.living_kind'] : []),
    'updated_at = excluded.updated_at',
  ];
  await db.execute({
    sql: `INSERT INTO living_merchant_defaults (merchant_key, monthly_amount, living_kind, updated_at)
          VALUES (?, ?, ?, ?)
          ON CONFLICT(merchant_key) DO UPDATE SET ${sets.join(', ')}`,
    args: [key, dbAmount, kind, new Date().toISOString()],
  });
  return { updated: true };
}

export type LivingMerchantDto = {
  key: string;
  name: string;
  /** What the merchant's charges count as: the user's label, else the wording-based guess. */
  kind: LivingKind;
  /** What the wording alone says; this is what "automatic" means for this merchant. */
  automaticKind: LivingKind;
  /** The user's label, or null while the kind is automatic. */
  labelledKind: LivingKind | null;
  /** The user's expected monthly amount, or null while it is automatic. */
  monthlyAmount: string | null;
  lastDate: string | null;
  /** Signed like a transaction: positive is money out. */
  lastAmount: string | null;
  /** labelled: the user set something. recurring: a detected bill or income. other: everything else. */
  group: 'labelled' | 'recurring' | 'other';
};

const MERCHANT_GROUP_ORDER = { labelled: 0, recurring: 1, other: 2 } as const;

// Every merchant seen in the Living window, so a landlord or employer the
// wording does not give away can still be found and labelled.
export async function livingMerchants(opts: { today?: string } = {}): Promise<{ merchants: LivingMerchantDto[] }> {
  const today = opts.today ?? utcToday();
  const historyFrom = monthStart(addMonths(monthStart(today), -5));
  const [rawRows, defaults] = await Promise.all([loadRows(historyFrom, today), loadDefaults()]);
  const recurringKeys = new Set(
    detectRecurring(rawRows.map((row) => enrich(row, defaults)), today)
      .filter((rec) => rec.kind !== 'grocery' && rec.kind !== 'dining' && rec.kind !== 'rideshare')
      .map((rec) => rec.key),
  );
  const latest = new Map<string, LivingRow>();
  for (const row of rawRows) {
    const key = merchantKey(row.merchant_name, row.name);
    const prev = latest.get(key);
    if (!prev || row.date > prev.date) latest.set(key, row);
  }

  const merchants: LivingMerchantDto[] = [];
  for (const [key, last] of latest) {
    const def = defaults.get(key);
    const automaticKind = classifyLivingKind(last.category, last.name, last.merchant_name, last.amount, last.raw_category);
    const labelledKind = def?.livingKind ?? null;
    const kind = labelledKind ?? automaticKind;
    const monthlyCents = def?.monthlyCents ?? null;
    merchants.push({
      key,
      name: last.merchant_name || last.name,
      kind,
      automaticKind,
      labelledKind,
      monthlyAmount: moneyOrNull(monthlyCents),
      lastDate: last.date,
      lastAmount: centsToAmount(toCents(last.amount)),
      group: labelledKind !== null || monthlyCents !== null
        ? 'labelled'
        : recurringKeys.has(key) || kind === 'income' || FIXED_KINDS.has(kind) ? 'recurring' : 'other',
    });
  }
  // A label outlives its charges: keep it listed so it can still be changed.
  for (const [key, def] of defaults) {
    if (latest.has(key) || (def.livingKind === null && def.monthlyCents === null)) continue;
    merchants.push({
      key,
      name: key,
      kind: def.livingKind ?? 'other',
      automaticKind: 'other',
      labelledKind: def.livingKind,
      monthlyAmount: moneyOrNull(def.monthlyCents),
      lastDate: null,
      lastAmount: null,
      group: 'labelled',
    });
  }
  const size = (m: LivingMerchantDto) => Math.abs(Number(m.lastAmount ?? 0));
  merchants.sort((a, b) =>
    MERCHANT_GROUP_ORDER[a.group] - MERCHANT_GROUP_ORDER[b.group]
    || size(b) - size(a)
    || a.name.localeCompare(b.name));
  return { merchants };
}

export async function putLivingPreferences(input: {
  diningMealsPerWeek?: number | null;
  goalKind?: GoalKind;
  goalLabel?: string | null;
  goalMonthlyContribution?: string | null;
  goalTargetAmount?: string | null;
}): Promise<{ updated: true }> {
  const allowed = new Set([
    'diningMealsPerWeek', 'goalKind', 'goalLabel', 'goalMonthlyContribution', 'goalTargetAmount',
  ]);
  for (const key of Object.keys(input)) {
    if (!allowed.has(key)) throw validation(`Unknown field: ${key}`, { field: key });
  }
  if ('diningMealsPerWeek' in input) {
    const meals = input.diningMealsPerWeek;
    if (meals === null || meals === undefined) await setSetting(PREF_DINING, '');
    else {
      if (!Number.isInteger(meals) || meals < 0 || meals > 14) {
        throw validation('diningMealsPerWeek must be an integer from 0 to 14.', { field: 'diningMealsPerWeek' });
      }
      await setSetting(PREF_DINING, String(meals));
    }
  }
  if ('goalKind' in input) {
    if (!input.goalKind || !GOAL_KINDS.has(input.goalKind)) {
      throw validation('goalKind must be none|contribution|target.', { field: 'goalKind' });
    }
    await setSetting(PREF_GOAL_KIND, input.goalKind);
  }
  if ('goalLabel' in input) {
    const label = input.goalLabel === null ? '' : String(input.goalLabel).trim();
    if (label.length > 80) throw validation('goalLabel is too long.', { field: 'goalLabel' });
    await setSetting(PREF_GOAL_LABEL, label);
  }
  if ('goalMonthlyContribution' in input) {
    const monthly = input.goalMonthlyContribution;
    if (monthly === null || monthly === undefined || monthly === '') {
      await setSetting(PREF_GOAL_MONTHLY, '');
    } else {
      if (parseAmountToCents(monthly) === null) {
        throw validation('goalMonthlyContribution must be a two-decimal amount.', { field: 'goalMonthlyContribution' });
      }
      await setSetting(PREF_GOAL_MONTHLY, monthly);
    }
  }
  if ('goalTargetAmount' in input) {
    const target = input.goalTargetAmount;
    if (target === null || target === undefined || target === '') {
      await setSetting(PREF_GOAL_TARGET, '');
    } else {
      if (parseAmountToCents(target) === null) {
        throw validation('goalTargetAmount must be a two-decimal amount.', { field: 'goalTargetAmount' });
      }
      await setSetting(PREF_GOAL_TARGET, target);
    }
  }
  return { updated: true };
}

export async function createObligation(input: {
  name?: string;
  amount?: string;
  interval?: string;
  kind?: string;
}): Promise<{ id: string }> {
  const name = String(input.name ?? '').trim();
  if (!name || name.length > 80) throw validation('Name must be 1–80 characters.', { field: 'name' });
  const cents = parseAmountToCents(String(input.amount ?? ''));
  if (cents === null || cents <= 0) throw validation('Amount must be a positive two-decimal number.', { field: 'amount' });
  const interval = String(input.interval ?? 'monthly');
  if (!OBLIGATION_INTERVALS.has(interval)) throw validation('interval must be weekly|monthly|yearly.', { field: 'interval' });
  const kind = String(input.kind ?? 'other');
  if (!OBLIGATION_KINDS.has(kind)) throw validation('Unknown obligation kind.', { field: 'kind' });
  const id = `living-${randomUUID()}`;
  await db.execute({
    sql: `INSERT INTO living_obligations (id, name, amount, interval, kind, created_at)
          VALUES (?, ?, ?, ?, ?, ?)`,
    args: [id, name, centsToDbValue(cents), interval, kind, new Date().toISOString()],
  });
  return { id };
}

export async function patchObligation(id: string, patch: {
  name?: string;
  amount?: string;
  interval?: string;
  kind?: string;
}): Promise<{ updated: true }> {
  const existing = await db.execute({ sql: 'SELECT id FROM living_obligations WHERE id = ?', args: [id] });
  if (!existing.rows.length) throw notFound('Obligation');
  const sets: string[] = [];
  const args: (string | number)[] = [];
  if ('name' in patch) {
    const name = String(patch.name ?? '').trim();
    if (!name || name.length > 80) throw validation('Name must be 1–80 characters.', { field: 'name' });
    sets.push('name = ?'); args.push(name);
  }
  if ('amount' in patch) {
    const cents = parseAmountToCents(String(patch.amount ?? ''));
    if (cents === null || cents <= 0) throw validation('Amount must be a positive two-decimal number.', { field: 'amount' });
    sets.push('amount = ?'); args.push(centsToDbValue(cents));
  }
  if ('interval' in patch) {
    if (!OBLIGATION_INTERVALS.has(String(patch.interval))) throw validation('interval must be weekly|monthly|yearly.', { field: 'interval' });
    sets.push('interval = ?'); args.push(String(patch.interval));
  }
  if ('kind' in patch) {
    if (!OBLIGATION_KINDS.has(String(patch.kind))) throw validation('Unknown obligation kind.', { field: 'kind' });
    sets.push('kind = ?'); args.push(String(patch.kind));
  }
  if (sets.length === 0) throw validation('No editable fields supplied.');
  args.push(id);
  await db.execute({ sql: `UPDATE living_obligations SET ${sets.join(', ')} WHERE id = ?`, args });
  return { updated: true };
}

export async function deleteObligation(id: string): Promise<{ deleted: true }> {
  const existing = await db.execute({ sql: 'SELECT id FROM living_obligations WHERE id = ?', args: [id] });
  if (!existing.rows.length) throw notFound('Obligation');
  await db.execute({ sql: 'DELETE FROM living_obligations WHERE id = ?', args: [id] });
  return { deleted: true };
}

export async function putRouteAlternative(input: {
  origin?: string;
  destination?: string;
  label?: string | null;
  monthlyAmount?: string | null;
}): Promise<{ updated: true }> {
  const origin = clipPlace(input.origin ?? null);
  const destination = clipPlace(input.destination ?? null);
  if (!origin || !destination) throw validation('origin and destination are required.');
  if (input.monthlyAmount === null || input.monthlyAmount === '' || input.label === null) {
    await db.execute({
      sql: 'DELETE FROM living_route_alternatives WHERE origin = ? AND destination = ?',
      args: [origin, destination],
    });
    return { updated: true };
  }
  const cents = parseAmountToCents(String(input.monthlyAmount ?? ''));
  if (cents === null || cents < 0) throw validation('monthlyAmount must be a two-decimal amount.', { field: 'monthlyAmount' });
  const label = String(input.label ?? '').trim();
  if (!label || label.length > 80) throw validation('label must be 1–80 characters.', { field: 'label' });
  await db.execute({
    sql: `INSERT INTO living_route_alternatives (origin, destination, label, monthly_amount)
          VALUES (?, ?, ?, ?)
          ON CONFLICT(origin, destination) DO UPDATE SET label = excluded.label, monthly_amount = excluded.monthly_amount`,
    args: [origin, destination, label, centsToDbValue(cents)],
  });
  return { updated: true };
}
