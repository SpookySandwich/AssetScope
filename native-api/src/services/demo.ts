import { db } from '../core/database.js';

// Demo Mode seed (SPEC §15.6, UC-001). Net-worth-focused: a realistic account
// mix with ~18 months of daily balance history so every chart, change label,
// carry-forward path, and freshness state has something truthful to show.
// Values follow the same conventions core/sync.ts writes from Plaid:
// credit/loan balances are positive amounts owed.
//
// Deterministic PRNG so the demo looks the same on every machine.

function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const isoDate = (d: Date): string => d.toISOString().slice(0, 10);

function daysAgo(n: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - n);
  return isoDate(d);
}

export async function isDemoSeeded(): Promise<boolean> {
  const res = await db.execute("SELECT COUNT(*) AS c FROM accounts WHERE id LIKE 'demo-%'");
  return Number((res.rows[0] as unknown as { c: number }).c) > 0;
}

export async function seedDemoData(): Promise<void> {
  if (await isDemoSeeded()) {
    await seedDemoInvestments();
    await seedDemoLiving();
    return;
  }

  const now = new Date().toISOString();
  const rand = mulberry32(20260812);

  // Two fake Plaid items so item-level freshness/attention states render.
  await db.batch([
    {
      sql: `INSERT OR IGNORE INTO plaid_items (item_id, access_token, institution_name, last_synced_at, status, last_success_at)
            VALUES (?, ?, ?, ?, 'active', ?)`,
      args: ['demo-item-fnb', 'demo:not-a-real-token', 'First National Bank', Date.now() - 8 * 60_000, now],
    },
    {
      sql: `INSERT OR IGNORE INTO plaid_items (item_id, access_token, institution_name, last_synced_at, status, last_success_at)
            VALUES (?, ?, ?, ?, 'active', ?)`,
      args: ['demo-item-broker', 'demo:not-a-real-token', 'Meridian Invest', Date.now() - 32 * 60_000, now],
    },
  ], 'write');

  type DemoAccount = {
    id: string; name: string; type: string; subtype: string | null;
    institution: string | null; mask: string | null; itemId: string | null;
    source: 'plaid' | 'manual'; accountClass: 'asset' | 'liability';
    manualCategory: string | null;
  };

  const accounts: DemoAccount[] = [
    { id: 'demo-checking', name: 'Everyday Checking', type: 'depository', subtype: 'checking', institution: 'First National Bank', mask: '4242', itemId: 'demo-item-fnb', source: 'plaid', accountClass: 'asset', manualCategory: null },
    { id: 'demo-savings', name: 'High-Yield Savings', type: 'depository', subtype: 'savings', institution: 'First National Bank', mask: '8888', itemId: 'demo-item-fnb', source: 'plaid', accountClass: 'asset', manualCategory: null },
    { id: 'demo-brokerage', name: 'Brokerage', type: 'investment', subtype: 'brokerage', institution: 'Meridian Invest', mask: '5678', itemId: 'demo-item-broker', source: 'plaid', accountClass: 'asset', manualCategory: null },
    { id: 'demo-401k', name: '401(k)', type: 'investment', subtype: '401k', institution: 'Meridian Invest', mask: '9001', itemId: 'demo-item-broker', source: 'plaid', accountClass: 'asset', manualCategory: null },
    { id: 'demo-credit', name: 'Rewards Visa', type: 'credit', subtype: 'credit card', institution: 'First National Bank', mask: '1234', itemId: 'demo-item-fnb', source: 'plaid', accountClass: 'liability', manualCategory: null },
    { id: 'demo-autoloan', name: 'Auto Loan', type: 'loan', subtype: 'auto', institution: 'First National Bank', mask: '7710', itemId: 'demo-item-fnb', source: 'plaid', accountClass: 'liability', manualCategory: null },
    { id: 'demo-car', name: '2022 Subaru Outback', type: 'other', subtype: 'manual', institution: null, mask: null, itemId: null, source: 'manual', accountClass: 'asset', manualCategory: 'vehicle' },
  ];

  await db.batch(
    accounts.map((a) => ({
      sql: `INSERT OR IGNORE INTO accounts (id, name, type, subtype, institution_name, mask, item_id,
              source, account_class, manual_category, currency_code, excluded, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'USD', 0, ?, ?)`,
      args: [a.id, a.name, a.type, a.subtype, a.institution, a.mask, a.itemId,
             a.source, a.accountClass, a.manualCategory, now, now],
    })),
    'write',
  );

  // ── Balance history ─────────────────────────────────────────────────────────
  const DAYS = 550;
  const rows: { sql: string; args: (string | number)[] }[] = [];
  const push = (id: string, day: number, value: number) => {
    rows.push({
      sql: `INSERT OR IGNORE INTO balance_history (account_id, balance, date) VALUES (?, ?, ?)`,
      args: [id, Math.round(value * 100) / 100, daysAgo(day)],
    });
  };

  // Checking: paycheck cycle around a slowly rising base.
  let checkingBase = 21500;
  for (let day = DAYS; day >= 0; day--) {
    checkingBase += 4.0 + (rand() - 0.5) * 30;
    const cycle = 3300 * Math.abs(Math.sin(((DAYS - day) % 30) / 30 * Math.PI));
    push('demo-checking', day, checkingBase + cycle);
  }

  // Savings: monthly deposits, small interest.
  let savings = 24800;
  for (let day = DAYS; day >= 0; day--) {
    if (day % 30 === 0) savings += 480;
    savings *= 1.00010;
    push('demo-savings', day, savings);
  }

  // Brokerage + 401(k): market-like walks with drift, weekday snapshots only —
  // the weekend gaps exercise as-of carry-forward exactly like real Plaid data.
  let brokerage = 62800;
  let k401 = 74100;
  for (let day = DAYS; day >= 0; day--) {
    brokerage *= 1 + 0.00055 + (rand() - 0.5) * 0.018;
    k401 *= 1 + 0.00050 + (rand() - 0.5) * 0.012;
    if (day % 120 < 14) brokerage *= 0.9985; // occasional drawdown stretches
    const dow = new Date(`${daysAgo(day)}T00:00:00Z`).getUTCDay();
    if (dow !== 0 && dow !== 6) {
      push('demo-brokerage', day, brokerage);
      push('demo-401k', day, k401);
    }
  }

  // Credit card: statement cycle, positive = owed.
  for (let day = DAYS; day >= 0; day--) {
    const cyclePos = (DAYS - day) % 30;
    const balance = 350 + cyclePos * 85 + rand() * 120 - (cyclePos > 24 ? 1900 : 0);
    push('demo-credit', day, Math.max(120, balance));
  }

  // Auto loan: amortizes down, monthly snapshots (sparse history).
  let loan = 21400;
  for (let day = DAYS; day >= 0; day -= 30) {
    push('demo-autoloan', day, loan);
    loan -= 410;
  }

  // Manual vehicle: quarterly owner updates, depreciating — long carry-forward spans.
  let car = 27400;
  for (let day = DAYS; day >= 0; day -= 90) {
    push('demo-car', day, car);
    car -= 700;
  }

  await db.batch(rows, 'write');
  await seedDemoInvestments();
  await seedDemoLiving();
}

async function seedDemoInvestments(): Promise<void> {
  const now = new Date().toISOString();
  const positions = [
    ['demo-pos-aapl', 'demo-brokerage', 'AAPL', 'Apple Inc.', 'stock', '38', '226.42', '171.80'],
    ['demo-pos-msft', 'demo-brokerage', 'MSFT', 'Microsoft Corporation', 'stock', '21', '512.18', '389.40'],
    ['demo-pos-vti', 'demo-brokerage', 'VTI', 'Vanguard Total Stock Market ETF', 'etf', '66', '318.74', '251.25'],
    ['demo-pos-vxus', 'demo-401k', 'VXUS', 'Vanguard Total International Stock ETF', 'etf', '186', '74.16', '62.84'],
    ['demo-pos-bnd', 'demo-401k', 'BND', 'Vanguard Total Bond Market ETF', 'etf', '265', '73.21', '71.95'],
    ['demo-pos-fxaix', 'demo-401k', 'FXAIX', 'Fidelity 500 Index Fund', 'mutualfund', '238.4401', '224.37', '176.14'],
  ] as const;
  const statements: { sql: string; args: (string | number | null)[] }[] = positions.map((p) => {
    const units = Number(p[5]);
    const price = Number(p[6]);
    const average = Number(p[7]);
    return {
      sql: `INSERT OR IGNORE INTO investment_positions
              (id, account_id, source, provider_position_id, symbol, name, instrument_type,
               units, price, average_price, market_value, gain_loss, currency_code,
               cash_equivalent, as_of_at)
            VALUES (?, ?, 'demo', ?, ?, ?, ?, ?, ?, ?, ?, ?, 'USD', 0, ?)`,
      args: [p[0], p[1], p[0], p[2], p[3], p[4], p[5], p[6], p[7],
        (units * price).toFixed(2), (units * (price - average)).toFixed(2), now],
    };
  });
  statements.push(
    { sql: `INSERT OR IGNORE INTO investment_balances (account_id, currency_code, source, cash, buying_power, as_of_at) VALUES ('demo-brokerage', 'USD', 'demo', '12842.66', '12842.66', ?)`, args: [now] },
    { sql: `INSERT OR IGNORE INTO investment_balances (account_id, currency_code, source, cash, buying_power, as_of_at) VALUES ('demo-401k', 'USD', 'demo', '4210.18', '4210.18', ?)`, args: [now] },
    { sql: `INSERT OR IGNORE INTO investment_activities (id, account_id, source, activity_type, trade_date, settlement_date, symbol, description, units, price, amount, fee, currency_code, updated_at)
            VALUES ('demo-act-1', 'demo-brokerage', 'demo', 'DIVIDEND', ?, ?, 'AAPL', 'Cash dividend', NULL, NULL, '9.50', NULL, 'USD', ?)`, args: [daysAgo(8), daysAgo(6), now] },
    { sql: `INSERT OR IGNORE INTO investment_activities (id, account_id, source, activity_type, trade_date, settlement_date, symbol, description, units, price, amount, fee, currency_code, updated_at)
            VALUES ('demo-act-2', 'demo-brokerage', 'demo', 'BUY', ?, ?, 'VTI', 'Bought Vanguard Total Stock Market ETF', '4', '312.18', '-1248.72', NULL, 'USD', ?)`, args: [daysAgo(18), daysAgo(16), now] },
    { sql: `INSERT OR IGNORE INTO investment_activities (id, account_id, source, activity_type, trade_date, settlement_date, symbol, description, units, price, amount, fee, currency_code, updated_at)
            VALUES ('demo-act-3', 'demo-401k', 'demo', 'CONTRIBUTION', ?, ?, NULL, 'Employer plan contribution', NULL, NULL, '1650.00', NULL, 'USD', ?)`, args: [daysAgo(31), daysAgo(31), now] },
  );
  await db.batch(statements, 'write');
}

async function seedDemoLiving(): Promise<void> {
  const existing = await db.execute("SELECT COUNT(*) AS c FROM transactions WHERE id LIKE 'demo-liv-%'");
  if (Number((existing.rows[0] as unknown as { c: number }).c) > 0) return;

  const ymd = (year: number, month: number, day: number): string => {
    const dim = new Date(Date.UTC(year, month, 0)).getUTCDate();
    const d = Math.min(day, dim);
    return `${year}-${String(month).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
  };
  const today = new Date();
  const year = today.getUTCFullYear();
  const month = today.getUTCMonth() + 1;
  const statements: { sql: string; args: (string | number | null)[] }[] = [];
  let n = 0;
  const push = (
    accountId: string,
    date: string,
    name: string,
    merchant: string | null,
    amount: number,
    category: string,
    extra: { party?: number; origin?: string; dest?: string; kind?: string } = {},
  ) => {
    n += 1;
    statements.push({
      sql: `INSERT OR IGNORE INTO transactions
              (id, account_id, date, name, merchant_name, amount, category, raw_category,
               pending, ignored, party_size, trip_origin, trip_destination, trip_kind)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0, 0, ?, ?, ?, ?)`,
      args: [
        `demo-liv-${String(n).padStart(4, '0')}`, accountId, date, name, merchant, amount,
        category, category, extra.party ?? null, extra.origin ?? null, extra.dest ?? null, extra.kind ?? null,
      ],
    });
  };

  const dining = [
    ['Chipotle', 'CHIPOTLE', 14.85],
    ['Blue Bottle Coffee', 'BLUE BOTTLE', 6.25],
    ['Thai Spoon', 'THAI SPOON', 42.10],
    ['Sweetgreen', 'SWEETGREEN', 16.40],
    ['Shake Shack', 'SHAKE SHACK', 18.75],
    ['Campus Dining', 'CAMPUS DINING', 11.50],
  ] as const;
  const groceries = [
    ['Whole Foods Market', 'WHOLE FOODS', 68.40],
    ['Trader Joe\'s', 'TRADER JOE\'S', 54.20],
    ['Publix', 'PUBLIX', 71.15],
  ] as const;

  for (let ago = 5; ago >= 0; ago--) {
    let y = year;
    let m = month - ago;
    while (m <= 0) { m += 12; y -= 1; }
    const dim = new Date(Date.UTC(y, m, 0)).getUTCDate();
    const isCurrent = ago === 0;
    const lastDay = isCurrent ? Math.min(today.getUTCDate(), dim) : dim;

    push('demo-checking', ymd(y, m, 1), 'UNIVERSITY STIPEND', 'University Payroll', -3200, 'INCOME');
    push('demo-checking', ymd(y, m, 1), 'CAMPUS HEIGHTS RENT', 'Campus Heights', 1450, 'RENT_AND_UTILITIES');
    if (lastDay >= 5) push('demo-checking', ymd(y, m, 5), 'CITY POWER AND LIGHT', 'City Power', 162 + ago, 'RENT_AND_UTILITIES');
    if (lastDay >= 8) push('demo-credit', ymd(y, m, 8), 'NETFLIX.COM', 'Netflix', 15.49, 'ENTERTAINMENT');
    if (lastDay >= 12) push('demo-checking', ymd(y, m, 12), 'T-MOBILE', 'T-Mobile', 45, 'RENT_AND_UTILITIES');
    if (lastDay >= 15) push('demo-checking', ymd(y, m, 15), 'TUITION PAYMENT PLAN', 'University Bursar', 400, 'LOAN_PAYMENTS');

    for (let day = 3; day <= lastDay; day += 6) {
      const g = groceries[(ago + day) % groceries.length]!;
      push('demo-checking', ymd(y, m, day), g[1], g[0], g[2] + ago * 1.5, 'FOOD_AND_DRINK');
    }

    const diningDays = [2, 4, 7, 9, 11, 14, 16, 18, 21, 23, 25, 28].filter((d) => d <= lastDay);
    for (const [i, day] of diningDays.entries()) {
      const d = dining[(ago + i) % dining.length]!;
      const party = d[0] === 'Thai Spoon' ? 3 : d[0] === 'Shake Shack' && i % 2 === 0 ? 2 : 1;
      push('demo-credit', ymd(y, m, day), d[1], d[0], d[2], 'FOOD_AND_DRINK', { party });
    }

    const rideDays = [3, 6, 10, 13, 17, 20, 24, 27].filter((d) => d <= lastDay);
    for (const day of rideDays) {
      push('demo-credit', ymd(y, m, day), 'UBER TRIP', 'Uber', 14.75 + (day % 3), 'TRANSPORTATION', {
        origin: 'Campus', dest: 'Home', kind: 'campus',
      });
    }
    if (ago % 2 === 0 && lastDay >= 19) {
      push('demo-credit', ymd(y, m, 19), 'UBER TRIP AIRPORT', 'Uber', 47.60, 'TRANSPORTATION', {
        origin: 'Campus', dest: 'Airport', kind: 'airport',
      });
    }
  }

  statements.push({
    sql: `INSERT OR IGNORE INTO living_route_alternatives (origin, destination, label, monthly_amount)
          VALUES ('Campus', 'Home', 'Transit pass', 30)`,
    args: [],
  });
  statements.push({
    sql: `INSERT OR REPLACE INTO settings (key, value) VALUES ('living_goal_kind', 'contribution')`,
    args: [],
  });
  statements.push({
    sql: `INSERT OR REPLACE INTO settings (key, value) VALUES ('living_goal_label', 'Index fund plan')`,
    args: [],
  });
  statements.push({
    sql: `INSERT OR REPLACE INTO settings (key, value) VALUES ('living_goal_monthly_contribution', '500.00')`,
    args: [],
  });

  await db.batch(statements, 'write');
}
