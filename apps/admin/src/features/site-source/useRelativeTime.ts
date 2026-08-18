import { useTranslation } from 'react-i18next';

// A locale-aware "2 hours ago" formatter, no dependency. Falls back to empty for
// missing timestamps. Shared by the Source Control history and the Deployments list.
export function useRelativeTime() {
  const { i18n } = useTranslation();
  return (iso?: string | null): string => {
    if (!iso) return '';
    const then = new Date(iso).getTime();
    if (Number.isNaN(then)) return '';
    const diff = then - Date.now();
    const abs = Math.abs(diff);
    const rtf = new Intl.RelativeTimeFormat(i18n.language, { numeric: 'auto' });
    const units: [Intl.RelativeTimeFormatUnit, number][] = [
      ['year', 31_536_000_000],
      ['month', 2_592_000_000],
      ['week', 604_800_000],
      ['day', 86_400_000],
      ['hour', 3_600_000],
      ['minute', 60_000],
      ['second', 1000],
    ];
    for (const [unit, ms] of units) {
      if (abs >= ms || unit === 'second') return rtf.format(Math.round(diff / ms), unit);
    }
    return '';
  };
}
