import { describe, expect, it } from 'vitest';
import {
  THEME_CSS,
  isGeneratedPath,
  outputFileName,
  pageCssPath,
  pageHtmlPath,
  slugFromPageCssPath,
  slugFromPageHtmlPath,
  slugFromRoutePath,
  slugify,
  stylesheetsForPage,
  uniqueSlug,
} from './paths';

describe('page paths', () => {
  it('round-trips a slug through the html path', () => {
    expect(slugFromPageHtmlPath(pageHtmlPath('about-us'))).toBe('about-us');
    expect(slugFromPageCssPath(pageCssPath('about-us'))).toBe('about-us');
  });

  it('does not mistake other files for pages', () => {
    expect(slugFromPageHtmlPath('styles/global.css')).toBeNull();
    expect(slugFromPageHtmlPath('pages/nested/deep.html')).toBeNull();
    // The page CSS lives under styles/pages, so a page HTML path is not a CSS path.
    expect(slugFromPageCssPath('pages/home.html')).toBeNull();
  });

  it('orders stylesheets theme -> global -> page', () => {
    expect(stylesheetsForPage('home')).toEqual([
      'styles/theme.css',
      'styles/global.css',
      'styles/pages/home.css',
    ]);
  });

  it('marks only the generated theme file as generated', () => {
    expect(isGeneratedPath(THEME_CSS)).toBe(true);
    expect(isGeneratedPath('styles/global.css')).toBe(false);
    expect(isGeneratedPath('site.json')).toBe(false);
  });
});

describe('slugify', () => {
  it('folds diacritics instead of dropping the word', () => {
    expect(slugify('Příspěvky a články')).toBe('prispevky-a-clanky');
  });

  it('collapses punctuation and trims separators', () => {
    expect(slugify('  Hello, World!  ')).toBe('hello-world');
  });

  it('never returns an empty slug', () => {
    expect(slugify('...')).toBe('page');
    expect(slugify('')).toBe('page');
  });
});

describe('slugFromRoutePath', () => {
  it('maps the root to home', () => {
    expect(slugFromRoutePath('/')).toBe('home');
    expect(slugFromRoutePath('')).toBe('home');
  });

  it('flattens nested routes', () => {
    expect(slugFromRoutePath('/about/team')).toBe('about-team');
  });
});

describe('uniqueSlug', () => {
  it('returns the slug when free', () => {
    expect(uniqueSlug('home', ['about'])).toBe('home');
  });

  it('suffixes until free', () => {
    expect(uniqueSlug('home', ['home', 'home-2'])).toBe('home-3');
  });
});

describe('outputFileName', () => {
  it('matches the published Mode A convention', () => {
    expect(outputFileName('/')).toBe('index.html');
    expect(outputFileName('/about')).toBe('about.html');
    expect(outputFileName('/about/team')).toBe('about_team.html');
  });
});
