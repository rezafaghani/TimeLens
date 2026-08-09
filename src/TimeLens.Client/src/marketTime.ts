export type RangePreset = '1H' | '6H' | '24H' | '7D' | '30D' | 'previous-day' | 'today' | 'next-day';

export function formatMarketTime(value: Date | string | number, timeZone: string, spanMs: number) {
  const date = value instanceof Date ? value : new Date(value);
  const options: Intl.DateTimeFormatOptions = spanMs <= 36 * 60 * 60 * 1000
    ? { timeZone, hour: '2-digit', minute: '2-digit', hour12: false }
    : spanMs <= 45 * 24 * 60 * 60 * 1000
      ? { timeZone, day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }
      : { timeZone, day: '2-digit', month: 'short', year: '2-digit' };
  return new Intl.DateTimeFormat('en-GB', options).format(date).replace(',', '');
}

export function formatTooltipTime(value: Date | string | number, timeZone: string) {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false
  }).format(value instanceof Date ? value : new Date(value)).replace(',', '');
}

export function rangeForPreset(preset: RangePreset, now = new Date()) {
  const hour = 60 * 60 * 1000;
  const day = 24 * hour;
  if (preset === '1H') return [new Date(now.getTime() - hour), now] as const;
  if (preset === '6H') return [new Date(now.getTime() - 6 * hour), now] as const;
  if (preset === '24H') return [new Date(now.getTime() - day), now] as const;
  if (preset === '7D') return [new Date(now.getTime() - 7 * day), now] as const;
  if (preset === '30D') return [new Date(now.getTime() - 30 * day), now] as const;

  const localMidnight = new Date(now);
  localMidnight.setHours(0, 0, 0, 0);
  if (preset === 'previous-day') return [new Date(localMidnight.getTime() - day), localMidnight] as const;
  if (preset === 'next-day') return [new Date(localMidnight.getTime() + day), new Date(localMidnight.getTime() + 2 * day)] as const;
  return [localMidnight, new Date(localMidnight.getTime() + day)] as const;
}

export function toLocalInput(date: Date) {
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}
