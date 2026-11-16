import { describe, expect, it } from 'vitest';
import { band, linkParts, pageSpeedUrl } from './audits';

describe('site audits', () => {
  it('bands a score as Lighthouse does', () => {
    expect([band(100), band(90), band(89), band(50), band(49), band(0), band(null)]).toEqual([
      'good', 'good', 'average', 'average', 'poor', 'poor', 'none',
    ]);
  });

  it('splits a description into text and its links, and nothing but http(s) becomes a link', () => {
    expect(linkParts('Resources block the first paint. [Learn more](https://developer.chrome.com/x).')).toEqual([
      { text: 'Resources block the first paint. ' },
      { text: 'Learn more', href: 'https://developer.chrome.com/x' },
      { text: '.' },
    ]);
    expect(linkParts('[bad](javascript:alert(1))')).toEqual([{ text: '[bad](javascript:alert(1))' }]);
  });

  it('links a live address to Google’s report', () => {
    expect(pageSpeedUrl('https://acme.example/')).toBe('https://pagespeed.web.dev/analysis?url=https%3A%2F%2Facme.example%2F');
  });
});
