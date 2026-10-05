import type { MiniTour } from './tours';

/**
 * The builder's help (Mode D v2, U5.4): short task articles, Markdown in the repo — `articles/
 * <lang>/<id>.md`, a `# Title` line then the text. English is the fallback for a language that
 * lacks one.
 */
const FILES = import.meta.glob<string>('./articles/*/*.md', { query: '?raw', import: 'default', eager: true });

/** In reading order, each with the task tour that shows it, when there is one. */
export const ARTICLES: readonly { id: string; tour?: MiniTour }[] = [
  { id: 'sections', tour: 'firstPage' },
  { id: 'editing', tour: 'firstPage' },
  { id: 'pages' },
  { id: 'responsive' },
  { id: 'theme' },
  { id: 'collections', tour: 'content' },
  { id: 'forms' },
  { id: 'state' },
  { id: 'components', tour: 'reusable' },
  { id: 'developer' },
  { id: 'connections' },
  { id: 'publishing', tour: 'publish' },
  { id: 'shortcuts' },
];

export type ArticleId = string;

export interface Article {
  id: ArticleId;
  title: string;
  body: string;
  tour?: MiniTour;
}

export function articles(language: string): Article[] {
  const lang = language.startsWith('cs') ? 'cs' : 'en';
  return ARTICLES.map(({ id, tour }) => {
    const text = FILES[`./articles/${lang}/${id}.md`] ?? FILES[`./articles/en/${id}.md`] ?? `# ${id}\n`;
    const [first, ...rest] = text.split('\n');
    return { id, tour, title: first!.replace(/^#\s*/, ''), body: rest.join('\n').trim() };
  });
}

/** Without case or accents: "nahled" finds "Náhled". */
const fold = (s: string) => s.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();

/** Every word of the query, in the title or the text; titles first. */
export function search(list: Article[], query: string): Article[] {
  const words = fold(query).split(/\s+/).filter(Boolean);
  if (!words.length) return list;
  const hits = list.filter((a) => words.every((w) => fold(`${a.title} ${a.body}`).includes(w)));
  const inTitle = (a: Article) => words.every((w) => fold(a.title).includes(w));
  return [...hits.filter(inTitle), ...hits.filter((a) => !inTitle(a))];
}
