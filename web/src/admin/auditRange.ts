/** The audit API needs `from` and `to` at most 31 days apart. */
const MaxRangeDays = 31;

export function rangeError(from: string, to: string): string | null {
  if (!from || !to) {
    return 'Choose a date range (at most 31 days).';
  }

  // Calendar days (UTC dates): a daylight-saving change must not make 31 days count as 30.96.
  const utc = (date: string) => Date.parse(`${date}T00:00:00Z`);
  const days = (utc(to) - utc(from)) / 86_400_000;
  return days < 0 ? '"From" is after "To".' : days > MaxRangeDays - 1 ? 'The date range can be at most 31 days.' : null;
}
