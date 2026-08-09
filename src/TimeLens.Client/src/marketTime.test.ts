import { describe, expect, it } from 'vitest';
import { formatMarketTime, formatTooltipTime, rangeForPreset } from './marketTime';

describe('market time formatting', () => {
  it('uses 24-hour intraday labels in the selected timezone', () => {
    expect(formatMarketTime('2026-08-09T12:15:00Z', 'UTC', 6 * 60 * 60 * 1000)).toBe('12:15');
  });

  it('includes dates for multi-day ranges', () => {
    expect(formatMarketTime('2026-08-09T12:15:00Z', 'UTC', 7 * 24 * 60 * 60 * 1000)).toBe('09 Aug 12:15');
  });

  it('formats tooltip timestamps precisely', () => {
    expect(formatTooltipTime('2026-08-09T12:15:00Z', 'UTC')).toBe('2026-08-09 12:15');
  });

  it('builds relative ranges without arbitrary absolute defaults', () => {
    const [start, end] = rangeForPreset('1H', new Date('2026-08-09T12:00:00Z'));
    expect(start.toISOString()).toBe('2026-08-09T11:00:00.000Z');
    expect(end.toISOString()).toBe('2026-08-09T12:00:00.000Z');
  });
});
