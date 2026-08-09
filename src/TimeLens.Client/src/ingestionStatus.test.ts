import { describe, expect, it } from 'vitest';
import { buildJobHistoryRows, buildScheduleJobHistoryRows, buildScheduleStatusRows, suggestCronFromGranularity, type IngestionExecution, type IngestionJob, type IngestionSchedule } from './ingestionStatus';

describe('ingestion status rows', () => {
  it('connects each schedule to the latest job and latest execution', () => {
    const rows = buildScheduleStatusRows(
      [schedule('schedule-1')],
      [
        job('job-old', 'schedule-1', 'queued', '2026-01-01T00:00:00Z'),
        job('job-new', 'schedule-1', 'completed', '2026-01-02T00:00:00Z')
      ],
      [
        execution('execution-old', 'job-new', 'running', '2026-01-02T00:01:00Z'),
        execution('execution-new', 'job-new', 'completed', '2026-01-02T00:02:00Z')
      ]
    );

    expect(rows).toHaveLength(1);
    expect(rows[0].latestJob?.id).toBe('job-new');
    expect(rows[0].latestExecution).toMatchObject({
      id: 'execution-new',
      status: 'completed'
    });
    expect(rows[0].displayStatus).toBe('completed');
  });

  it('shows working when a completed schedule is running again', () => {
    const rows = buildScheduleStatusRows(
      [schedule('schedule-1')],
      [
        job('job-complete', 'schedule-1', 'completed', '2026-01-01T00:00:00Z'),
        job('job-running', 'schedule-1', 'running', '2026-01-02T00:00:00Z')
      ],
      [
        execution('execution-complete', 'job-complete', 'completed', '2026-01-01T00:01:00Z'),
        execution('execution-running', 'job-running', 'running', '2026-01-02T00:01:00Z')
      ]
    );

    expect(rows[0].displayStatus).toBe('working');
    expect(rows[0].displayExecution?.id).toBe('execution-complete');
  });

  it('shows retrying when a failed schedule is running again', () => {
    const rows = buildScheduleStatusRows(
      [schedule('schedule-1')],
      [
        job('job-failed', 'schedule-1', 'failed', '2026-01-01T00:00:00Z'),
        job('job-running', 'schedule-1', 'running', '2026-01-02T00:00:00Z')
      ],
      [
        execution('execution-failed', 'job-failed', 'failed', '2026-01-01T00:01:00Z'),
        execution('execution-running', 'job-running', 'running', '2026-01-02T00:01:00Z')
      ]
    );

    expect(rows[0].displayStatus).toBe('retrying');
    expect(rows[0].displayExecution?.id).toBe('execution-failed');
  });

  it('builds job history newest first with latest execution result', () => {
    const rows = buildJobHistoryRows(
      [
        job('job-old', 'schedule-1', 'completed', '2026-01-01T00:00:00Z'),
        job('job-new', 'schedule-1', 'failed', '2026-01-02T00:00:00Z')
      ],
      [
        execution('execution-old', 'job-new', 'running', '2026-01-02T00:01:00Z'),
        execution('execution-new', 'job-new', 'failed', '2026-01-02T00:02:00Z')
      ]
    );

    expect(rows.map(row => row.id)).toEqual(['job-new', 'job-old']);
    expect(rows[0].latestExecution?.id).toBe('execution-new');
  });

  it('filters job history to one schedule', () => {
    const rows = buildScheduleJobHistoryRows(
      'schedule-2',
      [
        job('job-1', 'schedule-1', 'completed', '2026-01-01T00:00:00Z'),
        job('job-2', 'schedule-2', 'completed', '2026-01-02T00:00:00Z')
      ],
      [
        execution('execution-1', 'job-1', 'completed', '2026-01-01T00:01:00Z', 'schedule-1'),
        execution('execution-2', 'job-2', 'completed', '2026-01-02T00:01:00Z', 'schedule-2')
      ]
    );

    expect(rows).toHaveLength(1);
    expect(rows[0].id).toBe('job-2');
    expect(rows[0].latestExecution?.id).toBe('execution-2');
  });

  it('suggests cron from dataset granularity', () => {
    expect(suggestCronFromGranularity('15min')).toBe('*/15 * * * *');
    expect(suggestCronFromGranularity('hourly')).toBe('0 * * * *');
    expect(suggestCronFromGranularity('daily')).toBe('0 3 * * *');
  });
});

function schedule(id: string): IngestionSchedule {
  return {
    id,
    seriesId: 'series-1',
    name: 'BTC-USD 1m',
    cronExpression: '*/15 * * * *',
    defaultCronExpression: '*/15 * * * *',
    enabled: true,
    source: 'coinbase-exchange',
    endpoint: 'products/candles',
    parameters: {},
    lookbackHours: 48,
    windowStartExpression: 'now-48h',
    windowEndExpression: 'now',
    defaultWindowStartExpression: 'now-48h',
    defaultWindowEndExpression: 'now',
    batchSize: 500,
    lastQueuedAt: '2026-01-02T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z'
  };
}

function job(id: string, scheduleId: string, status: string, queuedAt: string): IngestionJob {
  return {
    id,
    scheduleId,
    seriesId: 'series-1',
    status,
    queuedAt,
    error: ''
  };
}

function execution(id: string, jobId: string, status: string, createdAt: string, scheduleId = 'schedule-1'): IngestionExecution {
  return {
    id,
    jobId,
    scheduleId,
    seriesId: 'series-1',
    status,
    createdAt,
    inserted: status === 'completed' ? 10 : 0,
    skipped: 2,
    error: status === 'failed' ? 'failed' : ''
  };
}
