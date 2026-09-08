import { describe, expect, it } from 'vitest';
import { formatOf, normalizeRich, shouldAdopt } from './richText';

describe('formatOf', () => {
  it('keeps a Markdown field as source text', () => {
    expect(formatOf('Markdown')).toBe('markdown');
  });

  it('stores RichText as markup, which is what a template binds', () => {
    expect(formatOf('RichText')).toBe('html');
  });
});

describe('normalizeRich', () => {
  it.each(['<p></p>', '<p><br></p>', '<p>  </p>', '<p>&nbsp;</p>', '', '   '])(
    'treats %s as empty',
    (value) => {
      expect(normalizeRich(value)).toBe('');
    },
  );

  it('leaves real content exactly as the editor serialised it', () => {
    const html = '<p>Doors at <strong>8</strong>.</p>';
    expect(normalizeRich(html)).toBe(html);
  });

  it('keeps content that only looks empty at the start', () => {
    const html = '<p></p><p>Second paragraph.</p>';
    expect(normalizeRich(html)).toBe(html);
  });
});

describe('shouldAdopt', () => {
  it('loads an initial value', () => {
    expect(shouldAdopt('<p>Hello</p>', null)).toBe(true);
  });

  it('does not reload an editor that starts empty', () => {
    expect(shouldAdopt('', null)).toBe(false);
  });

  it('ignores the value it just emitted, which is what stops the caret jumping', () => {
    expect(shouldAdopt('<p>Typed</p>', '<p>Typed</p>')).toBe(false);
  });

  it('adopts a value that changed somewhere else', () => {
    expect(shouldAdopt('<p>From elsewhere</p>', '<p>Typed</p>')).toBe(true);
  });
});
