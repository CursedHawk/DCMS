import { describe, expect, it } from 'vitest';
import { classify } from './classify';

describe('classify', () => {
  it('treats a short specific change as trivial and skips planning', () => {
    // The failure this prevents: answering "change the button text" with a comprehensive plan.
    for (const task of [
      'change the button text to Buy now',
      'rename Hero to Banner',
      'fix the typo in the footer',
      'set the heading colour to blue',
    ]) {
      expect(classify(task)).toMatchObject({ complexity: 'trivial', plan: false });
    }
  });

  it('plans for a change that spans the project', () => {
    for (const task of [
      'refactor the routing',
      'migrate every page to the new layout',
      'rewrite the architecture of the data layer',
      'update the spacing across all components',
    ]) {
      expect(classify(task)).toMatchObject({ complexity: 'complex', plan: true });
    }
  });

  it('plans for an open-ended request even when it sounds small', () => {
    // "make the hero nicer" is one file and a dozen judgement calls; diving in produces work
    // the author never asked for.
    expect(classify('make the hero look better')).toMatchObject({
      complexity: 'complex',
      plan: true,
    });
  });

  it('plans for a long multi-part request whatever verbs it uses', () => {
    const long = `add ${'a section about our team and '.repeat(12)} finish`;
    expect(classify(long)).toMatchObject({ complexity: 'complex', plan: true });
  });

  it('does not plan over an empty workspace', () => {
    // Whatever was asked, it starts by scaffolding, and planning a scaffold is ceremony.
    expect(classify('build me a portfolio site', { fileCount: 0 })).toMatchObject({
      plan: false,
    });
  });

  it('falls back to normal for an ordinary request', () => {
    expect(classify('add an About page with a nav link', { fileCount: 12 })).toMatchObject({
      complexity: 'normal',
      plan: false,
    });
  });

  it('separates a small verb on a structural noun from a genuine one-liner', () => {
    // Same verb, different size of work: a class on a button is one edit; a page is a file, a
    // route and a nav entry.
    expect(classify('add a class to the button')).toMatchObject({ complexity: 'trivial' });
    expect(classify('add a contact form')).toMatchObject({ complexity: 'normal' });
    expect(classify('add a pricing section')).toMatchObject({ complexity: 'normal' });
  });

  it('always explains itself', () => {
    expect(classify('rename x').because).toBeTruthy();
    expect(classify('refactor everything').because).toBeTruthy();
  });
});
