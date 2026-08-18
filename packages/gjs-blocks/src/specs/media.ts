import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Media components.
 *
 * Every source trait is `kind: 'media'`, which the Traits panel renders as the
 * real DCMS media library picker rather than a URL box — so an author picks an
 * asset instead of pasting a link that may not survive a re-upload. The picker
 * writes `/api/media/{id}/original`, the same URL the published page resolves.
 */
export const mediaSpecs: DcmsComponentSpec[] = [
  {
    type: 'Image',
    label: 'Image',
    category: 'media',
    tag: 'img',
    icon: 'image',
    acceptsChildren: false,
    order: 0,
    docs: 'A single image from the media library.',
    traits: [
      { name: 'src', label: 'Image', kind: 'media', required: true, accepts: { mediaCategory: 'Image' } },
      {
        name: 'alt',
        label: 'Alt text',
        kind: 'text',
        description: 'What the image conveys. Leave empty only if it is purely decorative.',
      },
      {
        name: 'loading',
        label: 'Loading',
        kind: 'select',
        default: 'lazy',
        options: [
          { value: 'lazy', label: 'Lazy (recommended)' },
          { value: 'eager', label: 'Eager' },
        ],
      },
    ],
    snippet: `<img class="dcms-image" src="" alt="" loading="lazy" />`,
  },
  {
    type: 'Figure',
    label: 'Image with caption',
    category: 'media',
    tag: 'figure',
    icon: 'image',
    acceptsChildren: true,
    order: 1,
    docs: 'An image and its caption, kept together.',
    traits: [],
    snippet: `<figure class="dcms-figure"><img class="dcms-image" src="" alt="" loading="lazy" /><figcaption>Caption</figcaption></figure>`,
  },
  {
    type: 'Video',
    label: 'Video',
    category: 'media',
    tag: 'div',
    icon: 'video',
    acceptsChildren: false,
    order: 2,
    docs: 'A hosted video file or an embed from YouTube or Vimeo.',
    traits: [
      {
        name: 'data-provider',
        label: 'Source',
        kind: 'select',
        default: 'file',
        options: [
          { value: 'file', label: 'Media library' },
          { value: 'youtube', label: 'YouTube' },
          { value: 'vimeo', label: 'Vimeo' },
        ],
      },
      { name: 'data-src', label: 'Video', kind: 'media', accepts: { mediaCategory: 'Video' } },
      { name: 'data-url', label: 'Video URL', kind: 'url', description: 'Used for YouTube and Vimeo.' },
      { name: 'data-poster', label: 'Poster', kind: 'media', accepts: { mediaCategory: 'Image' } },
      { name: 'data-autoplay', label: 'Autoplay (muted)', kind: 'checkbox' },
      { name: 'data-loop', label: 'Loop', kind: 'checkbox' },
    ],
    snippet: `<div class="dcms-video" data-provider="file"></div>`,
  },
  {
    type: 'Audio',
    label: 'Audio',
    category: 'media',
    tag: 'audio',
    icon: 'audio',
    acceptsChildren: false,
    order: 3,
    docs: 'An audio player for a media-library track.',
    traits: [
      { name: 'src', label: 'Track', kind: 'media', accepts: { mediaCategory: 'Audio' } },
      { name: 'controls', label: 'Show controls', kind: 'checkbox', default: true },
      { name: 'loop', label: 'Loop', kind: 'checkbox' },
    ],
    snippet: `<audio class="dcms-audio" controls src=""></audio>`,
  },
  {
    type: 'MediaGallery',
    label: 'Image grid',
    category: 'media',
    tag: 'div',
    icon: 'gallery',
    acceptsChildren: true,
    order: 4,
    docs: 'A fixed grid of images. For a gallery driven by content, use the Image gallery plugin block.',
    traits: [{ name: 'data-columns', label: 'Columns', kind: 'number', default: 3 }],
    snippet: `<div class="dcms-media-gallery" data-columns="3"><img class="dcms-image" src="" alt="" loading="lazy" /><img class="dcms-image" src="" alt="" loading="lazy" /><img class="dcms-image" src="" alt="" loading="lazy" /></div>`,
  },
  {
    type: 'Icon',
    label: 'Icon',
    category: 'media',
    tag: 'span',
    icon: 'icon',
    acceptsChildren: true,
    order: 5,
    docs: 'An inline SVG icon.',
    traits: [
      {
        name: 'data-size',
        label: 'Size',
        kind: 'select',
        default: 'md',
        options: [
          { value: 'sm', label: 'Small' },
          { value: 'md', label: 'Medium' },
          { value: 'lg', label: 'Large' },
        ],
      },
    ],
    snippet: `<span class="dcms-icon" data-size="md" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><path d="M12 2l3 6.5 7 .9-5 4.8 1.2 7L12 18l-6.2 3.2L7 14.2 2 9.4l7-.9z"/></svg></span>`,
  },
  {
    type: 'Logo',
    label: 'Logo',
    category: 'media',
    tag: 'a',
    icon: 'logo',
    acceptsChildren: true,
    order: 6,
    docs: 'The site logo, linked to the home page.',
    traits: [
      { name: 'href', label: 'Link', kind: 'url', default: '/' },
      { name: 'data-src', label: 'Logo image', kind: 'media', accepts: { mediaCategory: 'Image' } },
    ],
    snippet: `<a class="dcms-logo" href="/"><img src="" alt="Home" /></a>`,
  },
];
