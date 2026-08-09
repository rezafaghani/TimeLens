import { describe, expect, it } from 'vitest';
import { hubUrl, upsertBar, type MarketDataUpdatedEvent, type TimeSeriesPoint } from './marketDataLive';

describe('market-data live helpers', () => {
  it('updates an existing same-timestamp bar', () => {
    const updated = upsertBar([bar('2026-08-09T12:15:00Z', 10)], event('2026-08-09T12:15:00Z', 12));
    expect(updated).toHaveLength(1);
    expect(updated[0].close).toBe(12);
  });

  it('appends newer bars and keeps chronological order', () => {
    const updated = upsertBar([bar('2026-08-09T12:15:00Z', 10)], event('2026-08-09T12:30:00Z', 11));
    expect(updated.map(x => x.timestamp)).toEqual(['2026-08-09T12:15:00Z', '2026-08-09T12:30:00Z']);
  });

  it('applies historical corrections without duplicates', () => {
    const updated = upsertBar([bar('2026-08-09T12:30:00Z', 11)], event('2026-08-09T12:00:00Z', 9));
    expect(updated.map(x => x.timestamp)).toEqual(['2026-08-09T12:00:00Z', '2026-08-09T12:30:00Z']);
  });

  it('maps api base urls to the SignalR hub url', () => {
    expect(hubUrl('/api')).toBe('/hubs/market-data');
    expect(hubUrl('http://localhost:5100/api')).toBe('http://localhost:5100/hubs/market-data');
  });
});

function bar(timestamp: string, close: number): TimeSeriesPoint {
  return { timestamp, open: close, high: close, low: close, close, volume: 1, asOf: timestamp };
}

function event(timestamp: string, close: number): MarketDataUpdatedEvent {
  return {
    eventId: `event-${timestamp}`,
    version: 1,
    occurredAt: timestamp,
    datasetId: 'coinbase-exchange:coinbase:eth-usd:ohlcv:15m',
    seriesId: 'coinbase:eth-usd:15m',
    providerId: 'coinbase-exchange',
    exchange: 'Coinbase',
    instrumentId: 'ETH-USD',
    symbol: 'ETH-USD',
    marketDataType: 'ohlcv',
    timeframe: '15m',
    operation: 'upsert',
    dataTimestamp: timestamp,
    asOf: timestamp,
    bar: { timestamp, open: close, high: close, low: close, close, volume: 1 }
  };
}
