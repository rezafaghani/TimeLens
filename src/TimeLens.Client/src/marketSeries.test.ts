import { describe, expect, it } from 'vitest';
import { groupSeriesByInstrument } from './marketSeries';

describe('groupSeriesByInstrument', () => {
  it('groups market data series by exchange and symbol', () => {
    const instruments = groupSeriesByInstrument([
      series('BTC-USD', '1m', '2026-01-01T00:00:00Z'),
      series('BTC-USD', '1h', '2026-01-02T00:00:00Z'),
      series('ETH-USD', '1m', '2026-01-01T00:00:00Z')
    ]);

    expect(instruments.find(instrument => instrument.id === 'Coinbase:BTC-USD')).toMatchObject({
      label: 'BTC-USD',
      providers: ['coinbase-exchange'],
      timeframes: ['1h', '1m'],
      dataTypes: ['ohlcv'],
      lastIngestedAt: '2026-01-02T00:00:00Z'
    });
    expect(instruments.find(instrument => instrument.id === 'Coinbase:ETH-USD')?.series).toHaveLength(1);
  });
});

function series(symbol: string, timeframe: string, lastIngestedAt: string) {
  const [baseAsset, quoteAsset] = symbol.split('-');
  return {
    id: `coinbase:${symbol}:${timeframe}`,
    seriesId: `coinbase:${symbol}:${timeframe}`.toLowerCase(),
    provider: 'coinbase-exchange',
    exchange: 'Coinbase',
    symbol,
    assetClass: 'Crypto',
    baseAsset,
    quoteAsset,
    marketDataType: 'ohlcv',
    timeframe,
    lastIngestedAt
  };
}
