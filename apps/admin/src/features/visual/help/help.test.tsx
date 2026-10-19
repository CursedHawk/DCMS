import { describe, expect, it } from 'vitest';
import { ARTICLES, articles, search } from './articles';

describe('help articles', () => {
  it('every article has a title and text in English and Czech', () => {
    for (const lang of ['en', 'cs']) {
      const list = articles(lang);
      expect(list).toHaveLength(ARTICLES.length);
      for (const a of list) {
        expect(a.title, `${lang}/${a.id}`).not.toBe(a.id);
        expect(a.body.length, `${lang}/${a.id}`).toBeGreaterThan(100);
      }
    }
    // Czech is really Czech, not the English fallback.
    expect(articles('cs')[0]!.title).not.toBe(articles('en')[0]!.title);
  });

  it('search ignores case and accents and puts title matches first', () => {
    const list = articles('cs');
    expect(search(list, 'NAHLED').length).toBeGreaterThan(0);
    expect(search(articles('en'), 'phone')[0]!.id).toBe('responsive');
    expect(search(list, '')).toEqual(list);
  });
});
