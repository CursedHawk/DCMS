import { describe, expect, it } from 'vitest';
import { expandImportGlobs, matchGlob } from './importGlob';

const PATHS = [
  'dcms/app.json',
  'dcms/pages/home.json',
  'dcms/components/card/v1.json',
  'dcms/readme.md',
  'src/main.tsx',
  'src/components/countdown.tsx',
  'src/components/nested/x.tsx',
];

describe('import.meta.glob in the preview bundler', () => {
  it('matches * within a segment and ** across them, keyed as written', () => {
    expect(matchGlob('../dcms/**/*.json', 'src/main.tsx', PATHS).map((m) => m.key)).toEqual([
      '../dcms/app.json',
      '../dcms/components/card/v1.json',
      '../dcms/pages/home.json',
    ]);
    expect(matchGlob('./components/*.tsx', 'src/main.tsx', PATHS).map((m) => m.key)).toEqual(['./components/countdown.tsx']);
  });

  it('expands an eager default-import glob into imports and an object of their defaults', () => {
    const code = "const docs = load(import.meta.glob('./components/*.tsx', { eager: true, import: 'default' }));";
    const out = expandImportGlobs(code, 'src/main.tsx', PATHS);
    expect(out).toBe(
      'import * as __dcms_glob_0 from "./components/countdown.tsx";\n' +
        'const docs = load(({ "./components/countdown.tsx": __dcms_glob_0.default }));',
    );
  });

  it('leaves code without a glob untouched, and never matches outside the project', () => {
    expect(expandImportGlobs('const a = 1;', 'src/main.tsx', PATHS)).toBe('const a = 1;');
    expect(matchGlob('../../etc/*.json', 'src/main.tsx', PATHS)).toEqual([]);
  });
});
