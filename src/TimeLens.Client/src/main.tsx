import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import type { HubConnection } from '@microsoft/signalr';
import { MarketChart, type ChartMode } from './MarketChart';
import { getInstrumentId, groupSeriesByInstrument, type InstrumentSummary } from './marketSeries';
import { buildScheduleJobHistoryRows, buildScheduleStatusRows, suggestCronFromGranularity, type IngestionExecution, type IngestionJob, type IngestionSchedule } from './ingestionStatus';
import { buildHubConnection, latestTimestamp, upsertBar, type MarketDataSubscription, type MarketDataUpdatedEvent, type TimeSeriesPoint } from './marketDataLive';
import { formatMarketTime, rangeForPreset, toLocalInput, type RangePreset } from './marketTime';
import './styles.css';

interface DatasetMetadata {
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
  currency: string;
  timeZone: string;
  providerInstrumentId: string;
  endpoint: string;
  unit: string;
  calendar: string;
  deprecated: boolean;
  requestParameters: Record<string, string>;
  firstAvailableAt: string | null;
  lastAvailableAt: string | null;
  lastIngestedAt: string;
}

interface QualityFinding {
  validatorId: string;
  category: string;
  severity: string;
  qualityStatus: string;
  title: string;
  message: string;
  affectedStart: string | null;
  affectedEnd: string | null;
  affectedCount: number | null;
}

interface QualityStatus {
  overallStatus: string;
  latestExecutionId: string;
  asOf: string;
}

interface QualityValidationJob {
  id: string;
  name: string;
  enabled: boolean;
  cronExpression: string;
  windowStartExpression: string;
  windowEndExpression: string;
  targets: { targetType: string; targetId: string }[];
  checks: { validatorId: string; enabled: boolean }[];
  lastQueuedAt: string | null;
}

interface QualityExecution {
  id: string;
  jobId: string;
  triggerType: string;
  status: string;
  queuedAt: string;
  evaluatedStart: string | null;
  evaluatedEnd: string | null;
  warningCount: number;
  criticalCount: number;
  error: string;
}

const apiBase = import.meta.env.VITE_API_BASE_URL ?? '/api';
const defaultTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';

function App() {
  const [series, setSeries] = useState<DatasetMetadata[]>([]);
  const [selectedInstrumentId, setSelectedInstrumentId] = useState('');
  const [selected, setSelected] = useState<DatasetMetadata | null>(null);
  const [points, setPoints] = useState<TimeSeriesPoint[]>([]);
  const [schedules, setSchedules] = useState<IngestionSchedule[]>([]);
  const [jobs, setJobs] = useState<IngestionJob[]>([]);
  const [executions, setExecutions] = useState<IngestionExecution[]>([]);
  const [findings, setFindings] = useState<QualityFinding[]>([]);
  const [qualityStatus, setQualityStatus] = useState<QualityStatus | null>(null);
  const [validationJobs, setValidationJobs] = useState<QualityValidationJob[]>([]);
  const [validationExecutions, setValidationExecutions] = useState<QualityExecution[]>([]);
  const [search, setSearch] = useState('');
  const [provider, setProvider] = useState('');
  const [assetClass, setAssetClass] = useState('');
  const [timeframe, setTimeframe] = useState('');
  const [start, setStart] = useState(toLocalInput(new Date(Date.now() - 24 * 60 * 60 * 1000)));
  const [end, setEnd] = useState(toLocalInput(new Date()));
  const [asOf, setAsOf] = useState('');
  const [timeZone, setTimeZone] = useState(defaultTimeZone);
  const [chartMode, setChartMode] = useState<ChartMode>('candlestick');
  const [liveEnabled, setLiveEnabled] = useState(false);
  const [liveStatus, setLiveStatus] = useState('Disconnected');
  const [newBarCount, setNewBarCount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const connectionRef = useRef<HubConnection | null>(null);

  useEffect(() => {
    void loadSeriesList();
  }, []);

  useEffect(() => {
    if (!liveEnabled || !selected) {
      void stopLive();
      return;
    }

    let cancelled = false;
    const buffer: MarketDataUpdatedEvent[] = [];
    let syncing = true;
    const subscription = toSubscription(selected);
    const connection = buildHubConnection(apiBase);
    connectionRef.current = connection;

    connection.on('MarketDataUpdated', (event: MarketDataUpdatedEvent) => {
      if (event.datasetId !== selected.id) return;
      if (syncing) {
        buffer.push(event);
        return;
      }
      applyLiveUpdate(event);
    });
    connection.onreconnecting(() => setLiveStatus('Reconnecting'));
    connection.onreconnected(() => {
      setLiveStatus('Live');
      void catchUpAfterReconnect(selected);
    });
    connection.onclose(() => setLiveStatus(liveEnabled ? 'Disconnected' : 'Disconnected'));

    void (async () => {
      try {
        setLiveStatus('Connecting');
        await connection.start();
        await connection.invoke('Subscribe', subscription);
        setLiveStatus('Live');
        await loadBars(selected);
        if (!cancelled) {
          setPoints(current => buffer.reduce((next, event) => upsertBar(next, event), current));
          syncing = false;
        }
      } catch {
        if (!cancelled) setLiveStatus('Disconnected');
      }
    })();

    return () => {
      cancelled = true;
      void connection.invoke('Unsubscribe', subscription).catch(() => undefined);
      void connection.stop();
    };
  }, [liveEnabled, selected?.id]);

  const instruments = useMemo(() => groupSeriesByInstrument(series), [series]);
  const selectedInstrument = instruments.find(instrument => instrument.id === selectedInstrumentId);
  const selectedSeries = selectedInstrument?.series ?? [];
  const providers = unique(series.map(item => item.provider));
  const assetClasses = unique(series.map(item => item.assetClass));
  const timeframes = unique(series.map(item => item.timeframe));

  async function loadSeriesList() {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (provider) params.set('provider', provider);
      if (assetClass) params.set('assetClass', assetClass);
      if (timeframe) params.set('timeframe', timeframe);
      const response = await fetch(`${apiBase}/datasets?${params}`);
      if (!response.ok) throw new Error('Series request failed');
      const result = await response.json() as DatasetMetadata[];
      const nextInstruments = groupSeriesByInstrument(result);
      const nextInstrumentId = selectedInstrumentId && nextInstruments.some(instrument => instrument.id === selectedInstrumentId)
        ? selectedInstrumentId
        : nextInstruments[0]?.id ?? '';
      const nextSeries = result.filter(item => getInstrumentId(item) === nextInstrumentId);
      const nextSelected = nextSeries.find(item => item.id === selected?.id) ?? nextSeries[0] ?? null;
      setSeries(result);
      setSelectedInstrumentId(nextInstrumentId);
      setSelected(nextSelected);
      if (nextSelected) {
        await Promise.all([loadBars(nextSelected), loadIngestionStatus(nextSelected.seriesId), loadQuality(nextSelected)]);
      }
    } catch {
      setError('Unable to load market data series.');
    } finally {
      setLoading(false);
    }
  }

  async function loadBars(item = selected) {
    if (!item) return [];
    setError('');
    const params = new URLSearchParams({ start: start.trim(), end: end.trim(), timeZone });
    if (asOf) params.set('asOf', asOf.trim());
    const response = await fetch(`${apiBase}/datasets/${encodeURIComponent(item.id)}/series?${params}`);
    if (!response.ok) {
      setError('Unable to load OHLCV bars.');
      return [];
    }
    const result = await response.json() as TimeSeriesPoint[];
    setPoints(result);
    setNewBarCount(0);
    return result;
  }

  async function loadIngestionStatus(seriesId: string) {
    if (!seriesId) return;
    const encoded = encodeURIComponent(seriesId);
    const [scheduleResponse, jobResponse, executionResponse] = await Promise.all([
      fetch(`${apiBase}/ingestion/series/${encoded}/schedules`),
      fetch(`${apiBase}/ingestion/series/${encoded}/jobs`),
      fetch(`${apiBase}/ingestion/series/${encoded}/executions`)
    ]);
    if (scheduleResponse.ok) setSchedules(await scheduleResponse.json() as IngestionSchedule[]);
    if (jobResponse.ok) setJobs(await jobResponse.json() as IngestionJob[]);
    if (executionResponse.ok) setExecutions(await executionResponse.json() as IngestionExecution[]);
  }

  async function loadQuality(item = selected) {
    if (!item) return;
    const id = encodeURIComponent(item.seriesId || item.id);
    const [findingsResponse, statusResponse, jobsResponse, executionsResponse] = await Promise.all([
      fetch(`${apiBase}/data-quality/findings?seriesId=${id}&activeOnly=true`),
      fetch(`${apiBase}/data-quality/status?seriesId=${id}`),
      fetch(`${apiBase}/data-quality/jobs?seriesId=${id}`),
      fetch(`${apiBase}/data-quality/executions?seriesId=${id}`)
    ]);
    setFindings(findingsResponse.ok ? await findingsResponse.json() as QualityFinding[] : []);
    setQualityStatus(statusResponse.ok ? await statusResponse.json() as QualityStatus : null);
    setValidationJobs(jobsResponse.ok ? await jobsResponse.json() as QualityValidationJob[] : []);
    setValidationExecutions(executionsResponse.ok ? await executionsResponse.json() as QualityExecution[] : []);
  }

  async function saveSchedule(schedule: IngestionSchedule) {
    const response = await fetch(`${apiBase}/ingestion/schedules/${encodeURIComponent(schedule.id)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        cronExpression: schedule.cronExpression,
        enabled: schedule.enabled,
        windowStartExpression: schedule.windowStartExpression,
        windowEndExpression: schedule.windowEndExpression,
        batchSize: schedule.batchSize
      })
    });
    if (response.ok && selected) await loadIngestionStatus(selected.seriesId);
  }

  async function queueBackload(schedule: IngestionSchedule) {
    if (!selected) return;
    const response = await fetch(`${apiBase}/ingestion/schedules/${encodeURIComponent(schedule.id)}/backloads`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        datasetId: selected.id,
        windowStartExpression: start,
        windowEndExpression: end,
        batchSize: schedule.batchSize
      })
    });
    if (!response.ok) {
      setError(await response.text() || 'Unable to queue backload.');
      return;
    }
    await loadIngestionStatus(selected.seriesId);
  }

  async function createStandardValidation() {
    if (!selected) return;
    const checks = [
      ['completeness.missing-timestamps', { granularity: isoDurationForTimeframe(selected.timeframe) }],
      ['timestamps.alignment', { granularity: isoDurationForTimeframe(selected.timeframe) }],
      ['freshness.latest-point', { gracePeriod: isoDurationForTimeframe(selected.timeframe) }],
      ['validity.ohlc-consistency', { requirePositivePrices: false }],
      ['validity.price-positive', {}],
      ['validity.volume', { allowZeroVolume: true }]
    ] as const;
    const response = await fetch(`${apiBase}/data-quality/jobs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name: `Standard OHLCV Quality - ${selected.symbol} ${selected.timeframe}`,
        description: `Standard OHLCV quality checks for ${selected.provider} ${selected.symbol} ${selected.timeframe}.`,
        enabled: true,
        cronExpression: suggestCronFromGranularity(selected.timeframe) || '*/15 * * * *',
        timeZone: selected.timeZone || 'UTC',
        windowStartExpression: 'now-2h',
        windowEndExpression: 'now',
        targets: [{ targetType: 'series', targetId: selected.seriesId || selected.id }],
        checks: checks.map(([validatorId, configuration], sortOrder) => ({
          validatorId,
          enabled: true,
          configuration,
          sortOrder
        }))
      })
    });
    if (!response.ok) {
      setError(await response.text() || 'Unable to configure validation.');
      return;
    }
    await loadQuality(selected);
  }

  async function runValidation(job: QualityValidationJob, range?: { start: string; end: string }) {
    if (!selected) return;
    const response = await fetch(`${apiBase}/data-quality/jobs/${encodeURIComponent(job.id)}/runs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        start: range?.start,
        end: range?.end,
        triggerType: range ? 'backfill' : 'manual'
      })
    });
    if (!response.ok) {
      setError(await response.text() || 'Unable to run validation.');
      return;
    }
    await loadQuality(selected);
  }

  async function setValidationEnabled(job: QualityValidationJob, enabled: boolean) {
    if (!selected) return;
    const response = await fetch(`${apiBase}/data-quality/jobs/${encodeURIComponent(job.id)}/enabled`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled })
    });
    if (!response.ok) {
      setError(await response.text() || 'Unable to update validation.');
      return;
    }
    await loadQuality(selected);
  }

  function selectInstrument(instrument: InstrumentSummary) {
    const next = instrument.series[0] as DatasetMetadata | undefined;
    setSelectedInstrumentId(instrument.id);
    setSelected(next ?? null);
    setPoints([]);
    setFindings([]);
    setQualityStatus(null);
    setValidationJobs([]);
    setValidationExecutions([]);
    setLiveEnabled(false);
    if (next) void Promise.all([loadBars(next), loadIngestionStatus(next.seriesId), loadQuality(next)]);
  }

  function selectSeries(item: DatasetMetadata) {
    setSelected(item);
    setPoints([]);
    setValidationJobs([]);
    setValidationExecutions([]);
    setLiveEnabled(false);
    void Promise.all([loadBars(item), loadIngestionStatus(item.seriesId), loadQuality(item)]);
  }

  function applyRangePreset(preset: RangePreset) {
    const [nextStart, nextEnd] = rangeForPreset(preset);
    setStart(toLocalInput(nextStart));
    setEnd(toLocalInput(nextEnd));
  }

  function applyLiveUpdate(event: MarketDataUpdatedEvent) {
    setPoints(current => {
      const previousLatest = latestTimestamp(current);
      const next = upsertBar(current, event);
      if (new Date(event.dataTimestamp).getTime() > previousLatest) {
        setNewBarCount(count => count + 1);
      }
      return next;
    });
  }

  async function catchUpAfterReconnect(item: DatasetMetadata) {
    const latest = latestTimestamp(points);
    if (!latest) {
      await loadBars(item);
      return;
    }

    const params = new URLSearchParams({
      start: toLocalInput(new Date(latest)),
      end: end.trim(),
      timeZone
    });
    const response = await fetch(`${apiBase}/datasets/${encodeURIComponent(item.id)}/series?${params}`);
    if (!response.ok) return;
    const missed = await response.json() as TimeSeriesPoint[];
    setPoints(current => missed.reduce((next, bar) => upsertBar(next, pointToEvent(item, bar)), current));
  }

  async function stopLive() {
    const connection = connectionRef.current;
    connectionRef.current = null;
    if (connection) await connection.stop();
    setLiveStatus('Disconnected');
    setNewBarCount(0);
  }

  return (
    <main className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <strong>TimeLens</strong>
          <span>Market data intelligence</span>
        </div>

        <div className="filter-panel">
          <label><span>Search</span><input value={search} onChange={event => setSearch(event.target.value)} onKeyDown={event => event.key === 'Enter' && void loadSeriesList()} placeholder="BTC, ETH, provider, series id" /></label>
          <label><span>Provider</span><select value={provider} onChange={event => setProvider(event.target.value)}><option value="">All providers</option>{providers.map(value => <option key={value}>{value}</option>)}</select></label>
          <label><span>Asset class</span><select value={assetClass} onChange={event => setAssetClass(event.target.value)}><option value="">All asset classes</option>{assetClasses.map(value => <option key={value}>{value}</option>)}</select></label>
          <label><span>Timeframe</span><select value={timeframe} onChange={event => setTimeframe(event.target.value)}><option value="">All timeframes</option>{timeframes.map(value => <option key={value}>{value}</option>)}</select></label>
          <button onClick={() => void loadSeriesList()}>{loading ? 'Loading' : 'Search'}</button>
        </div>

        <div className="sidebar-heading">Instruments</div>
        <div className="dataset-list">
          {instruments.map(instrument => (
            <button key={instrument.id} className={selectedInstrumentId === instrument.id ? 'dataset active' : 'dataset'} onClick={() => selectInstrument(instrument)}>
              <span>{instrument.label}</span>
              <small>{instrument.providers.join(', ')} - {instrument.timeframes.join(', ')}</small>
            </button>
          ))}
        </div>
      </aside>

      <section className="workspace">
        <section className="control-panel">
          <div className="title-block">
            <h1>{selected?.symbol || 'Instrument explorer'}</h1>
            <p>{selected ? `${selected.provider} - ${selected.marketDataType.toUpperCase()} - ${selected.timeframe} - ${qualityStatus?.overallStatus ?? 'quality unknown'} - Live ${liveEnabled ? liveStatus : 'OFF'}` : 'Select an instrument to inspect market data, ingestion, validation, and metadata.'}</p>
          </div>
          <div className="range-toolbar">
            <div className="preset-row range-presets">
              {(['1H', '6H', '24H', '7D', '30D', 'previous-day', 'today', 'next-day'] as RangePreset[]).map(preset => (
                <button key={preset} type="button" className="secondary" onClick={() => applyRangePreset(preset)}>{rangeLabel(preset)}</button>
              ))}
            </div>
            <DateExpressionInput label="Start" value={start} onChange={setStart} />
            <DateExpressionInput label="End" value={end} onChange={setEnd} />
            <DateExpressionInput label="Version time" value={asOf} onChange={setAsOf} />
            <label><span>Time zone</span><select value={timeZone} onChange={event => setTimeZone(event.target.value)}><option>{defaultTimeZone}</option><option>UTC</option><option>America/New_York</option><option>Europe/London</option></select></label>
            <button disabled={!selected} onClick={() => void loadBars()}>{loading ? 'Loading' : 'Load bars'}</button>
            <button
              type="button"
              className={`live-toggle-button ${liveEnabled ? 'on' : ''}`}
              role="switch"
              aria-checked={liveEnabled}
              disabled={!selected}
              onClick={() => setLiveEnabled(value => !value)}>
              <span className="toggle-track"><span className="toggle-thumb" /></span>
              <span>{liveEnabled ? 'Live ON' : 'Live OFF'}</span>
            </button>
          </div>
        </section>

        {error && <div className="error">{error}</div>}

        <MetadataPanel item={selected} instrument={selectedInstrument} timeZone={timeZone} onSelectSeries={selectSeries} />
        <QualityPanel
          status={qualityStatus}
          findings={findings}
          jobs={validationJobs}
          executions={validationExecutions}
          timeZone={timeZone}
          onRefresh={() => void loadQuality()}
          onConfigure={() => void createStandardValidation()}
          onRun={job => void runValidation(job)}
          onBackfill={(job, range) => void runValidation(job, range)}
          onSetEnabled={(job, enabled) => void setValidationEnabled(job, enabled)}
        />
        <IngestionPanel schedules={schedules} jobs={jobs} executions={executions} timeZone={timeZone} onSave={saveSchedule} onBackload={queueBackload} />

        <MarketChart
          points={points}
          mode={chartMode}
          currency={selected?.currency ?? selected?.quoteAsset ?? ''}
          timeZone={timeZone}
          liveEnabled={liveEnabled}
          liveStatus={liveStatus}
          newBarCount={newBarCount}
          onModeChange={setChartMode}
          onJumpLatest={() => setNewBarCount(0)}
        />

        <PointTable points={points} currency={selected?.currency ?? ''} timeZone={timeZone} />
      </section>
    </main>
  );
}

function toSubscription(item: DatasetMetadata): MarketDataSubscription {
  return {
    datasetId: item.id,
    seriesId: item.seriesId,
    providerId: item.provider,
    symbol: item.symbol,
    marketDataType: item.marketDataType,
    timeframe: item.timeframe
  };
}

function rangeLabel(preset: RangePreset) {
  return preset === 'previous-day' ? '-1D' : preset === 'next-day' ? '+1D' : preset === 'today' ? 'Today' : preset;
}

function isoDurationForTimeframe(timeframe: string) {
  return timeframe === '1m' ? 'PT1M'
    : timeframe === '5m' ? 'PT5M'
    : timeframe === '15m' ? 'PT15M'
    : timeframe === '1h' ? 'PT1H'
    : timeframe === '1d' ? 'P1D'
    : 'PT15M';
}

function pointToEvent(item: DatasetMetadata, point: TimeSeriesPoint): MarketDataUpdatedEvent {
  return {
    eventId: `catch-up-${item.id}-${point.timestamp}`,
    version: 1,
    occurredAt: point.asOf,
    datasetId: item.id,
    seriesId: item.seriesId,
    providerId: item.provider,
    exchange: item.exchange,
    instrumentId: item.providerInstrumentId,
    symbol: item.symbol,
    marketDataType: item.marketDataType,
    timeframe: item.timeframe,
    operation: 'upsert',
    dataTimestamp: point.timestamp,
    asOf: point.asOf,
    bar: point
  };
}

function DateExpressionInput({ label, value, onChange }: { label: string; value: string; onChange: (value: string) => void }) {
  return (
    <label>
      <span>{label}</span>
      <div className="date-expression">
        <input value={value} onChange={event => onChange(event.target.value)} placeholder="now-24h or ISO time" />
        <input aria-label={`${label} picker`} type="datetime-local" value={toPickerInput(value)} onChange={event => onChange(event.target.value)} />
      </div>
    </label>
  );
}

function MetadataPanel({ item, instrument, timeZone, onSelectSeries }: { item: DatasetMetadata | null; instrument?: InstrumentSummary; timeZone: string; onSelectSeries: (item: DatasetMetadata) => void }) {
  if (!item) return <section className="metadata-panel"><h2>Instrument details</h2><p>No instrument selected.</p></section>;
  const rows = [
    ['Provider', item.provider],
    ['Exchange', item.exchange],
    ['Symbol', item.symbol],
    ['Asset class', item.assetClass],
    ['Base asset', item.baseAsset],
    ['Quote asset', item.quoteAsset],
    ['Data type', item.marketDataType],
    ['Timeframe', item.timeframe],
    ['Currency', item.currency],
    ['Calendar', item.calendar],
    ['Series id', item.seriesId],
    ['Provider instrument', item.providerInstrumentId],
    ['First available', item.firstAvailableAt ? formatDate(item.firstAvailableAt, timeZone) : ''],
    ['Last available', item.lastAvailableAt ? formatDate(item.lastAvailableAt, timeZone) : ''],
    ['Last ingested', item.lastIngestedAt ? formatDate(item.lastIngestedAt, timeZone) : '']
  ].filter(([, value]) => value);

  return (
    <section className="metadata-panel">
      <h2>Instrument details</h2>
      <dl>{rows.map(([label, value]) => <React.Fragment key={label}><dt>{label}</dt><dd>{value}</dd></React.Fragment>)}</dl>
      <div className="preset-row">
        {(instrument?.series as DatasetMetadata[] | undefined ?? []).map(series => (
          <button key={series.id} className={series.id === item.id ? '' : 'secondary'} onClick={() => onSelectSeries(series)}>
            {series.timeframe}
          </button>
        ))}
      </div>
    </section>
  );
}

function QualityPanel({
  status,
  findings,
  jobs,
  executions,
  timeZone,
  onRefresh,
  onConfigure,
  onRun,
  onBackfill,
  onSetEnabled
}: {
  status: QualityStatus | null;
  findings: QualityFinding[];
  jobs: QualityValidationJob[];
  executions: QualityExecution[];
  timeZone: string;
  onRefresh: () => void;
  onConfigure: () => void;
  onRun: (job: QualityValidationJob) => void;
  onBackfill: (job: QualityValidationJob, range: { start: string; end: string }) => void;
  onSetEnabled: (job: QualityValidationJob, enabled: boolean) => void;
}) {
  const summary = qualitySummary(findings);
  const [backfillJob, setBackfillJob] = useState<QualityValidationJob | null>(null);
  return (
    <section className="quality-panel">
      <header className="section-heading">
        <div><h2>Data quality</h2><p>{status?.latestExecutionId ? `Last validation ${formatDate(status.asOf, timeZone)}` : jobs.length ? 'Validation configured. No execution has completed yet.' : 'No validation has been configured for this market-data series.'}</p></div>
        <div className="section-actions">
          <StatusBadge status={status?.overallStatus ?? 'unknown'} />
          {!jobs.length && <button type="button" onClick={onConfigure}>Configure validation</button>}
          <button type="button" onClick={onRefresh}>Refresh results</button>
        </div>
      </header>
      <div className="quality-summary-grid">
        <MetricTile label="Freshness" value={summary.freshness || (status?.latestExecutionId ? 'Healthy' : 'Unknown')} />
        <MetricTile label="Gaps" value={summary.gaps.toLocaleString()} />
        <MetricTile label="Duplicates" value={summary.duplicates.toLocaleString()} />
        <MetricTile label="Warnings" value={summary.warnings.toLocaleString()} />
      </div>
      {!!jobs.length && (
        <div className="table-scroll compact">
          <table>
            <thead><tr><th>Configuration</th><th>Schedule</th><th>Validators</th><th>Last run</th><th></th></tr></thead>
            <tbody>{jobs.map(job => {
              const latest = executions.find(execution => execution.jobId === job.id);
              return (
                <tr key={job.id}>
                  <td><strong>{job.name}</strong><small>{job.enabled ? 'Enabled' : 'Disabled'}</small></td>
                  <td><code>{job.cronExpression}</code><small>{job.windowStartExpression} to {job.windowEndExpression}</small></td>
                  <td>{job.checks.filter(check => check.enabled).length}</td>
                  <td>{latest ? <><StatusBadge status={latest.status} /><small>{formatDate(latest.queuedAt, timeZone)}</small></> : 'No executions yet'}</td>
                  <td className="button-row">
                    <button className="table-action" onClick={() => onSetEnabled(job, !job.enabled)}>{job.enabled ? 'Disable' : 'Enable'}</button>
                    <button className="table-action" onClick={() => onRun(job)}>Run now</button>
                    <button className="table-action" onClick={() => setBackfillJob(job)}>Backfill</button>
                  </td>
                </tr>
              );
            })}</tbody>
          </table>
        </div>
      )}
      <div className="table-scroll compact">
        <table>
          <thead><tr><th>Status</th><th>Finding</th><th>Affected range</th><th>Count</th></tr></thead>
          <tbody>{findings.slice(0, 12).map(finding => (
            <tr key={`${finding.validatorId}-${finding.affectedStart ?? ''}`}>
              <td><StatusBadge status={finding.severity} /></td>
              <td><strong>{finding.title}</strong><small>{finding.message}</small></td>
              <td>{formatRange(finding.affectedStart, finding.affectedEnd, timeZone)}</td>
              <td>{finding.affectedCount?.toLocaleString() ?? ''}</td>
            </tr>
          ))}</tbody>
        </table>
        {!findings.length && <p className="empty-state">{status?.latestExecutionId ? 'No active validation findings.' : jobs.length ? 'Validation configured. No execution has completed yet.' : 'No validation has been configured for this market-data series.'}</p>}
      </div>
      {!!executions.length && (
        <div className="table-scroll compact">
          <table>
            <thead><tr><th>Execution</th><th>Window</th><th>Warnings</th><th>Critical</th></tr></thead>
            <tbody>{executions.slice(0, 8).map(execution => (
              <tr key={execution.id}>
                <td><StatusBadge status={execution.status} /><small>{execution.triggerType} - {formatDate(execution.queuedAt, timeZone)}</small></td>
                <td>{formatRange(execution.evaluatedStart, execution.evaluatedEnd, timeZone)}</td>
                <td>{execution.warningCount}</td>
                <td>{execution.criticalCount}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
      {backfillJob && <BackfillModal job={backfillJob} onClose={() => setBackfillJob(null)} onRun={range => { onBackfill(backfillJob, range); setBackfillJob(null); }} />}
    </section>
  );
}

function BackfillModal({ job, onClose, onRun }: { job: QualityValidationJob; onClose: () => void; onRun: (range: { start: string; end: string }) => void }) {
  const [start, setStart] = useState('now-7d');
  const [end, setEnd] = useState('now');
  return (
    <div className="modal-backdrop" role="presentation" onClick={onClose}>
      <div className="modal-panel form-modal" role="dialog" aria-modal="true" onClick={event => event.stopPropagation()}>
        <header className="modal-header"><div><h3>Backfill validation</h3><p>{job.name}</p></div><button className="icon-button" onClick={onClose} aria-label="Close validation backfill">x</button></header>
        <div className="form-grid">
          <DateExpressionInput label="Start" value={start} onChange={setStart} />
          <DateExpressionInput label="End" value={end} onChange={setEnd} />
        </div>
        <footer className="modal-actions"><button className="secondary" onClick={onClose}>Cancel</button><button onClick={() => onRun({ start, end })}>Run backfill</button></footer>
      </div>
    </div>
  );
}

function MetricTile({ label, value }: { label: string; value: string }) {
  return <div className="metric-tile"><span>{label}</span><strong>{value}</strong></div>;
}

function qualitySummary(findings: QualityFinding[]) {
  return {
    freshness: findings.find(x => x.category === 'freshness')?.qualityStatus ?? '',
    gaps: findings.filter(x => x.validatorId === 'completeness.missing-timestamps').reduce((sum, x) => sum + (x.affectedCount ?? 1), 0),
    duplicates: findings.filter(x => x.category === 'duplicates').reduce((sum, x) => sum + (x.affectedCount ?? 1), 0),
    warnings: findings.filter(x => x.severity === 'warning' || x.qualityStatus === 'warning').length
  };
}

function IngestionPanel({ schedules, jobs, executions, timeZone, onSave, onBackload }: { schedules: IngestionSchedule[]; jobs: IngestionJob[]; executions: IngestionExecution[]; timeZone: string; onSave: (schedule: IngestionSchedule) => Promise<void>; onBackload: (schedule: IngestionSchedule) => Promise<void> }) {
  const rows = buildScheduleStatusRows(schedules, jobs, executions);
  const [editing, setEditing] = useState<IngestionSchedule | null>(null);
  return (
    <section className="ingestion-panel">
      <header className="section-heading">
        <div><h2>Ingestion</h2><p>{schedules.length} schedules - {executions[0]?.inserted.toLocaleString() ?? 0} latest inserted</p></div>
        {executions[0] && <StatusBadge status={executions[0].status} />}
      </header>
      <div className="table-scroll compact">
        <table>
          <thead><tr><th>Schedule</th><th>Latest result</th><th>Last queued</th><th>Rows</th><th></th></tr></thead>
          <tbody>{rows.map(row => (
            <tr key={row.id}>
              <td><strong>{row.name}</strong><small>{row.detail}</small></td>
              <td><StatusBadge status={row.displayStatus} /><small>{row.latestJob?.id ?? 'No jobs yet'}</small></td>
              <td>{row.latestJob?.queuedAt ? formatDate(row.latestJob.queuedAt, timeZone) : 'Never'}</td>
              <td>{row.displayExecution ? `${row.displayExecution.inserted.toLocaleString()} inserted - ${row.displayExecution.skipped.toLocaleString()} skipped` : ''}</td>
              <td className="button-row">
                <button className="table-action" onClick={() => setEditing(schedules.find(schedule => schedule.id === row.id) ?? null)}>Edit</button>
                <button className="table-action" onClick={() => {
                  const schedule = schedules.find(item => item.id === row.id);
                  if (schedule) void onBackload(schedule);
                }}>Backload</button>
              </td>
            </tr>
          ))}</tbody>
        </table>
        {!rows.length && <p className="empty-state">No ingestion schedules for this series.</p>}
      </div>
      {editing && <ScheduleModal schedule={editing} onClose={() => setEditing(null)} onSave={async schedule => { await onSave(schedule); setEditing(null); }} />}
      <div hidden>{buildScheduleJobHistoryRows('', jobs, executions).length}</div>
    </section>
  );
}

function ScheduleModal({ schedule, onClose, onSave }: { schedule: IngestionSchedule; onClose: () => void; onSave: (schedule: IngestionSchedule) => Promise<void> }) {
  const [draft, setDraft] = useState(schedule);
  const suggested = suggestCronFromGranularity(draft.parameters.timeframe ?? '');
  return (
    <div className="modal-backdrop" role="presentation" onClick={onClose}>
      <div className="modal-panel form-modal" role="dialog" aria-modal="true" onClick={event => event.stopPropagation()}>
        <header className="modal-header"><div><h3>Edit ingestion</h3><p>{schedule.name}</p></div><button className="icon-button" onClick={onClose} aria-label="Close ingestion editor">x</button></header>
        <div className="form-grid">
          <label className="switch-row"><input type="checkbox" checked={draft.enabled} onChange={event => setDraft({ ...draft, enabled: event.target.checked })} /><span>{draft.enabled ? 'Enabled' : 'Disabled'}</span></label>
          <label><span>Cron</span><input value={draft.cronExpression} onChange={event => setDraft({ ...draft, cronExpression: event.target.value })} />{suggested && <small>Timeframe suggests {suggested}</small>}</label>
          <label><span>Window start</span><input value={draft.windowStartExpression} onChange={event => setDraft({ ...draft, windowStartExpression: event.target.value })} /></label>
          <label><span>Window end</span><input value={draft.windowEndExpression} onChange={event => setDraft({ ...draft, windowEndExpression: event.target.value })} /></label>
          <label><span>Batch size</span><input type="number" min="1" value={draft.batchSize} onChange={event => setDraft({ ...draft, batchSize: Number(event.target.value) })} /></label>
        </div>
        <footer className="modal-actions"><button className="secondary" onClick={onClose}>Cancel</button><button onClick={() => void onSave(draft)}>Save</button></footer>
      </div>
    </div>
  );
}

function PointTable({ points, currency, timeZone }: { points: TimeSeriesPoint[]; currency: string; timeZone: string }) {
  return (
    <section className="table-panel">
      <h2>{points.length} OHLCV bars</h2>
      <div className="table-scroll">
        <table>
          <thead><tr><th>Timestamp</th><th>Open</th><th>High</th><th>Low</th><th>Close</th><th>Volume</th><th>As of</th></tr></thead>
          <tbody>{points.slice(0, 200).map(point => (
            <tr key={`${point.timestamp}-${point.asOf}`}>
              <td>{formatDate(point.timestamp, timeZone)}</td>
              <td>{formatMoney(point.open, currency)}</td>
              <td>{formatMoney(point.high, currency)}</td>
              <td>{formatMoney(point.low, currency)}</td>
              <td>{formatMoney(point.close, currency)}</td>
              <td>{formatNumber(point.volume)}</td>
              <td>{formatDate(point.asOf, timeZone)}</td>
            </tr>
          ))}</tbody>
        </table>
      </div>
    </section>
  );
}

function StatusBadge({ status }: { status: string }) {
  return <span className={`status-badge ${status.toLowerCase()}`}>{status || 'unknown'}</span>;
}

function unique(values: string[]) {
  return Array.from(new Set(values.filter(Boolean))).sort();
}

function formatDate(value: string, timeZone: string) {
  return value ? formatMarketTime(value, timeZone, 48 * 60 * 60 * 1000) : '';
}

function formatRange(start: string | null, end: string | null, timeZone: string) {
  if (!start && !end) return '';
  return `${formatDate(start ?? '', timeZone)}${end ? ` - ${formatDate(end, timeZone)}` : ''}`;
}

function formatMoney(value: number, currency: string) {
  return currency ? `${formatNumber(value)} ${currency}` : formatNumber(value);
}

function formatNumber(value: number) {
  if (Math.abs(value) >= 1000) return value.toFixed(0);
  if (Math.abs(value) >= 1) return value.toFixed(2);
  return value.toPrecision(4);
}

function toPickerInput(value: string) {
  return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value) ? value : '';
}

createRoot(document.getElementById('root')!).render(<App />);
