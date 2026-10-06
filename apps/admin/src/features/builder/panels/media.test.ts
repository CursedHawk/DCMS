import { describe, expect, it } from 'vitest';
import { audioToAac } from './media';
import { assetIdFrom, mediaUrlFor } from './TraitsPanel';

const id = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';

describe('audio blocks link the AAC copy', () => {
  it('a picked track is written as /aac, anything else as /original', () => {
    expect(mediaUrlFor(id, 'Audio')).toBe(`/api/media/${id}/aac`);
    expect(mediaUrlFor(id, 'Image')).toBe(`/api/media/${id}/original`);
    expect(mediaUrlFor(id)).toBe(`/api/media/${id}/original`);
  });

  it('the picker still recognises either form as the same asset', () => {
    expect(assetIdFrom(`/api/media/${id}/aac`)).toBe(id);
    expect(assetIdFrom(`/api/media/${id}/original`)).toBe(id);
    expect(assetIdFrom(`/api/media/${id}/webp-640`)).toBeUndefined();
  });

  it('an audio block saved before this is moved to /aac when the page opens', () => {
    const html = `<audio class="dcms-audio" controls src="/api/media/${id}/original"></audio>`;
    expect(audioToAac(html)).toBe(`<audio class="dcms-audio" controls src="/api/media/${id}/aac"></audio>`);
  });

  it('leaves pictures, videos and download links on the original', () => {
    const html =
      `<img src="/api/media/${id}/original">` +
      `<video src="/api/media/${id}/original"></video>` +
      `<a href="/api/media/${id}/original" download>Track</a>`;
    expect(audioToAac(html)).toBe(html);
  });
});
