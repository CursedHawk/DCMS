import { beforeEach, describe, expect, it } from 'vitest';
import { AGENTS_MD, SITE_JSON, THEME_CSS, renderThemeCss } from '@dcms/gjs-schema';
import { DESIGN_KITS, applyKit } from '@dcms/gjs-blocks';
import { useVfs } from '../site-source/vfs';
import { useBuilder } from './store';

/**
 * What `syncFromVfs` owes the rest of the builder.
 *
 * <p>It is the single path every out-of-band change to the file map comes through — the code
 * view, a git restore, the agent writing `site.json` — so two things have to be true of it that
 * are not true of the canvas's own save path: the derived files are regenerated, and the canvas
 * is told to re-read when what it is showing changed underneath it.</p>
 *
 * <p>Both were only true for a builder edit before. A theme changed by anything else left
 * `styles/theme.css` holding the previous palette, and the publisher links that file rather than
 * deriving it from `site.json` — so the site shipped the old design.</p>
 */

const theme = applyKit(
  { colors: {}, fonts: {}, spacing: {}, text: {}, shadows: {}, metrics: {}, custom: {} },
  DESIGN_KITS[0],
);

function manifest(withTheme = theme) {
  return JSON.stringify({
    version: 2,
    theme: withTheme,
    pages: [{ id: 'home', slug: 'home', path: '/', title: 'Home', seo: { title: 'Home' } }],
  });
}

beforeEach(() => {
  useVfs.setState({ files: {}, rev: 0 });
  useBuilder.setState({ project: null, themeCss: '', reloadToken: 0, capturedFiles: {} });
});

describe('syncFromVfs', () => {
  it('regenerates the theme stylesheet and the authoring contract from site.json', () => {
    useVfs.setState({ files: { [SITE_JSON]: manifest(), 'pages/home.html': '<h1>Hi</h1>' } });

    useBuilder.getState().syncFromVfs();

    // The publisher links styles/theme.css as a file, so this is what actually ships.
    expect(useVfs.getState().files[THEME_CSS]).toBe(renderThemeCss(theme));
    expect(useVfs.getState().files[AGENTS_MD]).toContain('Block catalogue');
  });

  it('reloads the canvas when the theme changed outside it, and not otherwise', () => {
    useVfs.setState({ files: { [SITE_JSON]: manifest(), 'pages/home.html': '<h1>Hi</h1>' } });
    useBuilder.getState().syncFromVfs();
    const settled = useBuilder.getState().reloadToken;

    // Re-syncing the same files is not news.
    useBuilder.getState().syncFromVfs();
    expect(useBuilder.getState().reloadToken).toBe(settled);

    // A different kit, written straight into the file map the way the agent writes it. The
    // canvas holds its own copy of the `:root` block, so it has to be told.
    const other = applyKit(theme, DESIGN_KITS[1]);
    useVfs.setState({
      files: { ...useVfs.getState().files, [SITE_JSON]: manifest(other) },
    });
    useBuilder.getState().syncFromVfs();

    expect(useBuilder.getState().reloadToken).toBeGreaterThan(settled);
    expect(useVfs.getState().files[THEME_CSS]).toBe(renderThemeCss(other));
  });

  it('does not treat the first load as drift', () => {
    useVfs.setState({ files: { [SITE_JSON]: manifest(), 'pages/home.html': '<h1>Hi</h1>' } });

    useBuilder.getState().syncFromVfs();

    // Opening a site is not a change to it; the canvas is about to read everything anyway.
    expect(useBuilder.getState().reloadToken).toBe(0);
  });
});
