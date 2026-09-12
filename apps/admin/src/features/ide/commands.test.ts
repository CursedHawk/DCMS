import { describe, expect, it } from 'vitest';
import {
  fuzzyScore,
  highlightRuns,
  modifierLabel,
  rankCommands,
  rankPaths,
  type IdeCommand,
} from './commands';

const FILES = [
  'package.json',
  'src/App.tsx',
  'src/api/client.tsx',
  'src/api/openapi.json',
  'src/app/components/Header.tsx',
  'src/components/ui/button.tsx',
  'src/main.tsx',
  'vite.config.ts',
];

describe('fuzzyScore', () => {
  it('matches a subsequence, in order', () => {
    expect(fuzzyScore('src/api/client.tsx', 'acx')).not.toBeNull();
    expect(fuzzyScore('src/api/client.tsx', 'xca')).toBeNull();
  });

  it('reports where it matched, ascending', () => {
    const hit = fuzzyScore('button.tsx', 'btn');
    expect(hit?.positions).toEqual([0, 2, 5]);
  });

  it('scores an empty query as a match with nothing highlighted', () => {
    expect(fuzzyScore('anything', '')).toEqual({ score: 0, positions: [] });
  });

  it('is case-insensitive but reports positions in the original', () => {
    const hit = fuzzyScore('App.tsx', 'app');
    expect(hit?.positions).toEqual([0, 1, 2]);
  });

  it('prefers a consecutive run over scattered characters', () => {
    const run = fuzzyScore('client.tsx', 'cli')!;
    const scattered = fuzzyScore('cxxlxxixxent.tsx', 'cli')!;
    expect(run.score).toBeGreaterThan(scattered.score);
  });

  it('prefers segment starts, which is what makes initials work', () => {
    const initials = fuzzyScore('api/client/index.ts', 'aci')!;
    const middle = fuzzyScore('xxapixxclientxxindex', 'aci')!;
    expect(initials.score).toBeGreaterThan(middle.score);
  });
});

describe('rankPaths', () => {
  it('finds a file by initials across its name', () => {
    expect(rankPaths(FILES, 'clt')[0].item).toBe('src/api/client.tsx');
  });

  /*
   * The behaviour a file finder lives or dies by. Typing "app" while `src/app/` exists must not
   * bury `App.tsx` under every file in that folder: people search for a file by its name, and
   * the folder is how they narrow afterwards.
   */
  it('ranks a name match above every path match', () => {
    const ranked = rankPaths(FILES, 'app');
    expect(ranked[0].item).toBe('src/App.tsx');
    expect(ranked.map((r) => r.item)).toContain('src/app/components/Header.tsx');
  });

  it('still finds a file by its folder', () => {
    expect(rankPaths(FILES, 'components/header').map((r) => r.item)).toEqual([
      'src/app/components/Header.tsx',
    ]);
  });

  it('returns everything, alphabetically, for an empty query', () => {
    const ranked = rankPaths(FILES, '   ');
    expect(ranked).toHaveLength(FILES.length);
    expect(ranked[0].item).toBe('package.json');
  });

  it('drops what does not match at all', () => {
    expect(rankPaths(FILES, 'zzz')).toEqual([]);
  });

  it('honours the limit, so a large project cannot render ten thousand rows', () => {
    expect(rankPaths(FILES, '', 3)).toHaveLength(3);
  });

  it('highlights inside the full path, not inside the basename it matched', () => {
    const [hit] = rankPaths(['src/api/client.tsx'], 'client');
    expect(hit.positions[0]).toBe('src/api/'.length);
  });
});

describe('rankCommands', () => {
  const commands: IdeCommand[] = [
    { id: 'scm', label: 'Source control', keywords: 'git commit branch', run: () => {} },
    { id: 'problems', label: 'Problems', keywords: 'errors warnings', run: () => {} },
    { id: 'preview', label: 'Toggle preview', run: () => {} },
  ];

  it('lists everything for an empty query, in the order given', () => {
    expect(rankCommands(commands, '').map((r) => r.item.id)).toEqual([
      'scm',
      'problems',
      'preview',
    ]);
  });

  it('matches the label', () => {
    expect(rankCommands(commands, 'prev').map((r) => r.item.id)).toEqual(['preview']);
  });

  /*
   * The name on the button is often not the name in someone's head. Somebody who wants Source
   * Control types "git"; without keywords the palette is a memory test.
   */
  it('matches a keyword the label does not contain', () => {
    expect(rankCommands(commands, 'git').map((r) => r.item.id)).toEqual(['scm']);
    expect(rankCommands(commands, 'errors').map((r) => r.item.id)).toEqual(['problems']);
  });

  it('does not highlight a keyword hit, because the reader is not shown the keywords', () => {
    expect(rankCommands(commands, 'git')[0].positions).toEqual([]);
  });

  it('ranks a label match above a keyword match', () => {
    const ranked = rankCommands(
      [
        { id: 'a', label: 'Deployments', keywords: 'prev', run: () => {} },
        { id: 'b', label: 'Toggle preview', run: () => {} },
      ],
      'prev',
    );
    expect(ranked.map((r) => r.item.id)).toEqual(['b', 'a']);
  });
});

describe('highlightRuns', () => {
  it('joins adjacent matches into one run', () => {
    expect(highlightRuns('client', [0, 1, 2])).toEqual([
      { text: 'cli', hit: true },
      { text: 'ent', hit: false },
    ]);
  });

  it('splits a gap', () => {
    expect(highlightRuns('abc', [0, 2])).toEqual([
      { text: 'a', hit: true },
      { text: 'b', hit: false },
      { text: 'c', hit: true },
    ]);
  });

  it('returns the whole string as one unmatched run when nothing matched', () => {
    expect(highlightRuns('abc', [])).toEqual([{ text: 'abc', hit: false }]);
  });
});

describe('modifierLabel', () => {
  it('is the command glyph on a Mac and Ctrl elsewhere', () => {
    expect(modifierLabel('MacIntel')).toBe('⌘');
    expect(modifierLabel('Linux x86_64')).toBe('Ctrl');
    expect(modifierLabel('Win32')).toBe('Ctrl');
  });
});
