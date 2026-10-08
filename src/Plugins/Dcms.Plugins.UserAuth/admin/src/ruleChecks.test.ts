import { describe, expect, it } from 'vitest';
import type { Rule } from './api';
import { assetsConflict, instancesBehind, ruleFor, withFirst } from './ruleChecks';

const rule = (prefix: string, access: Rule['access'], groups: string[] = []): Rule => ({ prefix, access, groups });

describe('ruleFor', () => {
  it('takes the first covering rule, as the edge does', () => {
    const rules = [rule('/portal/news', 'public'), rule('/portal', 'signedIn'), rule('/', 'groups', ['a'])];
    expect(ruleFor(rules, '/portal/news/x')).toBe(rules[0]);
    expect(ruleFor(rules, '/Portal')).toBe(rules[1]);
    expect(ruleFor(rules, '/portals')).toBe(rules[2]);
    expect(ruleFor([], '/x')).toBeUndefined();
  });
});

describe('assetsConflict', () => {
  it('flags a group the app-wide rule shuts out of the bundle (the test-react case)', () => {
    const rules = [rule('/audio-library-1/track', 'groups', ['read']), rule('/', 'groups', ['admin'])];
    const conflict = assetsConflict(rules);
    expect(conflict?.affected).toEqual([rules[0]]);
    expect(conflict?.fix).toEqual(rule('/assets', 'groups', ['admin', 'read']));
  });

  it('flags a public page under a signed-in app, and fixes it with public assets', () => {
    const conflict = assetsConflict([rule('/login', 'public'), rule('/', 'signedIn')]);
    expect(conflict?.fix).toEqual(rule('/assets', 'public'));
  });

  it('is quiet when everyone let in can load the app', () => {
    expect(assetsConflict([rule('/admin', 'groups', ['a']), rule('/', 'signedIn')])).toBeNull();
    expect(assetsConflict([rule('/portal', 'groups', ['a'])])).toBeNull();
    expect(assetsConflict([])).toBeNull();
  });

  it('is fixed by its own fix', () => {
    const rules = [rule('/x', 'groups', ['read']), rule('/', 'groups', ['admin'])];
    expect(assetsConflict(withFirst(rules, assetsConflict(rules)!.fix))).toBeNull();
  });
});

describe('instancesBehind', () => {
  it('matches a rule path to the instance whose API serves it', () => {
    const instances = [{ slug: 'audio-library-1' }, { slug: 'news' }];
    expect(instancesBehind([rule('/audio-library-1/track', 'groups', ['r']), rule('/', 'signedIn')], instances)).toEqual([instances[0]]);
  });
});
