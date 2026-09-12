import { describe, expect, it } from 'vitest';
import { buildIndex, findDefinitions, findImporters, summarize, updateIndex } from './projectIndex';

const SITE = {
  'package.json': JSON.stringify({
    dependencies: { react: '^19.0.0' },
    devDependencies: { vite: '^6.0.0' },
  }),
  'src/main.tsx': `import React from 'react';\nimport App from './App';\n`,
  'src/App.tsx': `import { Hero } from './components/Hero';
import { Routes, Route } from 'react-router';

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Hero />} />
      <Route path="/about" element={<About />} />
    </Routes>
  );
}`,
  'src/components/Hero.tsx': `import './hero.css';

export const Hero = () => <section className="hero">hi</section>;

export interface HeroProps { title: string }

function helper() { return 1 }`,
  'src/components/hero.css': `.hero { color: var(--brand) }
.hero-title, .hero__sub { margin: 0 }
:root { --brand: red; --space-1: 4px }`,
  'src/util.ts': `export const clamp = (n: number) => n;\nexport class Box {}\n`,
  'logo.png': 'base64data',
};

describe('buildIndex', () => {
  const index = buildIndex(SITE);

  it('finds the entry point', () => {
    expect(index.entry).toBe('src/main.tsx');
  });

  it('merges dependencies and devDependencies', () => {
    expect(index.dependencies).toEqual({ react: '^19.0.0', vite: '^6.0.0' });
  });

  it('collects route paths from JSX router config', () => {
    expect(index.routes).toEqual(['/', '/about']);
  });

  it('indexes imports including bare and relative specifiers', () => {
    expect(index.files['src/App.tsx'].imports).toEqual(['./components/Hero', 'react-router']);
  });

  it('records definitions with 1-based lines', () => {
    expect(findDefinitions(index, 'Hero')).toEqual([
      { name: 'Hero', path: 'src/components/Hero.tsx', line: 3, kind: 'component', exported: true },
    ]);
  });

  it('classifies a capitalised definition in a JSX file as a component', () => {
    expect(index.files['src/components/Hero.tsx'].components).toEqual(['Hero']);
  });

  it('does not call a lowercase function a component', () => {
    expect(findDefinitions(index, 'helper')[0]).toMatchObject({
      kind: 'function',
      exported: false,
    });
  });

  it('does not call a capitalised symbol in a JSX-free file a component', () => {
    expect(findDefinitions(index, 'Box')[0].kind).toBe('class');
    expect(index.files['src/util.ts'].components).toEqual([]);
  });

  it('records types separately from values', () => {
    expect(findDefinitions(index, 'HeroProps')[0]).toMatchObject({ kind: 'type', exported: true });
  });

  it('marks a default-exported function as exported', () => {
    expect(findDefinitions(index, 'App')[0]).toMatchObject({ exported: true, kind: 'component' });
  });

  it('extracts css classes and custom properties', () => {
    const css = index.files['src/components/hero.css'];
    expect(css.cssClasses).toEqual(['hero', 'hero-title', 'hero__sub']);
    expect(css.cssVars).toEqual(['--brand', '--space-1']);
  });

  it('skips binary files', () => {
    expect(index.files['logo.png'].definitions).toEqual([]);
  });

  it('survives an unparseable package.json rather than throwing', () => {
    // Routine while the agent is midway through editing it.
    expect(buildIndex({ 'package.json': '{ "dependencies": ' }).dependencies).toEqual({});
  });
});

describe('findImporters', () => {
  const index = buildIndex(SITE);

  it('resolves a bare specifier directly', () => {
    expect(findImporters(index, 'react-router')).toEqual(['src/App.tsx']);
  });

  it('resolves a local file through its relative specifiers', () => {
    expect(findImporters(index, 'src/components/Hero.tsx')).toEqual(['src/App.tsx']);
  });

  it('returns nothing for a module nobody imports', () => {
    expect(findImporters(index, 'lodash')).toEqual([]);
  });
});

describe('updateIndex', () => {
  it('reflects a changed file without a full rebuild', () => {
    const index = buildIndex(SITE);
    updateIndex(index, 'src/components/Hero.tsx', `export const Banner = () => <div>x</div>;`);
    expect(findDefinitions(index, 'Hero')).toEqual([]);
    expect(findDefinitions(index, 'Banner')).toHaveLength(1);
  });

  it('drops a deleted file from every derived map', () => {
    const index = buildIndex(SITE);
    updateIndex(index, 'src/App.tsx', null);
    expect(index.files['src/App.tsx']).toBeUndefined();
    expect(index.routes).toEqual([]);
    expect(findImporters(index, 'react-router')).toEqual([]);
  });

  it('re-reads dependencies when package.json changes', () => {
    const index = buildIndex(SITE);
    updateIndex(index, 'package.json', JSON.stringify({ dependencies: { zustand: '^5' } }));
    expect(index.dependencies).toEqual({ zustand: '^5' });
  });

  it('clears the entry when the entry file is deleted', () => {
    const index = buildIndex(SITE);
    updateIndex(index, 'src/main.tsx', null);
    // Falls back down the conventional list rather than going null while App.tsx still exists.
    expect(index.entry).toBe('src/App.tsx');
  });

  it('does not accumulate duplicate importers on repeated updates', () => {
    const index = buildIndex(SITE);
    const content = SITE['src/App.tsx'];
    updateIndex(index, 'src/App.tsx', content);
    updateIndex(index, 'src/App.tsx', content);
    expect(findImporters(index, 'react-router')).toEqual(['src/App.tsx']);
  });
});

describe('summarize', () => {
  it('states the project in a few lines instead of a file list', () => {
    const text = summarize(buildIndex(SITE));
    expect(text).toContain('7 files, entry src/main.tsx');
    expect(text).toContain('dependencies: react, vite');
    expect(text).toContain('routes: / /about');
    expect(text).toContain('components: App, Hero');
  });

  it('caps the component list so a big site cannot blow the budget', () => {
    const files: Record<string, string> = {};
    for (let i = 0; i < 30; i++) {
      files[`src/C${i}.tsx`] = `export const C${i} = () => <div/>;`;
    }
    expect(summarize(buildIndex(files))).toContain('+10 more');
  });
});

describe('performance', () => {
  it('indexes a 200-file project well inside the 300ms budget', () => {
    const files: Record<string, string> = {};
    for (let i = 0; i < 200; i++) {
      files[`src/mod${i}.tsx`] =
        `import { a } from './a';\nexport const Comp${i} = () => <div className="c">x</div>;\n`.repeat(
          10,
        );
    }
    const started = performance.now();
    buildIndex(files);
    expect(performance.now() - started).toBeLessThan(300);
  });
});
