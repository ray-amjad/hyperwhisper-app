const DATE_OPTIONS: Intl.DateTimeFormatOptions = {
  year: "numeric",
  month: "short",
  day: "numeric",
};

const DATE_TIME_OPTIONS: Intl.DateTimeFormatOptions = {
  ...DATE_OPTIONS,
  hour: "2-digit",
  minute: "2-digit",
};

/** Accepts a Date, ISO string, or Unix timestamp in seconds. */
export function formatDate(
  date: string | Date | number,
  locale?: string,
): string {
  const d = typeof date === "number" ? new Date(date * 1000) : new Date(date);
  return d.toLocaleDateString(locale, DATE_OPTIONS);
}

export function formatDateTime(
  date: string | Date,
  locale?: string,
): string {
  return new Date(date).toLocaleString(locale, DATE_TIME_OPTIONS);
}

/** Long-form, UTC-anchored date for statically-generated content (blog). Falls back to the raw string on an invalid date. */
export function formatLongUTCDate(dateString: string, locale = "en"): string {
  const date = new Date(dateString);
  if (Number.isNaN(date.getTime())) return dateString;
  return new Intl.DateTimeFormat(locale, {
    year: "numeric",
    month: "long",
    day: "numeric",
    timeZone: "UTC",
  }).format(date);
}
