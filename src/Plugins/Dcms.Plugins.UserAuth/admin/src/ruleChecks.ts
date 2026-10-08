import type { Rule } from './api';

/** The rule the edge would apply to `path`: the first whose prefix covers it (SiteGateRules.Decide). */
export function ruleFor(rules: Rule[], path: string): Rule | undefined {
  const p = path.toLowerCase();
  return rules.find((r) => {
    const prefix = r.prefix.replace(/\/+$/, '').toLowerCase();
    return prefix === '' || p === prefix || p.startsWith(`${prefix}/`);
  });
}

/** Whether everyone `inner` lets in is also let in by `outer` (no rule: public). */
function admitsAll(outer: Rule | undefined, inner: Rule): boolean {
  if (!outer || outer.access === 'public') return true;
  if (inner.access === 'public') return false;
  if (outer.access === 'signedIn') return true;
  return inner.access === 'groups' && inner.groups.length > 0 && inner.groups.every((g) => outer.groups.includes(g));
}

/**
 * A single-page app's code is one bundle under `/assets`, judged by whichever rule covers it. A
 * rule letting in people that rule shuts out gives them the page but not the code that draws it.
 * Returns the rules affected and the `/assets` rule that would fix it — as open as the most open
 * of them — or null when every rule's people can load the app.
 */
export function assetsConflict(rules: Rule[]): { affected: Rule[]; fix: Rule } | null {
  const assets = ruleFor(rules, '/assets/index.js');
  const affected = rules.filter((r) => r !== assets && !admitsAll(assets, r));
  if (affected.length === 0) return null;
  const fix: Rule = affected.some((r) => r.access === 'public')
    ? { prefix: '/assets', access: 'public', groups: [] }
    : affected.some((r) => r.access === 'signedIn')
      ? { prefix: '/assets', access: 'signedIn', groups: [] }
      : { prefix: '/assets', access: 'groups', groups: [...new Set([...(assets?.groups ?? []), ...affected.flatMap((r) => r.groups)])] };
  return { affected, fix };
}

/** Puts `rule` first, replacing any rule with its prefix. */
export function withFirst(rules: Rule[], rule: Rule): Rule[] {
  return [rule, ...rules.filter((r) => r.prefix.replace(/\/+$/, '').toLowerCase() !== rule.prefix.toLowerCase())];
}

/** The plugin instances whose slug a rule's path starts with: /audio-library-1/track reads /api/audio-library-1. */
export function instancesBehind<T extends { slug: string }>(rules: Rule[], instances: T[]): T[] {
  const first = new Set(rules.map((r) => r.prefix.split('/').filter(Boolean)[0]).filter(Boolean));
  return instances.filter((i) => first.has(i.slug));
}
