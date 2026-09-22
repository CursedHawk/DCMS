import { describe, expect, it } from 'vitest';
import { describeAgent } from './userAgent';

describe('describeAgent', () => {
  it('names the browser and the platform', () => {
    expect(
      describeAgent(
        'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36',
      ),
    ).toBe('Chrome · Linux');
  });

  it('prefers the browser that is pretending to be another one', () => {
    // Every Chromium browser claims Chrome, and every one of them claims Safari. Checked in
    // order, so these do not all come back as "Chrome" — which would make the list useless for
    // telling two of your own devices apart.
    const chromium = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140.0.0.0 Safari/537.36';
    expect(describeAgent(`${chromium} Edg/140.0.0.0`)).toBe('Edge · Windows');
    expect(describeAgent(`${chromium} OPR/120.0.0.0`)).toBe('Opera · Windows');
  });

  it('calls Android a phone rather than Linux', () => {
    // Android's UA says "Linux; Android 14", and the phone is the half that identifies a device.
    expect(describeAgent('Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/140 Mobile Safari/537.36'))
      .toBe('Chrome · Android');
  });

  it('falls back to the raw string rather than to nothing', () => {
    expect(describeAgent('curl/8.5.0')).toBe('curl/8.5.0');
    expect(describeAgent(null)).toBe('');
    expect(describeAgent('   ')).toBe('');
  });
});
