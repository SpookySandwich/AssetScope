import { describe, it, expect, beforeEach, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});

import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema } from '../../native-api/src/services/schema.js';
import {
  rewardCategoryFor, rewardsOverview, putRewardCard, putRewardPreferences, quarterOf, refreshRotatingCalendars,
  accountIdentities,
} from '../../native-api/src/services/rewards.js';
import { loadAccounts } from '../../native-api/src/services/networth.js';
import { parseChaseAnnouncement, chaseAnnouncementUrl } from '../../native-api/src/services/rotating-categories.js';

const TODAY = '2026-10-22';

async function clean() {
  for (const t of ['transactions', 'accounts']) {
    await db.execute(`DELETE FROM ${t}`).catch(() => {});
  }
  await db.execute("DELETE FROM settings WHERE key LIKE 'rewards_%'").catch(() => {});
}

async function addCard(id: string, name: string, institution: string, type = 'credit') {
  await db.execute({
    sql: `INSERT INTO accounts (id, name, type, source, institution_name, mask, excluded, archived_at)
          VALUES (?, ?, ?, 'plaid', ?, ?, 0, NULL)`,
    args: [id, name, type, institution, id.slice(-4)],
  });
}

async function addPurchase(id: string, accountId: string, date: string, amount: number, category: string, detailed: string | null, merchant = id) {
  await db.execute({
    sql: `INSERT INTO transactions (id, account_id, date, name, merchant_name, amount, category, raw_category, pending, ignored)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0, 0)`,
    args: [id, accountId, date, merchant, merchant, amount, category, detailed],
  });
}

const overview = (range: '1m' | '3m' | '1y' = '3m') => rewardsOverview({ range, today: TODAY });
const bestIds = (o: Awaited<ReturnType<typeof overview>>, category: string) =>
  o.categories.find((c) => c.category === category)!.best.map((b) => b.accountId);
const activate = (accountId: string, quarter = '2026-Q4', today = TODAY) =>
  putRewardCard(accountId, { activation: { quarter, activated: true } }, { today });
const dining = (id: string, accountId: string, date: string, amount: number) =>
  addPurchase(id, accountId, date, amount, 'FOOD_AND_DRINK', 'FOOD_AND_DRINK_RESTAURANT');
const groceries = (id: string, accountId: string, date: string, amount: number) =>
  addPurchase(id, accountId, date, amount, 'FOOD_AND_DRINK', 'FOOD_AND_DRINK_GROCERIES');

beforeEach(async () => {
  await ensureNativeSchema();
  await clean();
});

describe('rewardCategoryFor', () => {
  it('maps Plaid categories to the categories issuers pay bonuses on', () => {
    expect(rewardCategoryFor('FOOD_AND_DRINK', 'FOOD_AND_DRINK_RESTAURANT', 'Chipotle')).toBe('dining');
    expect(rewardCategoryFor('FOOD_AND_DRINK', 'FOOD_AND_DRINK_GROCERIES', 'Publix')).toBe('groceries');
    expect(rewardCategoryFor('FOOD_AND_DRINK', null, 'Whole Foods')).toBe('groceries');
    expect(rewardCategoryFor('TRANSPORTATION', 'TRANSPORTATION_GAS', 'Shell')).toBe('gas');
    expect(rewardCategoryFor('TRANSPORTATION', 'TRANSPORTATION_TAXIS_AND_RIDE_SHARES', 'Uber')).toBe('transit');
    expect(rewardCategoryFor('MEDICAL', 'MEDICAL_PHARMACIES_AND_SUPPLEMENTS', 'CVS')).toBe('drugstores');
    expect(rewardCategoryFor('ENTERTAINMENT', 'ENTERTAINMENT_TV_AND_MOVIES', 'Netflix')).toBe('streaming');
    expect(rewardCategoryFor('RENT_AND_UTILITIES', 'RENT_AND_UTILITIES_TELEPHONE', 'Google Fi')).toBe('phoneInternet');
    expect(rewardCategoryFor('GENERAL_SERVICES', 'GENERAL_SERVICES_OTHER_GENERAL_SERVICES', 'Duolingo')).toBe('other');
  });

  it('skips rows that are not purchases', () => {
    expect(rewardCategoryFor('LOAN_PAYMENTS', 'LOAN_PAYMENTS_CREDIT_CARD_PAYMENT', 'Chase')).toBeNull();
    expect(rewardCategoryFor('TRANSFER_OUT', null, 'Robinhood')).toBeNull();
    expect(rewardCategoryFor('BANK_FEES', null, 'Late fee')).toBeNull();
  });

  it('reads calendar quarters', () => {
    expect(quarterOf('2026-01-01')).toBe('2026-Q1');
    expect(quarterOf('2026-09-30')).toBe('2026-Q3');
    expect(quarterOf('2026-10-01')).toBe('2026-Q4');
  });
});

describe('rewardsOverview', () => {
  it('detects card products by name and ranks each category', async () => {
    await addCard('csp-9001', 'Chase Sapphire Preferred', 'Chase');
    await addCard('rh-9003', 'Robinhood Credit Card', 'Robinhood');
    await addCard('ccr-9004', 'Customized Cash Rewards Visa Signature', 'Bank of America');

    const o = await overview();
    expect(o.quarter).toBe('2026-Q4');
    expect(o.cards.map((c) => [c.accountId, c.productId, c.productSource])).toEqual([
      ['csp-9001', 'chase-sapphire-preferred', 'detected'],
      ['ccr-9004', 'bofa-customized-cash', 'detected'],
      ['rh-9003', 'robinhood-gold-card', 'detected'],
    ]);
    // 3x points at 1¢ ties 3% cash; ties go to cash, but both are listed as best.
    expect(bestIds(o, 'dining')).toEqual(['rh-9003', 'csp-9001']);
    expect(bestIds(o, 'other')).toEqual(['rh-9003']);
    const gas = o.categories.find((c) => c.category === 'gas')!;
    expect(gas.best.map((b) => b.accountId)).toEqual(['ccr-9004', 'rh-9003', 'csp-9001']);
    expect(gas.best[0]).toMatchObject({ rate: '3', unit: 'percent', effectiveRate: '0.0300' });
  });

  it('values Ultimate Rewards points at the saved value, pooling Freedom points only with a premium card', async () => {
    await addCard('flex-9002', 'Ultimate Rewards', 'Chase');
    await putRewardCard('flex-9002', { productId: 'chase-freedom-flex' }, { today: TODAY });
    await activate('flex-9002');
    await putRewardPreferences({ pointValues: { 'chase-ur': '1.50' } });

    let o = await overview();
    // Without a Sapphire, Freedom points cash out at 1¢.
    expect(o.categories.find((c) => c.category === 'dining')!.best[0].effectiveRate).toBe('0.0700');

    await addCard('csp-9001', 'Ultimate Rewards', 'Chase');
    await putRewardCard('csp-9001', { productId: 'chase-sapphire-preferred' }, { today: TODAY });
    o = await overview();
    expect(o.pointValues).toEqual([expect.objectContaining({ currency: 'chase-ur', centsPerPoint: '1.50' })]);
    const row = o.categories.find((c) => c.category === 'dining')!;
    expect(row.best[0]).toMatchObject({ accountId: 'flex-9002', rate: '7', unit: 'points', effectiveRate: '0.1050' });
    expect(row.runnerUp).toMatchObject({ accountId: 'csp-9001', effectiveRate: '0.0450' });
  });

  it('judges each purchase by the rotating categories of its own quarter', async () => {
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    await addCard('dc-1111', 'Citi Double Cash', 'Citi');
    await activate('flex-9002');
    await addPurchase('q3-dinner', 'dc-1111', '2026-09-15', 100, 'FOOD_AND_DRINK', 'FOOD_AND_DRINK_RESTAURANT');
    await addPurchase('q4-dinner', 'dc-1111', '2026-10-10', 100, 'FOOD_AND_DRINK', 'FOOD_AND_DRINK_RESTAURANT');

    const o = await overview();
    // Q3 has no list on file, so Flex earns its everyday 3x; Q4's activated list puts dining at 7x.
    expect(o.review).toMatchObject({ spend: '200.00', earned: '4.00', bestPossible: '10.00', missed: '6.00', transactionCount: 2 });
    expect(o.review.topMisses.map((m) => [m.transactionId, m.missed, m.bestAccountId])).toEqual([
      ['q4-dinner', '5.00', 'flex-9002'],
      ['q3-dinner', '1.00', 'flex-9002'],
    ]);
    const flex = o.cards.find((c) => c.accountId === 'flex-9002')!;
    expect(flex).toMatchObject({ rotatingSource: 'calendar', rotatingActivateBy: '2026-12-14' });
    expect(flex.rotatingCategories).toEqual(['groceries', 'dining']);
  });

  it('keeps an empty list as a quarter the user did not activate', async () => {
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    await addCard('dc-1111', 'Citi Double Cash', 'Citi');
    await putRewardCard('flex-9002', { rotatingCategories: [] }, { today: TODAY });

    const o = await overview();
    const flex = o.cards.find((c) => c.accountId === 'flex-9002')!;
    expect(flex).toMatchObject({ rotatingCategories: [], rotatingSource: 'chosen' });
    expect(o.categories.find((c) => c.category === 'groceries')!.best[0].accountId).toBe('dc-1111');
    expect(o.categories.find((c) => c.category === 'dining')!.best[0]).toMatchObject({ accountId: 'flex-9002', rate: '3' });
  });

  it('warns when the spending pace would pass the best card\'s quarterly cap', async () => {
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    await activate('flex-9002');
    await addPurchase('groc-1', 'flex-9002', '2026-10-05', 600, 'FOOD_AND_DRINK', 'FOOD_AND_DRINK_GROCERIES');

    const o = await overview('1m');
    // $600 in 30 days runs to about $1,820 a quarter, over the shared $1,500 cap.
    const groceries = o.categories.find((c) => c.category === 'groceries')!;
    expect(groceries.capWarning).toEqual({ accountId: 'flex-9002', cap: '1500.00', quarterlySpend: '1820.00' });
    expect(o.categories.find((c) => c.category === 'dining')!.capWarning).not.toBeNull();
    expect(o.categories.find((c) => c.category === 'drugstores')!.capWarning).toBeNull();
  });

  it('applies the chosen 3% category of a Customized Cash card', async () => {
    await addCard('ccr-9004', 'Customized Cash Rewards Visa Signature', 'Bank of America');
    await putRewardCard('ccr-9004', { choiceCategory: 'onlineShopping' }, { today: TODAY });

    const o = await overview();
    const card = o.cards[0];
    expect(card.choiceCategory).toBe('onlineShopping');
    expect(card.bonuses.map((b) => `${b.category}:${b.rate}`)).toEqual([
      'streaming:3', 'onlineShopping:3', 'phoneInternet:3', 'groceries:2',
    ]);
  });

  it('leaves unidentified cards out of the review totals', async () => {
    await addCard('rh-9003', 'Robinhood Credit Card', 'Robinhood');
    await addCard('mystery-9999', 'Visa Signature', 'Some Credit Union');
    await addPurchase('known', 'rh-9003', '2026-10-01', 50, 'FOOD_AND_DRINK', 'FOOD_AND_DRINK_RESTAURANT');
    await addPurchase('unknown', 'mystery-9999', '2026-10-02', 80, 'FOOD_AND_DRINK', 'FOOD_AND_DRINK_RESTAURANT');
    await addPurchase('payment', 'rh-9003', '2026-10-03', 500, 'LOAN_PAYMENTS', 'LOAN_PAYMENTS_CREDIT_CARD_PAYMENT');

    const o = await overview();
    expect(o.categories.find((c) => c.category === 'dining')).toMatchObject({ spend: '130.00', transactionCount: 2 });
    expect(o.review).toMatchObject({ spend: '50.00', earned: '1.50', unidentifiedSpend: '80.00', transactionCount: 1 });
    expect(o.review.byCategory).toEqual([expect.objectContaining({
      category: 'dining', spend: '50.00', mostUsedAccountId: 'rh-9003',
    })]);
    expect(o.cards.find((c) => c.accountId === 'mystery-9999')).toMatchObject({ productId: null, earned: null, spend: '80.00' });
  });
});

describe('card identification', () => {
  const annualFee = (id: string, accountId: string, amount: number) =>
    addPurchase(id, accountId, '2026-09-01', amount, 'BANK_FEES', 'BANK_FEES_OTHER_BANK_FEES', 'ANNUAL MEMBERSHIP FEE');
  const card = (o: Awaited<ReturnType<typeof overview>>, accountId: string) => o.cards.find((c) => c.accountId === accountId)!;

  it('tells Chase Ultimate Rewards cards apart by the annual fee', async () => {
    await addCard('ur-9001', 'Ultimate Rewards®', 'Chase');
    await addCard('ur-9002', 'Ultimate Rewards®', 'Chase');
    await annualFee('fee', 'ur-9001', 95);

    const o = await overview();
    expect(card(o, 'ur-9001')).toMatchObject({
      productId: 'chase-sapphire-preferred', productSource: 'detected', productDetectedBy: 'annualFee',
      displayName: 'Chase Sapphire Preferred', productCandidates: [],
    });
    // Without a fee the Freedom cards look alike, so they are offered first.
    expect(card(o, 'ur-9002')).toMatchObject({
      productId: null, productSource: null, productDetectedBy: null, displayName: 'Ultimate Rewards®',
      productCandidates: ['chase-freedom-flex', 'chase-freedom-unlimited', 'chase-freedom', 'chase-sapphire-preferred'],
    });
    expect(bestIds(o, 'dining')).toEqual(['ur-9001']);
  });

  it('reads the fee only for the shared Ultimate Rewards name, and only a fee a known card charges', async () => {
    await addCard('ur-0001', 'Ultimate Rewards®', 'Chase');
    await addCard('other-0002', 'Visa Signature', 'Some Credit Union');
    await annualFee('fee-ur', 'ur-0001', 550);
    await annualFee('fee-other', 'other-0002', 95);

    const o = await overview();
    expect(card(o, 'ur-0001').productId).toBeNull();
    expect(card(o, 'other-0002')).toMatchObject({ productId: null, productCandidates: [] });
  });

  it('keeps the user\'s pick over the fee, and checks writes against the card the fee detected', async () => {
    await addCard('ur-9001', 'Ultimate Rewards®', 'Chase');
    await annualFee('fee', 'ur-9001', 95);
    await putRewardCard('ur-9001', { productId: 'chase-freedom-flex' });
    expect(card(await overview(), 'ur-9001')).toMatchObject({
      productId: 'chase-freedom-flex', productSource: 'chosen', productDetectedBy: null, displayName: 'Chase Freedom Flex',
    });
    await putRewardCard('ur-9001', { productId: null });
    expect(card(await overview(), 'ur-9001')).toMatchObject({ productId: 'chase-sapphire-preferred', productDetectedBy: 'annualFee' });
    await expect(putRewardCard('ur-9001', { rotatingCategories: ['dining'] })).rejects.toThrow(/no rotating categories/);
  });

  it('names an account by its nickname, then by the card when the bank\'s name does not say which it is', async () => {
    await addCard('ur-9002', 'Ultimate Rewards®', 'Chase');
    await addCard('ccr-9004', 'Customized Cash Rewards Visa Signature', 'Bank of America');
    await addCard('mystery-9999', 'Visa Signature', 'Some Credit Union');
    await addCard('chk-9005', 'TOTAL CHECKING', 'Chase', 'depository');
    await putRewardCard('ur-9002', { productId: 'chase-freedom-flex' });
    await putRewardCard('ccr-9004', { productId: 'bofa-customized-cash' });
    await putRewardCard('mystery-9999', { productId: 'flat-2' });

    const identify = async () => Object.fromEntries(await accountIdentities(await loadAccounts()));
    expect(await identify()).toEqual({
      'ur-9002': { displayName: 'Chase Freedom Flex', cardProduct: { id: 'chase-freedom-flex', name: 'Chase Freedom Flex', source: 'chosen' } },
      // The bank's name already says which card it is, and a flat rate is not a name.
      'ccr-9004': {
        displayName: 'Customized Cash Rewards Visa Signature',
        cardProduct: { id: 'bofa-customized-cash', name: 'Customized Cash Rewards', source: 'chosen' },
      },
      'mystery-9999': { displayName: 'Visa Signature', cardProduct: { id: 'flat-2', name: 'Other card, 2% on everything', source: 'chosen' } },
      'chk-9005': { displayName: 'TOTAL CHECKING', cardProduct: null },
    });

    await db.execute("UPDATE accounts SET nickname = 'Daily card' WHERE id = 'ur-9002'");
    expect((await identify())['ur-9002'].displayName).toBe('Daily card');
  });
});

// The parts of Chase's quarterly release the reader relies on: the title and the deadline sentence.
const release = (title: string, body = '') =>
  `<html><head><title>${title}</title><script>var t = "until May 1, 2020, to activate";</script></head>`
  + `<body><h1>${title}</h1><p>${body}</p></body></html>`;

describe('parseChaseAnnouncement', () => {
  it('reads the picks from the title and the deadline from the body', () => {
    expect(parseChaseAnnouncement(release(
      'Chase Freedom&reg; Announces Q3 2026 5% Cash Back Categories: Gas Stations and EV Charging, Public Transit, Select Live Entertainment and United Way',
      'Cardmembers have until September 14th, 2026, to activate these categories.',
    ), '2026-Q3')).toEqual({
      categories: ['gas', 'transit', 'entertainment'],
      untracked: ['United Way'],
      activateBy: '2026-09-14',
    });
  });

  it('keeps store picks as untracked and never reads Chase Travel as travel', () => {
    expect(parseChaseAnnouncement(release(
      'Chase Freedom® Announces Q2 2026 5% Cash Back Categories: Amazon, Whole Foods Market, Chase Travel and Feeding America®',
    ), '2026-Q2')).toEqual({
      categories: [],
      untracked: ['Amazon', 'Whole Foods Market', 'Chase Travel', 'Feeding America'],
      activateBy: '2026-06-14',
    });
  });

  it('drops parenthetical exclusions and ignores a deadline outside the quarter', () => {
    expect(parseChaseAnnouncement(release(
      'Chase Freedom Announces Q1 2027 Categories: Top Streaming Services &amp; Grocery Stores (excluding Walmart and Target)',
      'Cardmembers have until December 14, 2026, to activate last quarter.',
    ), '2027-Q1')).toEqual({ categories: ['streaming', 'groceries'], untracked: [], activateBy: '2027-03-14' });
  });

  it('returns null when the page is not laid out as a category release', () => {
    expect(parseChaseAnnouncement(release('Chase Media Center'), '2027-Q1')).toBeNull();
    expect(parseChaseAnnouncement(release('Payments and Cards: Chase Freedom'), '2027-Q1')).toBeNull();
    expect(parseChaseAnnouncement('<html></html>', '2027-Q1')).toBeNull();
  });
});

describe('refreshRotatingCalendars', () => {
  const HOUR = 60 * 60 * 1000;
  const NOW = Date.parse('2026-10-22T12:00:00Z');
  const pages: Record<string, string> = {};
  const fetchMock = vi.fn(async (url: string | URL | Request) => {
    const body = pages[String(url)];
    return new Response(body ?? 'Not found', { status: body ? 200 : 404 });
  });
  const refresh = (today = TODAY, now = NOW) =>
    refreshRotatingCalendars({ today, now, fetch: fetchMock as unknown as typeof fetch });
  const fetched = () => fetchMock.mock.calls.map(([url]) => String(url));

  beforeEach(() => {
    fetchMock.mockClear();
    for (const key of Object.keys(pages)) delete pages[key];
  });

  it('stays offline when no card has rotating categories', async () => {
    await addCard('csp-9001', 'Chase Sapphire Preferred', 'Chase');
    await refresh();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('reads this quarter and the next, then rechecks only a missing page after 12 hours', async () => {
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    pages[chaseAnnouncementUrl('2026-Q4')] = release(
      'Chase Freedom® Announces Q4 2026 5% Cash Back Categories: Grocery Stores, Dining and American Red Cross Donations',
      'Cardmembers have until December 14, 2026, to activate these categories.',
    );

    await refresh();
    expect(fetched()).toEqual([chaseAnnouncementUrl('2026-Q4'), chaseAnnouncementUrl('2027-Q1')]);
    await refresh(TODAY, NOW + 11 * HOUR);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await refresh(TODAY, NOW + 13 * HOUR);
    expect(fetched().slice(2)).toEqual([chaseAnnouncementUrl('2027-Q1')]);

    const flex = (await overview()).cards[0];
    expect(flex).toMatchObject({
      rotatingCategories: ['groceries', 'dining'],
      rotatingSource: 'calendar',
      rotatingActivateBy: '2026-12-14',
      rotatingUntracked: ['American Red Cross Donations'],
      rotatingAnnouncement: { url: chaseAnnouncementUrl('2026-Q4'), state: 'read' },
      // Previewed by Chase before the quarter's own page, so not open to activate yet.
      rotatingNext: { quarter: '2027-Q1', categories: ['groceries', 'streaming'], open: false },
      activation: { url: 'https://www.chasebonus.com/', quarter: '2026-Q4', activateBy: '2026-12-14' },
    });
  });

  it('moves activation to next quarter once its page is up and this deadline has passed', async () => {
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    pages[chaseAnnouncementUrl('2027-Q1')] = release(
      'Chase Freedom® Announces Q1 2027 5% Cash Back Categories: Top Streaming Services and Grocery Stores',
      'Cardmembers have until March 13, 2027, to activate these categories.',
    );

    await refresh('2026-12-16', Date.parse('2026-12-16T12:00:00Z'));
    const flex = (await rewardsOverview({ today: '2026-12-16' })).cards[0];
    expect(flex.rotatingNext).toEqual({
      quarter: '2027-Q1', categories: ['streaming', 'groceries'], untracked: [], activateBy: '2027-03-13', open: true, activated: false,
    });
    expect(flex.activation).toMatchObject({ quarter: '2027-Q1', activateBy: '2027-03-13' });
  });

  it('drops a quarter the user marked activated, then offers the next once it opens', async () => {
    const today = '2026-12-10';
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    pages[chaseAnnouncementUrl('2027-Q1')] = release(
      'Chase Freedom® Announces Q1 2027 5% Cash Back Categories: Top Streaming Services and Grocery Stores',
      'Cardmembers have until March 13, 2027, to activate these categories.',
    );
    await refresh(today, Date.parse('2026-12-10T12:00:00Z'));
    const flex = async () => (await rewardsOverview({ today })).cards[0];
    expect(await flex()).toMatchObject({ rotatingActivated: false, activation: { quarter: '2026-Q4' } });

    await putRewardCard('flex-9002', { activation: { quarter: '2026-Q4', activated: true } }, { today });
    expect(await flex()).toMatchObject({
      rotatingActivated: true,
      rotatingCategories: ['groceries', 'dining'],
      rotatingNext: { quarter: '2027-Q1', activated: false },
      activation: { quarter: '2027-Q1', activateBy: '2027-03-13' },
    });

    await putRewardCard('flex-9002', { activation: { quarter: '2027-Q1', activated: true } }, { today });
    expect(await flex()).toMatchObject({ rotatingNext: { activated: true }, activation: null });

    await putRewardCard('flex-9002', { activation: { quarter: '2027-Q1', activated: false } }, { today });
    expect((await flex()).activation).toMatchObject({ quarter: '2027-Q1' });
  });

  it('records failures without throwing, and the user\'s own list still wins', async () => {
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    await putRewardCard('flex-9002', { rotatingCategories: ['gas'] }, { today: TODAY });
    fetchMock.mockRejectedValueOnce(new Error('ENOTFOUND media.chase.com'));
    pages[chaseAnnouncementUrl('2027-Q1')] = release('Chase Media Center');

    await expect(refresh()).resolves.toBeUndefined();
    const flex = (await overview()).cards[0];
    expect(flex).toMatchObject({ rotatingCategories: ['gas'], rotatingSource: 'chosen', rotatingUntracked: [] });
    // The next quarter's page is up but unreadable: activation has begun, the built-in list stands.
    expect(flex.rotatingNext).toMatchObject({ quarter: '2027-Q1', categories: ['groceries', 'streaming'], open: true });
  });

  it('flags an unreadable page for a quarter with no list on file', async () => {
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    pages[chaseAnnouncementUrl('2027-Q2')] = release('Chase Media Center');

    await refresh('2027-04-05', Date.parse('2027-04-05T12:00:00Z'));
    const flex = (await rewardsOverview({ today: '2027-04-05' })).cards[0];
    expect(flex).toMatchObject({
      rotatingCategories: [],
      rotatingSource: null,
      rotatingAnnouncement: { url: chaseAnnouncementUrl('2027-Q2'), state: 'unreadable' },
      rotatingNext: null,
      activation: { quarter: '2027-Q2', activateBy: '2027-06-14' },
    });
  });
});

describe('Chase Freedom quarterly bonus', () => {
  const FLEX = 'flex-9002';
  const FREEDOM = 'free-2222';
  const rate = (o: Awaited<ReturnType<typeof overview>>, accountId: string, category: string) =>
    o.cards.find((c) => c.accountId === accountId)!.bonuses.find((b) => b.category === category)?.rate;

  it('pays 7x on Flex dining and 5x on groceries once activated, counting the base once', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await activate(FLEX);
    await dining('dinner', FLEX, '2026-10-10', 100);
    await groceries('market', FLEX, '2026-10-11', 100);

    const o = await overview();
    const flex = o.cards[0];
    expect(flex.bonuses).toEqual([
      { category: 'dining', rate: '7' },
      { category: 'groceries', rate: '5' },
      { category: 'drugstores', rate: '3' },
    ]);
    expect(flex).toMatchObject({ rotatingStatus: 'activated', earned: '12.00' });
    expect(o.categories.find((c) => c.category === 'dining')!.best[0]).toMatchObject({ accountId: FLEX, rate: '7', effectiveRate: '0.0700' });
    expect(o.review.byCategory.find((c) => c.category === 'dining')).toMatchObject({ earned: '7.00' });
    expect(o.review.byCategory.find((c) => c.category === 'groceries')).toMatchObject({ earned: '5.00' });
  });

  it('keeps Chase Freedom at 5x on dining, since it has no year-round dining bonus', async () => {
    await addCard(FREEDOM, 'CHASE FREEDOM', 'Chase');
    await activate(FREEDOM);
    await dining('dinner', FREEDOM, '2026-10-10', 1500);
    await dining('after', FREEDOM, '2026-10-11', 100);

    const before = await rewardsOverview({ today: '2026-10-05' });
    expect(before.cards[0]).toMatchObject({ productId: 'chase-freedom', productSource: 'detected' });
    expect(before.cards[0].bonuses).toEqual([{ category: 'dining', rate: '5' }, { category: 'groceries', rate: '5' }]);

    const after = await overview();
    expect(after.cards[0].rotatingRates).toEqual([{ category: 'groceries', rate: '5' }, { category: 'dining', rate: '5' }]);
    // Past the cap there is nothing left above the 1x base.
    expect(after.cards[0].bonuses).toEqual([]);
    expect(after.cards[0].earned).toBe('76.00');
  });

  it('leaves other Chase cards alone', async () => {
    await addCard('unl-3333', 'Chase Freedom Unlimited', 'Chase');
    await addCard('csp-9001', 'Chase Sapphire Preferred', 'Chase');
    const o = await overview();
    expect(rate(o, 'unl-3333', 'dining')).toBe('3');
    expect(rate(o, 'csp-9001', 'dining')).toBe('3');
  });

  // Pins what the model does today. No release on file settles this, so it is not a bank rule.
  it('adds the quarterly bonus to the base only for a category outside stacksOn', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await putRewardCard(FLEX, { rotatingCategories: ['drugstores'] }, { today: TODAY });
    expect(rate(await overview(), FLEX, 'drugstores')).toBe('5');
  });

  it('shares one cap across categories and returns each to its regular rate past it', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await addCard('dc-1111', 'Citi Double Cash', 'Citi');
    await activate(FLEX);
    await groceries('market', FLEX, '2026-10-05', 1000);
    await dining('dinner', FLEX, '2026-10-06', 500);

    const o = await overview();
    expect(o.cards[0]).toMatchObject({ earned: '85.00', capUsage: { cap: '1500.00', used: '1500.00', remaining: '0.00' } });
    expect(rate(o, FLEX, 'dining')).toBe('3');
    expect(rate(o, FLEX, 'groceries')).toBeUndefined();
    expect(o.categories.find((c) => c.category === 'dining')!.best[0]).toMatchObject({ accountId: FLEX, rate: '3' });
    // Flex falls to 1x on groceries, so the 2% card takes the category.
    expect(bestIds(o, 'groceries')).toEqual(['dc-1111']);
  });

  it('splits a purchase that crosses the cap', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await activate(FLEX);
    await groceries('market', FLEX, '2026-10-05', 1400);
    await dining('dinner', FLEX, '2026-10-06', 200);

    const o = await overview();
    // $100 at 7x and $100 at 3x on the dinner.
    expect(o.review.byCategory.find((c) => c.category === 'dining')).toMatchObject({ earned: '10.00' });
    expect(o.cards[0].earned).toBe('80.00');
  });

  it('counts the quarter\'s earlier purchases toward the cap when the window starts later', async () => {
    const today = '2026-12-20';
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await activate(FLEX);
    await groceries('early', FLEX, '2026-10-05', 1500);
    await dining('late', FLEX, '2026-12-10', 100);

    const o = await rewardsOverview({ range: '1m', today });
    expect(o.review).toMatchObject({ spend: '100.00', earned: '3.00', transactionCount: 1 });
    expect(o.cards[0].capUsage).toMatchObject({ used: '1500.00', remaining: '0.00' });
  });

  it('does not let the best card spend one cap twice', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await addCard('dc-1111', 'Citi Double Cash', 'Citi');
    await activate(FLEX);
    await dining('one', 'dc-1111', '2026-10-05', 1500);
    await dining('two', 'dc-1111', '2026-10-06', 1500);

    const o = await overview();
    expect(o.review).toMatchObject({ earned: '60.00', bestPossible: '150.00', missed: '90.00' });
  });

  describe('review comparison', () => {
    const RH = 'rh-9003';
    type Review = Awaited<ReturnType<typeof overview>>['review'];
    const expectConsistent = (r: Review) => {
      const sum = (values: string[]) => values.reduce((total, v) => total + Math.round(Number(v) * 100), 0);
      expect(sum(r.byCategory.map((c) => c.earned))).toBe(sum([r.earned]));
      expect(sum(r.byCategory.map((c) => c.bestPossible))).toBe(sum([r.bestPossible]));
      expect(sum(r.byCategory.map((c) => c.missed))).toBe(sum([r.missed]));
      expect(sum(r.topMisses.map((m) => m.missed))).toBe(sum([r.missed]));
      for (const c of r.byCategory) {
        expect(sum(r.topMisses.filter((m) => m.category === c.category).map((m) => m.missed))).toBe(sum([c.missed]));
      }
    };

    beforeEach(async () => {
      await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
      await addCard(RH, 'Robinhood Credit Card', 'Robinhood');
      await activate(FLEX);
    });

    it('never rates the comparison below how the cards were really used', async () => {
      await groceries('g', RH, '2026-10-05', 1500);
      await dining('d', FLEX, '2026-10-06', 1500);

      const { review } = await overview();
      expect(review).toMatchObject({ earned: '150.00', bestPossible: '150.00', missed: '0.00', topMisses: [] });
      expect(review.byCategory.map((c) => [c.category, c.earned, c.bestPossible, c.missed]).sort()).toEqual([
        ['dining', '105.00', '105.00', '0.00'],
        ['groceries', '45.00', '45.00', '0.00'],
      ]);
      expectConsistent(review);
    });

    it('spends free cap room on the purchase that gains the most, whatever came first', async () => {
      await groceries('g', RH, '2026-10-05', 1500);
      await dining('d', RH, '2026-10-06', 1500);

      const { review } = await overview();
      expect(review).toMatchObject({ earned: '90.00', bestPossible: '150.00', missed: '60.00' });
      expect(review.topMisses).toMatchObject([
        { transactionId: 'd', accountId: RH, bestAccountId: FLEX, earned: '45.00', bestPossible: '105.00', missed: '60.00' },
      ]);
      expectConsistent(review);
    });

    it('moves a purchase only into the room real purchases left, splitting at the cap', async () => {
      await dining('on-flex', FLEX, '2026-10-05', 1000);
      await dining('on-rh', RH, '2026-10-06', 1000);

      const { review } = await overview();
      expect(review).toMatchObject({ earned: '100.00', bestPossible: '120.00', missed: '20.00' });
      expect(review.topMisses).toMatchObject([{ transactionId: 'on-rh', earned: '30.00', bestPossible: '50.00' }]);
      expectConsistent(review);
    });

    it('estimates a lower bound, keeping a bonus a purchase really earned', async () => {
      await groceries('g', FLEX, '2026-10-05', 1500);
      await dining('d', RH, '2026-10-06', 1500);

      const { review } = await overview();
      expect(review).toMatchObject({ earned: '120.00', bestPossible: '120.00', missed: '0.00', topMisses: [] });
      expectConsistent(review);
    });

    it('compares from the cap really used before the window', async () => {
      await dining('before', FLEX, '2026-10-05', 1500);
      await dining('inside', RH, '2026-12-05', 100);

      const { review } = await rewardsOverview({ range: '1m', today: '2026-12-10' });
      expect(review).toMatchObject({ earned: '3.00', bestPossible: '3.00', missed: '0.00', topMisses: [] });
      expectConsistent(review);
    });

    it('leaves purchases before the window on their cards, so their cap room stays free', async () => {
      await dining('before', RH, '2026-10-05', 1500);
      await dining('inside', RH, '2026-12-05', 100);

      const { review } = await rewardsOverview({ range: '1m', today: '2026-12-10' });
      expect(review).toMatchObject({ earned: '3.00', bestPossible: '7.00', missed: '4.00' });
      expect(review.topMisses).toMatchObject([{ transactionId: 'inside', bestAccountId: FLEX }]);
      expectConsistent(review);
    });

    it('gives each quarter its own cap room', async () => {
      await putRewardCard(FLEX, { rotatingCategories: ['dining'] }, { today: '2026-09-01' });
      await dining('q3', RH, '2026-09-20', 1500);
      await dining('q4', RH, '2026-10-05', 1500);

      const { review } = await rewardsOverview({ range: '3m', today: '2026-12-10' });
      expect(review).toMatchObject({ earned: '90.00', bestPossible: '210.00', missed: '120.00' });
      expectConsistent(review);
    });
  });

  it('applies the quarter by purchase date and starts a new cap each quarter', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await activate(FLEX, '2026-Q4', '2026-12-10');
    await activate(FLEX, '2027-Q1', '2026-12-10');
    await dining('sep-30', FLEX, '2026-09-30', 100);
    await dining('oct-1', FLEX, '2026-10-01', 100);
    await groceries('dec', FLEX, '2026-12-30', 1400);
    await dining('dec-31', FLEX, '2026-12-31', 100);
    await dining('jan-1', FLEX, '2027-01-01', 100);
    await groceries('jan-2', FLEX, '2027-01-02', 100);

    const o = await rewardsOverview({ range: '1y', today: '2027-01-10' });
    // Sept $3, Oct $7, Dec $70 fills the cap, Dec 31 $3. Q1 lists groceries, not dining: $3 + $5.
    expect(o.review.earned).toBe('91.00');
    expect(o.review.byCategory.find((c) => c.category === 'groceries')).toMatchObject({ earned: '75.00' });
    expect(o.cards[0].capUsage).toMatchObject({ used: '100.00', remaining: '1400.00' });
  });

  it('counts no bonus for an issuer list the user has not confirmed, and says so', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await dining('dinner', FLEX, '2026-10-10', 100);
    await groceries('market', FLEX, '2026-10-11', 100);

    const o = await overview();
    expect(o.cards[0]).toMatchObject({
      rotatingCategories: ['groceries', 'dining'],
      rotatingStatus: 'unconfirmed',
      rotatingRates: [{ category: 'groceries', rate: '5' }, { category: 'dining', rate: '7' }],
      rotatingActivated: false,
      capUsage: null,
      earned: '4.00',
    });
    expect(o.cards[0].bonuses).toEqual([{ category: 'dining', rate: '3' }, { category: 'drugstores', rate: '3' }]);
    expect(o.categories.find((c) => c.category === 'groceries')!.capWarning).toBeNull();
  });

  it('counts no bonus once the user cleared the list, and the bonus for one they ticked', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await dining('dinner', FLEX, '2026-10-10', 100);

    await putRewardCard(FLEX, { rotatingCategories: [] }, { today: TODAY });
    let o = await overview();
    expect(o.cards[0]).toMatchObject({ rotatingStatus: 'notActivated', earned: '3.00' });

    await putRewardCard(FLEX, { rotatingCategories: ['dining'] }, { today: TODAY });
    o = await overview();
    expect(o.cards[0]).toMatchObject({ rotatingStatus: 'activated', earned: '7.00' });
  });

  it('leaves a quarter with no list on file at the regular rates', async () => {
    await addCard(FLEX, 'Chase Freedom Flex', 'Chase');
    await dining('dinner', FLEX, '2026-07-10', 100);

    const o = await rewardsOverview({ today: '2026-08-01' });
    expect(o.cards[0]).toMatchObject({ rotatingStatus: null, rotatingRates: [], earned: '3.00' });
  });

  it('applies Customized Cash\'s shared cap the same way', async () => {
    await addCard('ccr-9004', 'Customized Cash Rewards Visa Signature', 'Bank of America');
    await groceries('market', 'ccr-9004', '2026-10-05', 2600);
    // $2,500 at 2% and $100 at 1%.
    expect((await overview()).cards[0]).toMatchObject({ earned: '51.00', capUsage: { remaining: '0.00' } });
  });

  it('recognizes Chase Freedom without catching other cards', async () => {
    await addCard('a-1111', 'Chase Freedom Rise', 'Chase');
    await addCard('b-2222', 'Freedom Visa', 'Some Credit Union');
    await addCard('c-3333', 'Freedom Flex', 'Chase');
    await addCard('d-4444', 'Freedom', 'Chase');
    const ids = Object.fromEntries((await overview()).cards.map((c) => [c.accountId, c.productId]));
    expect(ids).toEqual({ 'a-1111': null, 'b-2222': null, 'c-3333': 'chase-freedom-flex', 'd-4444': 'chase-freedom' });
  });

  it('leaves superstores and warehouse clubs out of groceries', () => {
    expect(rewardCategoryFor('FOOD_AND_DRINK', 'FOOD_AND_DRINK_GROCERIES', 'Walmart Supercenter')).toBe('other');
    expect(rewardCategoryFor('FOOD_AND_DRINK', null, 'Target')).toBe('other');
    expect(rewardCategoryFor('FOOD_AND_DRINK', 'FOOD_AND_DRINK_GROCERIES', 'Kroger')).toBe('groceries');
  });
});

describe('reward writes', () => {
  it('validates card settings', async () => {
    await addCard('csp-9001', 'Chase Sapphire Preferred', 'Chase');
    await addCard('chk-1', 'Checking', 'Chase', 'depository');

    await expect(putRewardCard('csp-9001', { productId: 'nope' })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(putRewardCard('csp-9001', { choiceCategory: 'gas' })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(putRewardCard('csp-9001', { rotatingCategories: ['dining'] })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(putRewardCard('chk-1', { productId: 'flat-2' })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(putRewardCard('missing', { productId: 'flat-2' })).rejects.toMatchObject({ code: 'NOT_FOUND' });
  });

  it('validates activation marks', async () => {
    await addCard('csp-9001', 'Chase Sapphire Preferred', 'Chase');
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    const mark = (accountId: string, activation: { quarter?: string; activated?: boolean }) =>
      putRewardCard(accountId, { activation }, { today: TODAY });

    await expect(mark('csp-9001', { quarter: '2026-Q4', activated: true })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(mark('flex-9002', { quarter: '2026-Q3', activated: true })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(mark('flex-9002', { quarter: '2027-Q2', activated: true })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(mark('flex-9002', { quarter: '2026-Q4' })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
  });

  it('keeps ended quarters for the review, then forgets them, and a changed product\'s activation marks', async () => {
    await addCard('flex-9002', 'Chase Freedom Flex', 'Chase');
    const stored = async () => {
      const res = await db.execute("SELECT value FROM settings WHERE key = 'rewards_cards'");
      return JSON.parse(String(res.rows[0]?.value ?? '{}'))['flex-9002'];
    };
    await putRewardCard('flex-9002', { activation: { quarter: '2026-Q4', activated: true } }, { today: '2026-12-10' });
    await putRewardCard('flex-9002', { activation: { quarter: '2027-Q1', activated: true } }, { today: '2026-12-10' });
    expect(await stored()).toEqual({ activated: ['2026-Q4', '2027-Q1'] });

    await putRewardCard('flex-9002', { activation: { quarter: '2027-Q2', activated: true } }, { today: '2027-01-05' });
    expect(await stored()).toEqual({ activated: ['2026-Q4', '2027-Q1', '2027-Q2'] });

    await putRewardCard('flex-9002', { activation: { quarter: '2028-Q1', activated: true } }, { today: '2028-01-05' });
    expect(await stored()).toEqual({ activated: ['2027-Q1', '2027-Q2', '2028-Q1'] });

    await putRewardCard('flex-9002', { productId: 'chase-freedom-unlimited' }, { today: '2027-01-05' });
    expect(await stored()).toEqual({ productId: 'chase-freedom-unlimited' });
  });

  it('validates point values', async () => {
    await expect(putRewardPreferences({ pointValues: { cash: '2.00' } })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(putRewardPreferences({ pointValues: { 'chase-ur': '4.00' } })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    await expect(putRewardPreferences({ pointValues: { 'chase-ur': 'abc' } })).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
  });

  it('clears a chosen product back to detection', async () => {
    await addCard('csp-9001', 'Chase Sapphire Preferred', 'Chase');
    await putRewardCard('csp-9001', { productId: 'flat-2' });
    expect((await overview()).cards[0]).toMatchObject({ productId: 'flat-2', productSource: 'chosen' });
    await putRewardCard('csp-9001', { productId: null });
    expect((await overview()).cards[0]).toMatchObject({ productId: 'chase-sapphire-preferred', productSource: 'detected' });
  });
});
