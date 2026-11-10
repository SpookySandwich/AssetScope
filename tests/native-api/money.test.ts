import { describe, it, expect } from 'vitest';
import { toCents, centsToAmount, parseAmountToCents, ratio, money } from '../../native-api/src/services/money.js';

describe('money: cents conversion', () => {
  it('converts DB floats to cents with correct rounding', () => {
    expect(toCents(28410.45)).toBe(2841045);
    expect(toCents(-1340.005)).toBe(-134001); // half away from zero
    expect(toCents(0.1 + 0.2)).toBe(30);      // float noise collapses
    expect(toCents(0)).toBe(0);
  });

  it('formats cents as fixed two-decimal strings', () => {
    expect(centsToAmount(2841045)).toBe('28410.45');
    expect(centsToAmount(-134100)).toBe('-1341.00');
    expect(centsToAmount(5)).toBe('0.05');
    expect(centsToAmount(-5)).toBe('-0.05');
    expect(centsToAmount(0)).toBe('0.00');
  });

  it('round-trips typical balances', () => {
    for (const v of [0.01, 123.4, 999999.99, -0.07, 520000]) {
      expect(centsToAmount(toCents(v))).toBe(v.toFixed(2));
    }
  });
});

describe('money: parseAmountToCents', () => {
  it('accepts plain decimal strings', () => {
    expect(parseAmountToCents('520000.00')).toBe(52000000);
    expect(parseAmountToCents('0.5')).toBe(50);
    expect(parseAmountToCents('7')).toBe(700);
    expect(parseAmountToCents('-12.34')).toBe(-1234);
  });

  it('rejects malformed input', () => {
    expect(parseAmountToCents('')).toBeNull();
    expect(parseAmountToCents('1,000')).toBeNull();
    expect(parseAmountToCents('12.345')).toBeNull();
    expect(parseAmountToCents('abc')).toBeNull();
    expect(parseAmountToCents('1e5')).toBeNull();
    expect(parseAmountToCents('Infinity')).toBeNull();
  });
});

describe('money: ratio', () => {
  it('is null when the base is zero (SPEC §9.7)', () => {
    expect(ratio(500, 0)).toBeNull();
  });
  it('uses abs(base) so negative bases keep delta direction', () => {
    expect(ratio(100, -1000)).toBe('0.1000');
    expect(ratio(-100, 1000)).toBe('-0.1000');
  });
});

describe('money: dto', () => {
  it('defaults to USD', () => {
    expect(money(123456)).toEqual({ amount: '1234.56', currencyCode: 'USD' });
  });
});
