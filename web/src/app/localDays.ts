/**
 * Calendar days as the user sees them. The API returns UTC timestamps that the UI shows in local time, so a day the user
 * picks (yyyy-mm-dd) means that local day and is sent as UTC instants.
 */

/** Today's local date as yyyy-mm-dd, shifted by whole days. */
export function localDate(offsetDays = 0, now = new Date()): string {
  const day = new Date(now.getFullYear(), now.getMonth(), now.getDate() + offsetDays);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${day.getFullYear()}-${pad(day.getMonth() + 1)}-${pad(day.getDate())}`;
}

/** Whether a date input's value is a complete, valid yyyy-mm-dd (browsers allow e.g. a 5-digit year while typing). */
export function isLocalDate(date: string): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(date) && Number.isFinite(Date.parse(`${date}T00:00:00`));
}

/** Local midnight at the start of the day, as an ISO UTC instant. Call only with a valid date ({@link isLocalDate}). */
export function localDayStart(date: string): string {
  return new Date(`${date}T00:00:00`).toISOString();
}

/** The last millisecond of the local day (23 or 25 hours long on daylight-saving days), as an ISO UTC instant. */
export function localDayEnd(date: string): string {
  const next = new Date(`${date}T00:00:00`);
  next.setDate(next.getDate() + 1);
  return new Date(next.getTime() - 1).toISOString();
}
