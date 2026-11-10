import { describe, it, expect, beforeEach, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});

import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema } from '../../native-api/src/services/schema.js';
import {
  classifyLivingKind, livingOverview, patchTransaction, putLivingPreferences,
  createObligation, putRouteAlternative, livingMerchants, putMerchantDefault,
} from '../../native-api/src/services/living.js';

async function clean() {
  for (const t of [
    'transactions', 'accounts', 'living_obligations',
    'living_merchant_defaults', 'living_route_alternatives',
  ]) {
    await db.execute(`DELETE FROM ${t}`).catch(() => {});
  }
  await db.execute("DELETE FROM settings WHERE key LIKE 'living_%'").catch(() => {});
}

async function addAccount(id = 'chk') {
  await db.execute({
    sql: `INSERT INTO accounts (id, name, type, source, excluded, archived_at)
          VALUES (?, 'Checking', 'depository', 'plaid', 0, NULL)`,
    args: [id],
  });
}

async function addTxn(
  id: string,
  date: string,
  name: string,
  amount: number,
  over: {
    merchant?: string | null;
    category?: string;
    party?: number | null;
    origin?: string | null;
    dest?: string | null;
    kind?: string | null;
  } = {},
) {
  await db.execute({
    sql: `INSERT INTO transactions
            (id, account_id, date, name, merchant_name, amount, category,
             pending, ignored, party_size, trip_origin, trip_destination, trip_kind)
          VALUES (?, 'chk', ?, ?, ?, ?, ?, 0, 0, ?, ?, ?, ?)`,
    args: [
      id, date, name, over.merchant ?? name, amount, over.category ?? 'OTHER',
      over.party ?? null, over.origin ?? null, over.dest ?? null, over.kind ?? null,
    ],
  });
}

beforeEach(async () => {
  await ensureNativeSchema();
  await clean();
  await addAccount();
});

describe('classifyLivingKind', () => {
  it('splits grocery, dining, rideshare, and Uber Eats', () => {
    expect(classifyLivingKind('FOOD_AND_DRINK', 'WHOLE FOODS MARKET', 'Whole Foods', 60)).toBe('grocery');
    expect(classifyLivingKind('FOOD_AND_DRINK', 'CHIPOTLE', 'Chipotle', 14)).toBe('dining');
    expect(classifyLivingKind('TRANSPORTATION', 'UBER TRIP', 'Uber', 15)).toBe('rideshare');
    expect(classifyLivingKind('FOOD_AND_DRINK', 'UBER EATS', 'Uber Eats', 22)).toBe('dining');
    expect(classifyLivingKind('INCOME', 'PAYROLL', 'University Payroll', -3200)).toBe('income');
    expect(classifyLivingKind('RENT_AND_UTILITIES', 'CAMPUS HEIGHTS RENT', 'Campus Heights', 1450)).toBe('rent');
    expect(classifyLivingKind('TRANSFER_OUT', 'TO SAVINGS', null, 500)).toBe('transfer');
    expect(classifyLivingKind('Transfer', 'Robinhood', 'Robinhood', 955)).toBe('transfer');
    expect(classifyLivingKind('Loan Payment', 'CHASE CREDIT CRD AUTOPAY', null, 24)).toBe('transfer');
    expect(classifyLivingKind('Income', 'AUTOMATIC PAYMENT - THANK', null, -304)).toBe('transfer');
    // A landlord the wording does not give away stays unlabelled until the user labels it.
    expect(classifyLivingKind('Services', 'OAK STREET HOLDINGS', 'Oak Street', 2300)).toBe('other');
    expect(classifyLivingKind('Bills & Utilities', 'FI AB12CD', 'Google Fi', 45)).toBe('phone');
    expect(classifyLivingKind('RENT_AND_UTILITIES', 'FI EFGHJK', 'Google Fi', 35)).toBe('phone');
    expect(classifyLivingKind('Income', 'State University EDI PYMNTS', null, -2500)).toBe('income');
    expect(classifyLivingKind('Entertainment', 'YouTubePremium', 'YouTube Premium', 8.99)).toBe('subscription');
  });
});

describe('livingOverview arithmetic', () => {
  it('computes meal costs and the cook-more savings from the example numbers', async () => {
    // 31-day August, 20 dining meals / $340, 60 cooked implied over a longer
    // window — here we use August: 20 dining transactions of $17 and groceries
    // $260, today = 31 so cooked meals = 31 - 20 = 11. Use party_size so
    // dining meals stay 20 with fewer visits, matching $17/meal.
    for (let i = 0; i < 10; i++) {
      await addTxn(`d${i}`, `2026-08-${String(2 + i * 2).padStart(2, '0')}`, 'CHIPOTLE', 34, {
        category: 'FOOD_AND_DRINK', merchant: 'Chipotle', party: 2,
      });
    }
    await addTxn('g1', '2026-08-03', 'WHOLE FOODS MARKET', 130, { category: 'FOOD_AND_DRINK', merchant: 'Whole Foods' });
    await addTxn('g2', '2026-08-18', 'WHOLE FOODS MARKET', 130, { category: 'FOOD_AND_DRINK', merchant: 'Whole Foods' });
    await addTxn('inc', '2026-08-01', 'STIPEND', -2000, { category: 'INCOME', merchant: 'University Payroll' });

    const overview = await livingOverview({ today: '2026-08-31' });
    expect(overview.meals.diningMeals).toBe(20);
    expect(overview.meals.diningSpend).toBe('340.00');
    expect(overview.meals.diningCostPerMeal).toBe('17.00');
    expect(overview.meals.grocerySpend).toBe('260.00');
    expect(overview.meals.cookedMeals).toBe(21); // 31 days - 10 dining transactions
    // cooking $260 / 21 ≈ $12.38; 2 extra dinners/week * 31/7 ≈ 9 meals
    // save 9 * (17.00 - 12.38) ≈ 41.58 — not the 60-cooked-meal example.
    expect(Number(overview.meals.cookingCostPerMeal)).toBeCloseTo(12.38, 1);
    const cook = overview.tradeoffs.find((t) => t.kind === 'cookMore');
    expect(cook).toBeTruthy();
    expect(cook?.diningCostPerMeal).toBe('17.00');
  });

  it('slider leftover rises when dining is more expensive than cooking and meals drop', async () => {
    for (let i = 0; i < 8; i++) {
      await addTxn(`d${i}`, `2026-08-${String(2 + i * 3).padStart(2, '0')}`, 'CHIPOTLE', 20, {
        category: 'FOOD_AND_DRINK', merchant: 'Chipotle',
      });
    }
    await addTxn('g1', '2026-08-05', 'WHOLE FOODS MARKET', 80, { category: 'FOOD_AND_DRINK', merchant: 'Whole Foods' });
    const overview = await livingOverview({ today: '2026-08-31' });
    expect(overview.diningOutcomes).toHaveLength(15);
    const zero = overview.diningOutcomes[0]!;
    const max = overview.diningOutcomes[14]!;
    expect(Number(zero.leftover)).toBeGreaterThan(Number(max.leftover));
  });

  it('does not extrapolate fixed rent, and does extrapolate flexible dining', async () => {
    await addTxn('rent', '2026-06-01', 'CAMPUS HEIGHTS RENT', 1450, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('rent2', '2026-07-01', 'CAMPUS HEIGHTS RENT', 1450, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('rent3', '2026-08-01', 'CAMPUS HEIGHTS RENT', 1450, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('inc', '2026-08-01', 'STIPEND', -3200, { category: 'INCOME', merchant: 'University Payroll' });
    await addTxn('d1', '2026-08-02', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    await addTxn('d2', '2026-08-04', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    await addTxn('d3', '2026-08-06', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    await addTxn('d4', '2026-08-08', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    await addTxn('d5', '2026-08-10', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    await addTxn('d6', '2026-08-12', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    await addTxn('d7', '2026-08-14', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });

    const mid = await livingOverview({ today: '2026-08-15' });
    expect(mid.split.fixedPosted).toBe('1450.00');
    expect(mid.split.fixedRemaining).toBe('0.00'); // already posted this month
    expect(mid.split.fixedProjected).toBe('1450.00');
    expect(mid.split.flexiblePosted).toBe('140.00');
    // 140 / 15 * 31 = 289.333 → 289.33
    expect(mid.split.flexibleProjected).toBe('289.33');
    expect(mid.income.projected).toBe('3200.00');
    expect(mid.leftover.projected).toBe('1460.67'); // 3200 - 1450 - 289.33
  });

  it('projects remaining rent when it has not posted yet this month', async () => {
    await addTxn('rent', '2026-06-01', 'CAMPUS HEIGHTS RENT', 1450, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('rent2', '2026-07-01', 'CAMPUS HEIGHTS RENT', 1450, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('inc', '2026-08-01', 'STIPEND', -3200, { category: 'INCOME', merchant: 'University Payroll' });

    const mid = await livingOverview({ today: '2026-08-15' });
    expect(mid.split.fixedPosted).toBe('0.00');
    expect(mid.split.fixedRemaining).toBe('1450.00');
    expect(mid.split.fixedProjected).toBe('1450.00');
  });

  it('clusters tagged Uber routes and applies a transit alternative', async () => {
    for (const [i, day] of ['03', '06', '10', '13', '17', '20'].entries()) {
      await addTxn(`u${i}`, `2026-08-${day}`, 'UBER TRIP', 14, {
        category: 'TRANSPORTATION', merchant: 'Uber', origin: 'Campus', dest: 'Home', kind: 'campus',
      });
    }
    await addTxn('air', '2026-08-19', 'UBER TRIP ATL', 47, {
      category: 'TRANSPORTATION', merchant: 'Uber', origin: 'Campus', dest: 'Airport', kind: 'airport',
    });
    await putRouteAlternative({ origin: 'Campus', destination: 'Home', label: 'Transit pass', monthlyAmount: '30.00' });

    const overview = await livingOverview({ today: '2026-08-31' });
    expect(overview.routes[0]?.origin).toBe('Campus');
    expect(overview.routes[0]?.destination).toBe('Home');
    expect(overview.routes[0]?.rideCount).toBe(6);
    expect(overview.routes[0]?.monthlyCost).toBe('84.00');
    expect(overview.routes[0]?.alternativeLabel).toBe('Transit pass');
    expect(overview.routes[0]?.alternativeMonthlyCost).toBe('30.00');
    const ride = overview.tradeoffs.find((t) => t.kind === 'repeatedRide');
    expect(ride?.monthlyImpact).toBe('54.00'); // 84 - 30
    expect(ride?.alternativeLabel).toBe('Transit pass');
  });

  it('flags a flexible category that drifted from its three-month baseline', async () => {
    for (const [month, amount] of [['05', 80], ['06', 80], ['07', 80], ['08', 200]] as const) {
      await addTxn(`s${month}`, `2026-${month}-10`, 'NIKE STORE', amount, {
        category: 'GENERAL_MERCHANDISE', merchant: 'Nike',
      });
    }
    const overview = await livingOverview({ today: '2026-08-31' });
    const shopping = overview.categoryTrends.find((c) => c.category === 'GENERAL_MERCHANDISE');
    expect(shopping?.drifted).toBe(true);
    expect(overview.tradeoffs.some((t) => t.kind === 'categoryDrift')).toBe(true);
  });

  it('says so when fixed costs eat most of income', async () => {
    await addTxn('rent', '2026-06-01', 'CAMPUS HEIGHTS RENT', 1800, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('rent2', '2026-07-01', 'CAMPUS HEIGHTS RENT', 1800, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('rent3', '2026-08-01', 'CAMPUS HEIGHTS RENT', 1800, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('inc', '2026-08-01', 'STIPEND', -3000, { category: 'INCOME', merchant: 'University Payroll' });
    await addTxn('d1', '2026-08-10', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    const overview = await livingOverview({ today: '2026-08-31' });
    expect(Number(overview.split.fixedShareOfIncome)).toBeGreaterThanOrEqual(0.5);
    expect(overview.tradeoffs.some((t) => t.kind === 'fixedShare')).toBe(true);
  });

  it('remembers party size on the next Chipotle charge after one tap', async () => {
    await addTxn('d1', '2026-08-02', 'CHIPOTLE', 40, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    await patchTransaction('d1', { partySize: 4 });
    await addTxn('d2', '2026-08-16', 'CHIPOTLE', 40, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    const overview = await livingOverview({ today: '2026-08-31' });
    const second = overview.transactions.find((t) => t.id === 'd2');
    expect(second?.partySize).toBeNull();
    expect(second?.effectivePartySize).toBe(4);
    expect(overview.meals.diningMeals).toBe(8);
  });

  it('includes a manual obligation that has not posted yet', async () => {
    await addTxn('inc', '2026-08-01', 'STIPEND', -3000, { category: 'INCOME', merchant: 'University Payroll' });
    await createObligation({ name: 'Phone', amount: '45.00', interval: 'monthly', kind: 'phone' });
    const overview = await livingOverview({ today: '2026-08-15' });
    expect(overview.obligations).toHaveLength(1);
    expect(overview.split.fixedRemaining).toBe('45.00');
    expect(overview.split.fixedProjected).toBe('45.00');
  });

  it('translates cook-more savings into a named contribution goal', async () => {
    for (let i = 0; i < 10; i++) {
      await addTxn(`d${i}`, `2026-08-${String(2 + i * 2).padStart(2, '0')}`, 'CHIPOTLE', 34, {
        category: 'FOOD_AND_DRINK', merchant: 'Chipotle', party: 2,
      });
    }
    await addTxn('g1', '2026-08-03', 'WHOLE FOODS MARKET', 80, { category: 'FOOD_AND_DRINK', merchant: 'Whole Foods' });
    await addTxn('inc', '2026-08-01', 'STIPEND', -2000, { category: 'INCOME', merchant: 'University Payroll' });
    await putLivingPreferences({
      goalKind: 'contribution',
      goalLabel: 'Index fund plan',
      goalMonthlyContribution: '500.00',
    });
    const overview = await livingOverview({ today: '2026-08-31' });
    const cook = overview.tradeoffs.find((t) => t.kind === 'cookMore');
    expect(cook?.goalLabel).toBe('Index fund plan');
    expect(cook?.goalYearly).toBeTruthy();
  });

  it('reviews the prior month when the current month has no charges yet', async () => {
    await addTxn('rent', '2026-07-16', 'CAMPUS HEIGHTS RENT', 1450, { category: 'RENT_AND_UTILITIES', merchant: 'Campus Heights' });
    await addTxn('inc', '2026-07-31', 'State University EDI PYMNTS', -2500, { category: 'Income' });
    await addTxn('dine', '2026-08-26', 'Panda Express', 12.5, { category: 'FOOD_AND_DRINK', merchant: 'Panda Express' });
    await addTxn('groc', '2026-08-26', 'Publix', 80.25, { category: 'FOOD_AND_DRINK', merchant: 'Publix' });

    const overview = await livingOverview({ today: '2026-09-01' });
    expect(overview.hasHistory).toBe(true);
    expect(overview.period.usingPriorMonth).toBe(true);
    expect(overview.period.reviewFrom).toBe('2026-08-01');
    expect(overview.transactions.some((t) => t.id === 'dine')).toBe(true);
    expect(overview.transactions.some((t) => t.livingKind === 'rent')).toBe(false); // rent is July, review is August
    const rent = overview.detected.find((d) => d.kind === 'rent');
    expect(rent?.name).toBe('Campus Heights');
    expect(rent?.postedThisMonth).toBe(false);
    // Single observations with no confirmed override are unknown (N/A): they
    // are listed for review but excluded from projections.
    expect(rent?.monthlyAmount).toBeNull();
    expect(rent?.confidence).toBe('single');
    expect(overview.split.fixedRemaining).toBe('0.00');
    expect(overview.income.expectedRemaining).toBe('0.00');
    expect(overview.leftover.projected).toBe('0.00');
    expect(overview.meals.diningSpend).toBe('12.50');
    expect(overview.meals.grocerySpend).toBe('80.25');
  });

  it('labels a charge as rent and remembers the merchant', async () => {
    await addTxn('x1', '2026-08-10', 'SOME LANDLORD LLC', 1800, { category: 'Services', merchant: 'Some Landlord' });
    expect((await livingOverview({ today: '2026-08-31' })).transactions[0]?.livingKind).toBe('other');
    await patchTransaction('x1', { livingKind: 'rent' });
    await addTxn('x2', '2026-08-20', 'SOME LANDLORD LLC', 1800, { category: 'Services', merchant: 'Some Landlord' });
    const overview = await livingOverview({ today: '2026-08-31' });
    expect(overview.transactions.find((t) => t.id === 'x2')?.livingKind).toBe('rent');
    expect(overview.split.fixedPosted).toBe('3600.00');
  });

  it('groups deposits whose bank reference id changes every payment', async () => {
    // Plaid names carry per-payment ACH ids ("PPD ID: …"); without stripping
    // them the stipend splits into four single-use merchants and never series.
    await addTxn('s1', '2026-05-29', 'State University EDI PYMNTS PPD ID: 1000000001', -2000, { category: 'INCOME' });
    await addTxn('s2', '2026-06-30', 'State University EDI PYMNTS PPD ID: 1000000002', -2500, { category: 'INCOME' });
    await addTxn('s3', '2026-07-31', 'State University EDI PYMNTS PPD ID: 1000000003', -2550, { category: 'INCOME' });
    await addTxn('s4', '2026-08-31', 'State University EDI PYMNTS PPD ID: 1000000004', -2900, { category: 'INCOME' });
    const overview = await livingOverview({ today: '2026-09-01' });
    const stipend = overview.detected.find((d) => d.kind === 'income');
    expect(stipend?.confidence).toBe('series');
    // Median of the four deposits.
    expect(stipend?.monthlyAmount).toBe('2525.00');
    // September has no deposit yet; the series amount is still expected.
    expect(overview.income.expectedRemaining).toBe('2525.00');
  });

  it('merges legacy title-case categories into the canonical ones', async () => {
    await addTxn('c1', '2026-08-10', 'CLAUDE.AI', 80, { category: 'Services' });
    await addTxn('c2', '2026-08-12', 'OPENAI', 190, { category: 'GENERAL_SERVICES' });
    const overview = await livingOverview({ today: '2026-08-31' });
    const services = overview.categoryTrends.filter((t) => t.category === 'GENERAL_SERVICES');
    expect(services).toHaveLength(1);
    expect(services[0]?.currentMonth).toBe('270.00');
    expect(overview.categoryTrends.some((t) => t.category === 'Services')).toBe(false);
  });

  it('ignores transfers when computing leftover', async () => {
    await addTxn('inc', '2026-08-01', 'STIPEND', -2000, { category: 'INCOME', merchant: 'University Payroll' });
    await addTxn('tr', '2026-08-02', 'TO SAVINGS', 500, { category: 'TRANSFER_OUT' });
    await addTxn('d1', '2026-08-03', 'CHIPOTLE', 20, { category: 'FOOD_AND_DRINK', merchant: 'Chipotle' });
    const overview = await livingOverview({ today: '2026-08-31' });
    expect(overview.split.flexiblePosted).toBe('20.00');
    expect(overview.income.posted).toBe('2000.00');
  });
});

describe('merchant labels', () => {
  const landlord = async (today = '2026-08-31') =>
    (await livingMerchants({ today })).merchants.find((m) => m.key === 'oak street');

  it('lists a landlord the wording does not give away, and counts it as rent once labelled', async () => {
    // Uneven amounts: not a detected series, so only the full merchant list has it.
    await addTxn('l1', '2026-07-03', 'OAK STREET HOLDINGS', 2300, { category: 'GENERAL_SERVICES', merchant: 'Oak Street' });
    await addTxn('l2', '2026-08-04', 'OAK STREET HOLDINGS', 1400, { category: 'GENERAL_SERVICES', merchant: 'Oak Street' });
    await addTxn('pay', '2026-08-29', 'ACME CORP PAYROLL', -3000, { category: 'INCOME' });
    expect(await landlord()).toEqual({
      key: 'oak street', name: 'Oak Street', kind: 'other', automaticKind: 'other', labelledKind: null,
      monthlyAmount: null, lastDate: '2026-08-04', lastAmount: '1400.00', group: 'other',
    });
    expect((await livingOverview({ today: '2026-08-31' })).split.fixedPosted).toBe('0.00');

    await putMerchantDefault({ merchantKey: ' Oak  Street ', kind: 'rent' });
    const { merchants } = await livingMerchants({ today: '2026-08-31' });
    expect(merchants.map((m) => [m.key, m.kind, m.group])).toEqual([
      ['oak street', 'rent', 'labelled'],
      ['acme corp payroll', 'income', 'recurring'],
    ]);
    expect(merchants[0]).toMatchObject({ automaticKind: 'other', labelledKind: 'rent' });
    expect(merchants[1]?.lastAmount).toBe('-3000.00');
    expect((await livingOverview({ today: '2026-08-31' })).split.fixedPosted).toBe('1400.00');
  });

  it('counts a transfer-coded deposit as income once its sender is labelled', async () => {
    await addTxn('z1', '2026-08-15', 'ZELLE FROM ACME LLC', -2500, { category: 'TRANSFER_IN' });
    expect((await livingOverview({ today: '2026-08-31' })).income.posted).toBe('0.00');
    await putMerchantDefault({ merchantKey: 'zelle from acme llc', kind: 'income' });
    expect((await livingOverview({ today: '2026-08-31' })).income.posted).toBe('2500.00');
  });

  it('keeps the label and the monthly amount independent, and null returns each to automatic', async () => {
    await addTxn('l1', '2026-08-04', 'OAK STREET HOLDINGS', 2300, { category: 'GENERAL_SERVICES', merchant: 'Oak Street' });
    await putMerchantDefault({ merchantKey: 'oak street', kind: 'rent' });
    await putMerchantDefault({ merchantKey: 'oak street', monthlyAmount: '1800' });
    expect(await landlord()).toMatchObject({ kind: 'rent', labelledKind: 'rent', monthlyAmount: '1800.00', group: 'labelled' });

    await putMerchantDefault({ merchantKey: 'oak street', kind: null });
    expect(await landlord()).toMatchObject({ kind: 'other', labelledKind: null, monthlyAmount: '1800.00', group: 'labelled' });
    await putMerchantDefault({ merchantKey: 'oak street', monthlyAmount: null });
    expect(await landlord()).toMatchObject({ labelledKind: null, monthlyAmount: null, group: 'other' });
  });

  it('keeps a label listed after its charges have left the window', async () => {
    await putMerchantDefault({ merchantKey: 'old landlord', kind: 'rent', monthlyAmount: '1500' });
    expect((await livingMerchants({ today: '2026-08-31' })).merchants).toEqual([{
      key: 'old landlord', name: 'old landlord', kind: 'rent', automaticKind: 'other', labelledKind: 'rent',
      monthlyAmount: '1500.00', lastDate: null, lastAmount: null, group: 'labelled',
    }]);
  });

  it('rejects an empty update, an unknown kind, and an amount that is not positive', async () => {
    for (const input of [
      { merchantKey: 'x' },
      { merchantKey: 'x', kind: 'mortgage' },
      { merchantKey: 'x', monthlyAmount: '0' },
      { merchantKey: '  ', kind: 'rent' },
    ]) {
      await expect(putMerchantDefault(input)).rejects.toMatchObject({ code: 'VALIDATION_ERROR' });
    }
  });
});

describe('typical month, pace, and runway', () => {
  it('averages the last full months, skips a partial first month, and sizes cash runway', async () => {
    // History starts on May 20, so May is a partial month and must not drag the average down.
    await addTxn('m0', '2026-05-20', 'WHOLE FOODS MARKET', 40, { category: 'FOOD_AND_DRINK' });
    for (const [i, month] of ['2026-06', '2026-07', '2026-08'].entries()) {
      await addTxn(`inc${i}`, `${month}-01`, 'UNIVERSITY PAYROLL', -3000, { category: 'INCOME' });
      await addTxn(`rent${i}`, `${month}-03`, 'CAMPUS HEIGHTS RENT', 1500, { category: 'RENT_AND_UTILITIES' });
      await addTxn(`groc${i}`, `${month}-10`, 'WHOLE FOODS MARKET', 200, { category: 'FOOD_AND_DRINK' });
      await addTxn(`xfer${i}`, `${month}-12`, 'TO SAVINGS', 500, { category: 'TRANSFER_OUT' });
    }
    await db.execute({
      sql: 'INSERT INTO balance_history (account_id, balance, date) VALUES (?, ?, ?)',
      args: ['chk', 8500, '2026-09-02'],
    });

    const overview = await livingOverview({ today: '2026-09-02' });
    expect(overview.typical.months).toEqual(['2026-06', '2026-07', '2026-08']);
    expect(overview.typical.income).toBe('3000.00');
    expect(overview.typical.fixed).toBe('1500.00');
    expect(overview.typical.flexible).toBe('200.00');
    expect(overview.typical.spend).toBe('1700.00');
    expect(overview.typical.leftover).toBe('1300.00');
    expect(overview.typical.categories.map((c) => [c.category, c.monthlyAverage, c.flexibility])).toEqual([
      ['RENT_AND_UTILITIES', '1500.00', 'fixed'],
      ['FOOD_AND_DRINK', '200.00', 'flexible'],
    ]);
    expect(overview.runway.liquidCash).toBe('8500.00');
    expect(overview.runway.months).toBe('5.0');
    expect(overview.thisMonth.spent).toBe('0.00');
    // Day 2 is too early to score day-to-day pace.
    expect(overview.thisMonth.flexiblePace).toBeNull();
    expect(overview.thisMonth.projectedLeftoverIfTypical).toBe('1300.00');
  });

  it('scores pace once a few days are in and keeps a lone partial month as the only yardstick', async () => {
    await addTxn('g1', '2026-08-20', 'WHOLE FOODS MARKET', 300, { category: 'FOOD_AND_DRINK' });
    await addTxn('g2', '2026-09-02', 'WHOLE FOODS MARKET', 150, { category: 'FOOD_AND_DRINK' });

    const overview = await livingOverview({ today: '2026-09-10' });
    expect(overview.typical.months).toEqual(['2026-08']);
    expect(overview.typical.flexible).toBe('300.00');
    // 10/30 of a $300 month is $100 expected by now; $150 posted is 1.5x.
    expect(overview.thisMonth.flexibleExpectedByNow).toBe('100.00');
    expect(overview.thisMonth.flexiblePace).toBe('1.5000');
    expect(overview.runway.months).toBeNull;
  });
});
