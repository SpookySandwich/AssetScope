import { describe, it, expect } from 'vitest';
import { classify, allocationBucket, netWorthContributionCents } from '../../native-api/src/services/classify.js';

const acct = (over: Partial<Parameters<typeof classify>[0]>) => ({
  type: 'depository', subtype: null, source: 'plaid', account_class: null, manual_category: null,
  ...over,
});

describe('classify: upstream gap fixes', () => {
  it('classifies loan as a liability (upstream ignored loans entirely)', () => {
    expect(classify(acct({ type: 'loan', subtype: 'auto' })))
      .toEqual({ category: 'loan', accountClass: 'liability' });
  });

  it('classifies credit as a liability', () => {
    expect(classify(acct({ type: 'credit', subtype: 'credit card' })))
      .toEqual({ category: 'credit', accountClass: 'liability' });
  });
});

describe('classify: investment subtypes', () => {
  it('maps retirement subtypes to retirement', () => {
    for (const subtype of ['401k', 'roth', 'ira', 'Roth 401k', 'pension']) {
      expect(classify(acct({ type: 'investment', subtype })).category).toBe('retirement');
    }
  });

  it('maps unknown investment subtypes to investment, not a duplicate hardcode', () => {
    expect(classify(acct({ type: 'investment', subtype: 'brokerage' })).category).toBe('investment');
    expect(classify(acct({ type: 'investment', subtype: 'something-new' })).category).toBe('investment');
    expect(classify(acct({ type: 'investment', subtype: null })).category).toBe('investment');
  });
});

describe('classify: manual accounts', () => {
  it('honors manual categories and class', () => {
    expect(classify(acct({ type: 'other', subtype: 'manual', source: 'manual', manual_category: 'property', account_class: 'asset' })))
      .toEqual({ category: 'property', accountClass: 'asset' });
    expect(classify(acct({ type: 'other', subtype: 'manual', source: 'manual', manual_category: 'loan', account_class: 'liability' })))
      .toEqual({ category: 'loan', accountClass: 'liability' });
  });

  it('forces manual loans to liability even if class is missing', () => {
    expect(classify(acct({ type: 'other', subtype: 'manual', source: 'manual', manual_category: 'loan', account_class: null })).accountClass)
      .toBe('liability');
  });

  it('handles legacy manual accounts with no category columns', () => {
    expect(classify(acct({ type: 'other', subtype: 'manual', source: null })))
      .toEqual({ category: 'other', accountClass: 'asset' });
  });
});

describe('allocationBucket', () => {
  it('groups bank and cash together and never returns a liability bucket', () => {
    expect(allocationBucket('bank')).toBe('bankCash');
    expect(allocationBucket('cash')).toBe('bankCash');
    expect(allocationBucket('retirement')).toBe('retirement');
    expect(allocationBucket('property')).toBe('property');
  });
});

describe('net-worth contribution signs', () => {
  it('keeps assets positive and makes positive Plaid debt negative', () => {
    expect(netWorthContributionCents(acct({ type: 'depository' }), 100_00)).toBe(100_00);
    expect(netWorthContributionCents(acct({ type: 'investment' }), 250_00)).toBe(250_00);
    expect(netWorthContributionCents(acct({ type: 'credit' }), 72_00)).toBe(-72_00);
    expect(netWorthContributionCents(acct({ type: 'loan' }), 9_000_00)).toBe(-9_000_00);
  });

  it('treats a Plaid credit-card overpayment as positive and manual debt as amount owed', () => {
    expect(netWorthContributionCents(acct({ type: 'credit' }), -20_00)).toBe(20_00);
    expect(netWorthContributionCents(acct({
      type: 'other', subtype: 'manual', source: 'manual',
      manual_category: 'loan', account_class: 'liability',
    }), -500_00)).toBe(-500_00);
  });
});
