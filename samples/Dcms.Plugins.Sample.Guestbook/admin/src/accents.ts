/** The accent colours a guestbook can use: a strong ribbon and a pale paper, readable in both themes. */
export const ACCENTS = {
  amber: { ribbon: '#b45309', paper: 'rgba(245, 158, 11, 0.08)' },
  rose: { ribbon: '#be123c', paper: 'rgba(244, 63, 94, 0.07)' },
  teal: { ribbon: '#0f766e', paper: 'rgba(20, 184, 166, 0.08)' },
  indigo: { ribbon: '#4338ca', paper: 'rgba(99, 102, 241, 0.08)' },
  slate: { ribbon: '#475569', paper: 'rgba(100, 116, 139, 0.08)' },
} as const;

export type Accent = keyof typeof ACCENTS;

export const accentOf = (value: unknown): Accent =>
  typeof value === 'string' && value in ACCENTS ? (value as Accent) : 'amber';
