import * as signalR from '@microsoft/signalr';

export interface TimeSeriesPoint {
  timestamp: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  value?: number;
  asOf: string;
}

export interface MarketDataUpdatedEvent {
  eventId: string;
  version: number;
  occurredAt: string;
  datasetId: string;
  seriesId: string;
  providerId: string;
  exchange: string;
  instrumentId: string;
  symbol: string;
  marketDataType: string;
  timeframe: string;
  operation: string;
  dataTimestamp: string;
  asOf: string;
  bar: Omit<TimeSeriesPoint, 'asOf' | 'value'>;
}

export interface MarketDataSubscription {
  datasetId: string;
  seriesId: string;
  providerId: string;
  symbol: string;
  marketDataType: string;
  timeframe: string;
}

export function upsertBar(points: TimeSeriesPoint[], update: MarketDataUpdatedEvent) {
  const nextBar: TimeSeriesPoint = { ...update.bar, timestamp: update.dataTimestamp, asOf: update.asOf };
  const index = points.findIndex(point => sameInstant(point.timestamp, nextBar.timestamp));
  if (index >= 0) {
    const next = [...points];
    next[index] = nextBar;
    return next.sort(compareBars);
  }

  return [...points, nextBar].sort(compareBars);
}

export function sameInstant(left: string, right: string) {
  return new Date(left).getTime() === new Date(right).getTime();
}

export function compareBars(left: TimeSeriesPoint, right: TimeSeriesPoint) {
  return new Date(left.timestamp).getTime() - new Date(right.timestamp).getTime();
}

export function hubUrl(apiBase: string) {
  return apiBase.endsWith('/api') ? `${apiBase.slice(0, -4)}/hubs/market-data` : '/hubs/market-data';
}

export function buildHubConnection(apiBase: string) {
  return new signalR.HubConnectionBuilder()
    .withUrl(hubUrl(apiBase))
    .withAutomaticReconnect()
    .build();
}

export function latestTimestamp(points: TimeSeriesPoint[]) {
  return points.reduce((latest, point) => Math.max(latest, new Date(point.timestamp).getTime()), 0);
}
