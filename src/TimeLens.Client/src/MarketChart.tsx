import { useEffect, useMemo, useRef, useState } from 'react';
import {
  CandlestickSeries,
  ColorType,
  HistogramSeries,
  LineSeries,
  createChart,
  type IChartApi,
  type ISeriesApi,
  type MouseEventParams,
  type UTCTimestamp
} from 'lightweight-charts';
import { formatMarketTime, formatTooltipTime } from './marketTime';
import type { TimeSeriesPoint } from './marketDataLive';

export type ChartMode = 'candlestick' | 'line';

interface MarketChartProps {
  points: TimeSeriesPoint[];
  mode: ChartMode;
  currency: string;
  timeZone: string;
  liveEnabled: boolean;
  liveStatus: string;
  newBarCount: number;
  onModeChange: (mode: ChartMode) => void;
  onJumpLatest: () => void;
}

export function MarketChart({ points, mode, currency, timeZone, liveEnabled, liveStatus, newBarCount, onModeChange, onJumpLatest }: MarketChartProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const priceSeriesRef = useRef<ISeriesApi<'Candlestick'> | ISeriesApi<'Line'> | null>(null);
  const volumeSeriesRef = useRef<ISeriesApi<'Histogram'> | null>(null);
  const [hovered, setHovered] = useState<TimeSeriesPoint | null>(null);
  const sorted = useMemo(() => [...points].sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()), [points]);
  const spanMs = sorted.length > 1
    ? new Date(sorted[sorted.length - 1].timestamp).getTime() - new Date(sorted[0].timestamp).getTime()
    : 0;

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const chart = createChart(container, {
      autoSize: true,
      layout: {
        background: { type: ColorType.Solid, color: '#ffffff' },
        textColor: '#475467'
      },
      grid: {
        vertLines: { color: '#eef2f7' },
        horzLines: { color: '#eef2f7' }
      },
      rightPriceScale: {
        borderColor: '#d8dee8',
        scaleMargins: { top: 0.08, bottom: 0.28 }
      },
      timeScale: {
        borderColor: '#d8dee8',
        timeVisible: true,
        secondsVisible: false,
        tickMarkFormatter: time => formatMarketTime(Number(time) * 1000, timeZone, spanMs)
      },
      localization: {
        priceFormatter: value => `${formatNumber(value)}${currency ? ` ${currency}` : ''}`
      },
      crosshair: {
        mode: 1
      }
    });

    chartRef.current = chart;
    const resizeObserver = new ResizeObserver(() => chart.applyOptions({ autoSize: true }));
    resizeObserver.observe(container);

    return () => {
      resizeObserver.disconnect();
      chart.remove();
      chartRef.current = null;
      priceSeriesRef.current = null;
      volumeSeriesRef.current = null;
    };
  }, []);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;

    if (priceSeriesRef.current) {
      chart.removeSeries(priceSeriesRef.current);
    }
    if (volumeSeriesRef.current) {
      chart.removeSeries(volumeSeriesRef.current);
    }

    priceSeriesRef.current = mode === 'candlestick'
      ? chart.addSeries(CandlestickSeries, {
        upColor: '#147a4b',
        downColor: '#c2413b',
        borderVisible: false,
        wickUpColor: '#147a4b',
        wickDownColor: '#c2413b'
      })
      : chart.addSeries(LineSeries, {
        color: '#1f6feb',
        lineWidth: 2
      });
    volumeSeriesRef.current = chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: ''
    });
    volumeSeriesRef.current.priceScale().applyOptions({ scaleMargins: { top: 0.78, bottom: 0 } });
  }, [mode]);

  useEffect(() => {
    const chart = chartRef.current;
    const priceSeries = priceSeriesRef.current;
    const volumeSeries = volumeSeriesRef.current;
    if (!chart || !priceSeries || !volumeSeries) return;

    chart.applyOptions({
      timeScale: {
        tickMarkFormatter: time => formatMarketTime(Number(time) * 1000, timeZone, spanMs)
      },
      localization: {
        priceFormatter: value => `${formatNumber(value)}${currency ? ` ${currency}` : ''}`
      }
    });

    if (mode === 'candlestick') {
      (priceSeries as ISeriesApi<'Candlestick'>).setData(sorted.map(point => ({
        time: toChartTime(point.timestamp),
        open: point.open,
        high: point.high,
        low: point.low,
        close: point.close
      })));
    } else {
      (priceSeries as ISeriesApi<'Line'>).setData(sorted.map(point => ({
        time: toChartTime(point.timestamp),
        value: point.close
      })));
    }

    volumeSeries.setData(sorted.map(point => ({
      time: toChartTime(point.timestamp),
      value: point.volume,
      color: point.close >= point.open ? 'rgba(20, 122, 75, 0.35)' : 'rgba(194, 65, 59, 0.35)'
    })));
    if (sorted.length) chart.timeScale().fitContent();
  }, [sorted, mode, currency, timeZone, spanMs]);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;

    const onMove = (params: MouseEventParams) => {
      if (!params.time) {
        setHovered(null);
        return;
      }
      const timestamp = Number(params.time) * 1000;
      setHovered(nearestPoint(sorted, timestamp));
    };
    chart.subscribeCrosshairMove(onMove);
    return () => chart.unsubscribeCrosshairMove(onMove);
  }, [sorted]);

  return (
    <section className="chart-zone market-chart-zone">
      <header className="chart-toolbar">
        <div>
          <h2>Market chart</h2>
          <p>{sorted.length ? `${sorted.length} bars loaded` : 'No market data is available for this period.'}</p>
        </div>
        <div className="chart-actions">
          <div className="segmented-control" role="group" aria-label="Chart type">
            <button className={mode === 'candlestick' ? 'active' : 'secondary'} type="button" onClick={() => onModeChange('candlestick')}>Candles</button>
            <button className={mode === 'line' ? 'active' : 'secondary'} type="button" onClick={() => onModeChange('line')}>Line</button>
          </div>
          <span className={`live-pill ${liveEnabled ? liveStatus.toLowerCase() : ''}`}>Live: {liveEnabled ? liveStatus : 'OFF'}</span>
          <button type="button" className="secondary" onClick={() => chartRef.current?.timeScale().fitContent()}>Reset zoom</button>
        </div>
      </header>
      <div className="chart-wrap">
        <div className="market-chart" ref={containerRef} />
        {hovered && <ChartTooltip point={hovered} currency={currency} timeZone={timeZone} />}
        {!sorted.length && <div className="chart-empty">No market data is available for this period.</div>}
        {newBarCount > 0 && (
          <button type="button" className="new-bars" onClick={() => {
            chartRef.current?.timeScale().fitContent();
            onJumpLatest();
          }}>
            {newBarCount} new {newBarCount === 1 ? 'bar' : 'bars'} - Jump to latest
          </button>
        )}
      </div>
    </section>
  );
}

function ChartTooltip({ point, currency, timeZone }: { point: TimeSeriesPoint; currency: string; timeZone: string }) {
  return (
    <div className="chart-tooltip-panel">
      <strong>{formatTooltipTime(point.timestamp, timeZone)}</strong>
      <span>O: {formatMoney(point.open, currency)}</span>
      <span>H: {formatMoney(point.high, currency)}</span>
      <span>L: {formatMoney(point.low, currency)}</span>
      <span>C: {formatMoney(point.close, currency)}</span>
      <span>V: {formatNumber(point.volume)}</span>
    </div>
  );
}

function nearestPoint(points: TimeSeriesPoint[], timestamp: number) {
  let nearest = points[0] ?? null;
  let nearestDistance = nearest ? Math.abs(new Date(nearest.timestamp).getTime() - timestamp) : Number.MAX_SAFE_INTEGER;
  for (const point of points) {
    const distance = Math.abs(new Date(point.timestamp).getTime() - timestamp);
    if (distance < nearestDistance) {
      nearest = point;
      nearestDistance = distance;
    }
  }
  return nearest;
}

function toChartTime(value: string): UTCTimestamp {
  return Math.floor(new Date(value).getTime() / 1000) as UTCTimestamp;
}

function formatMoney(value: number, currency: string) {
  return currency ? `${formatNumber(value)} ${currency}` : formatNumber(value);
}

function formatNumber(value: number) {
  if (Math.abs(value) >= 1000) return value.toLocaleString('en-US', { maximumFractionDigits: 2 });
  if (Math.abs(value) >= 1) return value.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
  return value.toPrecision(4);
}
