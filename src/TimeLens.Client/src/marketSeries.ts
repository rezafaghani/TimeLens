export interface MarketSeries {
  id: string;
  seriesId: string;
  provider: string;
  exchange: string;
  symbol: string;
  assetClass: string;
  baseAsset: string;
  quoteAsset: string;
  marketDataType: string;
  timeframe: string;
  lastIngestedAt: string;
}

export interface InstrumentSummary {
  id: string;
  label: string;
  series: MarketSeries[];
  providers: string[];
  timeframes: string[];
  dataTypes: string[];
  lastIngestedAt: string;
}

export function getInstrumentId(series: Pick<MarketSeries, 'symbol' | 'exchange'>) {
  return `${series.exchange || 'unknown'}:${series.symbol || 'unknown'}`;
}

export function groupSeriesByInstrument(items: MarketSeries[]): InstrumentSummary[] {
  const instruments = new Map<string, InstrumentSummary>();

  for (const item of items) {
    const id = getInstrumentId(item);
    const instrument = instruments.get(id) ?? {
      id,
      label: item.symbol || id,
      series: [],
      providers: [],
      timeframes: [],
      dataTypes: [],
      lastIngestedAt: ''
    };

    instrument.series.push(item);
    addUnique(instrument.providers, item.provider);
    addUnique(instrument.timeframes, item.timeframe);
    addUnique(instrument.dataTypes, item.marketDataType);
    if (!instrument.lastIngestedAt || item.lastIngestedAt > instrument.lastIngestedAt) {
      instrument.lastIngestedAt = item.lastIngestedAt;
    }
    instruments.set(id, instrument);
  }

  return Array.from(instruments.values()).sort((a, b) => a.label.localeCompare(b.label));
}

function addUnique(values: string[], value: string) {
  if (value && !values.includes(value)) {
    values.push(value);
    values.sort();
  }
}
