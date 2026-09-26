import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { VersionHeader } from '../document/api';
import { sinceText } from '../document/historyText';
import { localDate, localDayEnd, localDayStart } from './localDays';

// Node applies a changed TZ at once; each test picks the zone it needs.
const originalTz = process.env.TZ;

describe('local calendar days (times are shown in local time, days picked by the user are local too)', () => {
  beforeEach(() => {
    process.env.TZ = 'Europe/Berlin';
  });

  afterEach(() => {
    process.env.TZ = originalTz;
  });

  it('sends a local day as UTC instants, 23 or 25 hours on daylight-saving days', () => {
    expect(localDayStart('2026-09-26')).toBe('2026-09-25T22:00:00.000Z');
    expect(localDayEnd('2026-09-26')).toBe('2026-09-26T21:59:59.999Z');
    // 25 October: clocks go back, the day ends at 23:00 UTC.
    expect(localDayStart('2026-10-25')).toBe('2026-10-24T22:00:00.000Z');
    expect(localDayEnd('2026-10-25')).toBe('2026-10-25T22:59:59.999Z');
  });

  it('knows today by the local clock', () => {
    // 25 Sep 23:30 UTC is already 26 Sep in Berlin.
    expect(localDate(0, new Date('2026-09-25T23:30:00Z'))).toBe('2026-09-26');
    expect(localDate(-7, new Date('2026-09-25T23:30:00Z'))).toBe('2026-09-19');
  });

  it('names the chosen day in the "changes since" banner west of UTC too', () => {
    process.env.TZ = 'America/New_York';
    expect(sinceText(`d:${localDayStart('2026-09-26')}`, [] as VersionHeader[])).toBe(
      new Date(2026, 8, 26).toLocaleDateString(),
    );
    expect(sinceText(`d:${localDayStart('2026-09-26')}`, [] as VersionHeader[])).toContain('26');
  });
});
