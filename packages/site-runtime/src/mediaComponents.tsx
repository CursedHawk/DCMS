import { useCallback, useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent } from 'react';
import { ImageSizesContext, mediaList, mediaUrl, picture } from './data';
import { choice, responsiveSelect, select, text, variants, type Props } from './kit';
import type { ComponentDefinition, ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';

/**
 * Media primitives (Mode D v2, U1.2): video, gallery with a lightbox, map, embed.
 *
 * Third-party players are cross-origin iframes only — never markup — and only from a short
 * allow-list, because the canvas runs in the admin's own origin. On the canvas they are drawn
 * as a still (a thumbnail, a shielded map) so a click selects instead of starting a player.
 */

// ---------------------------------------------------------------------------
// Video
// ---------------------------------------------------------------------------

const YOUTUBE = /(?:youtube(?:-nocookie)?\.com\/(?:watch\?(?:.*&)?v=|embed\/|shorts\/|live\/)|youtu\.be\/)([\w-]{11})/;
const VIMEO = /vimeo\.com\/(?:video\/|channels\/[\w-]+\/)?(\d{6,12})/;

export type VideoTarget = { kind: 'youtube' | 'vimeo'; id: string } | { kind: 'file'; src: string } | null;

/** Where a video setting points, or null when it is nothing playable. */
export function videoTarget(source: string, file: unknown, url: string): VideoTarget {
  if (source === 'file') {
    const src = mediaUrl(file);
    return src ? { kind: 'file', src } : null;
  }
  const youtube = YOUTUBE.exec(url)?.[1];
  if (youtube) return { kind: 'youtube', id: youtube };
  const vimeo = VIMEO.exec(url)?.[1];
  if (vimeo) return { kind: 'vimeo', id: vimeo };
  return null;
}

const VIDEO_SOURCES = ['link', 'file'] as const;
const VIDEO_RATIOS = ['16-9', '4-3', '1-1', '9-16'] as const;

function Video({ props, responsive }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const ratio = variants(props, responsive)('ratio', VIDEO_RATIOS, '16-9', (x) => `dcms-ratio-${x}`);
  const title = text(props.title).trim() || 'Video';
  const target = videoTarget(choice(props.source, VIDEO_SOURCES, 'link'), props.file, text(props.url));
  const poster = picture(props.poster)?.src;
  const loop = props.loop === true;
  const muted = props.autoplay === true;

  if (!target) {
    return mode === 'edit' ? <div className={`dcms-video dcms-video-empty ${ratio}`}>Video — paste a YouTube or Vimeo link, or pick a file, in the settings.</div> : null;
  }
  if (target.kind === 'file') {
    const captions = text(props.captions).trim();
    return (
      // Captions are the author's to supply (a .vtt file); a silent background loop needs none.
      // eslint-disable-next-line jsx-a11y/media-has-caption
      <video
        className={`dcms-video ${ratio}`}
        src={target.src}
        poster={poster}
        controls={props.controls !== false}
        preload="metadata"
        playsInline
        autoPlay={muted && mode === 'live'}
        muted={muted}
        loop={loop}
        title={title}
      >
        {captions && <track kind="captions" src={captions} label="Captions" default />}
      </video>
    );
  }
  if (mode === 'edit') {
    // A still on the canvas: an iframe there would swallow the click that selects it.
    const still = poster ?? (target.kind === 'youtube' ? `https://i.ytimg.com/vi/${target.id}/hqdefault.jpg` : undefined);
    return (
      <div className={`dcms-video dcms-video-still ${ratio}`} style={still ? { backgroundImage: `url("${still}")` } : undefined}>
        <span className="dcms-video-play" aria-hidden>▶</span>
      </div>
    );
  }
  const src =
    target.kind === 'youtube'
      ? `https://www.youtube-nocookie.com/embed/${target.id}?rel=0${muted ? '&autoplay=1&mute=1' : ''}${loop ? `&loop=1&playlist=${target.id}` : ''}`
      : `https://player.vimeo.com/video/${target.id}?dnt=1${muted ? '&autoplay=1&muted=1' : ''}${loop ? '&loop=1' : ''}`;
  return (
    <iframe
      className={`dcms-video ${ratio}`}
      src={src}
      title={title}
      loading="lazy"
      referrerPolicy="strict-origin-when-cross-origin"
      allow="autoplay; encrypted-media; picture-in-picture; fullscreen"
      allowFullScreen
    />
  );
}

// ---------------------------------------------------------------------------
// Gallery + lightbox
// ---------------------------------------------------------------------------

const GALLERY_COLUMNS = ['2', '3', '4', '5'] as const;
const GALLERY_GAPS = ['none', 'sm', 'md'] as const;
const GALLERY_SHAPES = ['natural', 'square', 'landscape'] as const;

interface Shot {
  src: string;
  alt: string;
}

function Gallery({ props, responsive, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const v = variants(props, responsive);
  const columns = v('columns', GALLERY_COLUMNS, '3', (x) => `dcms-cols-${x}`);
  // Pictures from content (an event's photos), drawn before any placed by hand.
  const listed = mediaList(props.images);
  const alt = text(props.alt).trim();
  // Each picture is a column wide: the browser loads the WebP that fits it, not the screen's.
  const sizes = `(max-width: 640px) 50vw, ${Math.ceil(100 / Number(choice(props.columns, GALLERY_COLUMNS, '3')))}vw`;
  const gap = choice(props.gap, GALLERY_GAPS, 'sm');
  const shape = choice(props.shape, GALLERY_SHAPES, 'square');
  const lightbox = props.lightbox !== false && mode === 'live';
  const [open, setOpen] = useState<{ shots: Shot[]; at: number } | null>(null);

  const box = useRef<HTMLDivElement>(null);
  // The pictures are the gallery's children, rendered by their own nodes; make each one a
  // keyboard-reachable button that opens the lightbox.
  useEffect(() => {
    if (!lightbox) return;
    for (const img of box.current?.querySelectorAll<HTMLImageElement>('.dcms-grid img') ?? []) {
      img.tabIndex = 0;
      img.setAttribute('role', 'button');
      img.setAttribute('aria-label', `Open picture${img.alt ? `: ${img.alt}` : ''}`);
    }
  });
  const openAt = (e: MouseEvent<HTMLDivElement> | ReactKeyboardEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    if (!lightbox || target.closest('.dcms-lightbox')) return;
    const img = target.closest('img');
    if (!img) return;
    const all = [...e.currentTarget.querySelectorAll<HTMLImageElement>('.dcms-grid img')];
    setOpen({ shots: all.map((i) => ({ src: largest(i), alt: i.alt })), at: Math.max(0, all.indexOf(img)) });
  };

  return (
    // Delegation: the interactive elements are the pictures, made buttons above.
    // eslint-disable-next-line jsx-a11y/no-static-element-interactions
    <div
      ref={box}
      className={`dcms-gallery dcms-gallery-${shape}${lightbox ? ' dcms-gallery-zoomable' : ''}`}
      onClick={openAt}
      onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), openAt(e))}
    >
      <ImageSizesContext.Provider value={sizes}>
        {listed.length > 0 && (
          <div className={`dcms-grid ${columns} dcms-gap-${gap === 'none' ? 'none' : gap}`}>
            {listed.map((url, i) => {
              const pic = picture(url)!;
              return <img key={`${i}:${url}`} className="dcms-image" src={pic.src} srcSet={pic.srcSet} sizes={pic.srcSet ? sizes : undefined} alt={alt ? `${alt} (${i + 1}/${listed.length})` : ''} loading="lazy" />;
            })}
          </div>
        )}
        {slot('images', { className: `dcms-grid ${columns} dcms-gap-${gap === 'none' ? 'none' : gap}` })}
      </ImageSizesContext.Provider>
      {open && <Lightbox shots={open.shots} at={open.at} onClose={() => setOpen(null)} />}
    </div>
  );
}

/**
 * The sharpest copy of a gallery picture for the full-screen view: the widest of its WebP ladder.
 * Where its address was swapped for a local copy (the admin's preview), that copy.
 */
function largest(img: HTMLImageElement): string {
  const ladder = img.getAttribute('srcset');
  if (!ladder || img.src.startsWith('blob:')) return img.currentSrc || img.src;
  return ladder.split(',').at(-1)!.trim().split(/\s+/)[0]!;
}

function Lightbox({ shots, at, onClose }: { shots: Shot[]; at: number; onClose: () => void }) {
  const [index, setIndex] = useState(at);
  const close = useRef<HTMLButtonElement>(null);
  const go = useCallback((step: number) => setIndex((i) => (i + step + shots.length) % shots.length), [shots.length]);
  useEffect(() => {
    close.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      else if (e.key === 'ArrowRight') go(1);
      else if (e.key === 'ArrowLeft') go(-1);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [go, onClose]);
  const shot = shots[index]!;
  return (
    <div className="dcms-lightbox" role="dialog" aria-modal="true" aria-label={shot.alt || 'Picture'}>
      <button type="button" className="dcms-lightbox-scrim" aria-label="Close" tabIndex={-1} onClick={onClose} />
      <img src={shot.src} alt={shot.alt} />
      <button ref={close} type="button" className="dcms-lightbox-close" aria-label="Close" onClick={onClose}>
        ×
      </button>
      {shots.length > 1 && (
        <>
          <button type="button" className="dcms-lightbox-prev" aria-label="Previous picture" onClick={() => go(-1)}>
            ‹
          </button>
          <button type="button" className="dcms-lightbox-next" aria-label="Next picture" onClick={() => go(1)}>
            ›
          </button>
          <span className="dcms-lightbox-count">
            {index + 1} / {shots.length}
          </span>
        </>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Map
// ---------------------------------------------------------------------------

const ZOOMS = ['12', '14', '16', '18'] as const;
const MAP_HEIGHTS = ['sm', 'md', 'lg'] as const;

/** An OpenStreetMap embed around a point; no key, no tracking cookie. */
export function mapEmbedUrl(lat: number, lng: number, zoom: number): string {
  const span = 360 / 2 ** zoom;
  const bbox = [lng - span, lat - span / 2, lng + span, lat + span / 2].map((n) => n.toFixed(5)).join(',');
  return `https://www.openstreetmap.org/export/embed.html?bbox=${bbox}&layer=mapnik&marker=${lat.toFixed(5)},${lng.toFixed(5)}`;
}

function MapView({ props }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const lat = typeof props.latitude === 'number' ? props.latitude : 50.0875;
  const lng = typeof props.longitude === 'number' ? props.longitude : 14.4213;
  const zoom = Number(choice(props.zoom, ZOOMS, '16'));
  const height = choice(props.height, MAP_HEIGHTS, 'md');
  const link = text(props.linkLabel).trim();
  return (
    <div className={`dcms-map dcms-map-${height}`}>
      <iframe
        src={mapEmbedUrl(lat, lng, zoom)}
        title={text(props.title).trim() || 'Map'}
        loading="lazy"
        referrerPolicy="strict-origin-when-cross-origin"
        // On the canvas the map is a picture: a click there selects it.
        style={mode === 'edit' ? { pointerEvents: 'none' } : undefined}
      />
      {link && (
        <a className="dcms-map-link" href={`https://www.openstreetmap.org/?mlat=${lat}&mlon=${lng}#map=${zoom}/${lat}/${lng}`} target="_blank" rel="noopener noreferrer">
          {link}
        </a>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Embed
// ---------------------------------------------------------------------------

/** Which services may be embedded, and how a pasted link becomes their player address. */
const EMBEDS: { name: string; match: RegExp; to: (url: URL) => string }[] = [
  {
    name: 'Spotify',
    match: /^open\.spotify\.com$/,
    to: (u) => `https://open.spotify.com/embed${u.pathname.replace(/^\/embed/, '').replace(/^\/intl-[a-z]+/, '')}`,
  },
  { name: 'SoundCloud', match: /^(www\.)?soundcloud\.com$/, to: (u) => `https://w.soundcloud.com/player/?url=${encodeURIComponent(u.href)}` },
  { name: 'SoundCloud', match: /^w\.soundcloud\.com$/, to: (u) => u.href },
  { name: 'Calendly', match: /^calendly\.com$/, to: (u) => u.href },
  { name: 'Google Forms', match: /^docs\.google\.com$/, to: (u) => (u.pathname.startsWith('/forms/') ? `${u.origin}${u.pathname}?embedded=true` : '') },
  { name: 'Google Maps', match: /^www\.google\.com$/, to: (u) => (u.pathname.startsWith('/maps/embed') ? u.href : '') },
  { name: 'Typeform', match: /^form\.typeform\.com$/, to: (u) => u.href },
  { name: 'Bandcamp', match: /^bandcamp\.com$/, to: (u) => (u.pathname.startsWith('/EmbeddedPlayer') ? u.href : '') },
];

export const EMBED_SERVICES = [...new Set(EMBEDS.map((e) => e.name))];

/** The player address for a pasted link, or null if it is not from an allowed service. */
export function embedUrl(raw: string): string | null {
  let url: URL;
  try {
    url = new URL(raw.trim());
  } catch {
    return null;
  }
  if (url.protocol !== 'https:') return null;
  for (const embed of EMBEDS) {
    if (!embed.match.test(url.hostname)) continue;
    const to = embed.to(url);
    if (to) return to;
  }
  return null;
}

const EMBED_HEIGHTS = ['sm', 'md', 'lg', 'xl'] as const;

function Embed({ props }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const src = embedUrl(text(props.url));
  const height = choice(props.height, EMBED_HEIGHTS, 'md');
  if (!src) {
    return mode === 'edit' ? (
      <div className={`dcms-embed dcms-embed-${height} dcms-embed-empty`}>
        Embed — paste a link from {EMBED_SERVICES.join(', ')} in the settings.
      </div>
    ) : null;
  }
  return (
    <div className={`dcms-embed dcms-embed-${height}`}>
      <iframe
        src={src}
        title={text(props.title).trim() || 'Embedded content'}
        loading="lazy"
        referrerPolicy="strict-origin-when-cross-origin"
        // Cross-origin players: their own scripts, forms and popups, nothing of ours.
        sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-presentation"
        allow="encrypted-media; fullscreen"
        style={mode === 'edit' ? { pointerEvents: 'none' } : undefined}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------

export const MEDIA_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.video',
    version: 1,
    label: 'Video',
    description: 'A video from YouTube or Vimeo, or one you uploaded — played on the page.',
    category: 'Media',
    keywords: ['youtube', 'vimeo', 'film', 'clip', 'movie', 'player'],
    component: Video,
    props: [
      select('source', 'Where it is', VIDEO_SOURCES, 'link', { link: 'YouTube or Vimeo link', file: 'Uploaded file' }, {
        group: 'content',
        description: 'Paste a link to a video on YouTube or Vimeo, or use one from your media library.',
      }),
      { kind: 'url', name: 'url', label: 'Video link', showIf: { prop: 'source', is: ['link'] }, group: 'content', description: 'The address of the video on YouTube or Vimeo — copy it from the browser.' },
      { kind: 'media', name: 'file', label: 'Video file', accept: 'video', showIf: { prop: 'source', is: ['file'] }, group: 'content', description: 'A video from your media library.' },
      { kind: 'media', name: 'poster', label: 'Cover picture', group: 'content', description: 'Shown before the video plays.' },
      {
        kind: 'url',
        name: 'captions',
        label: 'Captions file (.vtt)',
        showIf: { prop: 'source', is: ['file'] },
        group: 'content',
        description: 'Subtitles for people who cannot hear the video — a WebVTT file’s address. YouTube and Vimeo bring their own.',
      },
      { kind: 'text', name: 'title', label: 'Title (for screen readers)', maxLength: 120, group: 'content', description: 'Says what the video is, for people who cannot see it.' },
      responsiveSelect('ratio', 'Shape', VIDEO_RATIOS, '16-9', { '16-9': 'Widescreen', '4-3': 'Classic', '1-1': 'Square', '9-16': 'Upright (phone)' }, {
        group: 'style',
        description: 'Match the video’s own shape to avoid black bars.',
      }),
      { kind: 'boolean', name: 'autoplay', label: 'Play silently on its own', default: false, group: 'behaviour', description: 'Starts muted when the page opens — good for background clips, annoying for talks.' },
      { kind: 'boolean', name: 'loop', label: 'Loop', default: false, group: 'behaviour', description: 'Start again from the beginning when it ends.' },
      { kind: 'boolean', name: 'controls', label: 'Show controls', default: true, showIf: { prop: 'source', is: ['file'] }, group: 'behaviour', description: 'Play, pause and volume buttons (uploaded files only).' },
    ],
  },
  {
    type: 'dcms.gallery',
    version: 1,
    label: 'Gallery',
    description: 'A grid of pictures that open full-screen when clicked, with arrows to browse.',
    category: 'Media',
    keywords: ['photos', 'images', 'pictures', 'lightbox', 'portfolio', 'album'],
    component: Gallery,
    props: [
      responsiveSelect('columns', 'Columns', GALLERY_COLUMNS, '3', { '2': '2 columns', '3': '3 columns', '4': '4 columns', '5': '5 columns' }, {
        group: 'layout',
        description: 'How many pictures sit side by side. Use fewer on phones.',
      }),
      select('gap', 'Space between', GALLERY_GAPS, 'sm', { none: 'None', sm: 'Small', md: 'Medium' }, {
        group: 'layout',
        description: 'The gap between pictures.',
      }),
      select('shape', 'Picture shape', GALLERY_SHAPES, 'square', { natural: 'As uploaded', square: 'Square', landscape: 'Landscape' }, {
        group: 'style',
        description: 'Crop every picture to the same shape so the grid lines up.',
      }),
      { kind: 'boolean', name: 'lightbox', label: 'Open full-screen on click', default: true, group: 'behaviour', description: 'Visitors can click a picture to see it large and browse the rest.' },
      {
        kind: 'media',
        name: 'images',
        label: 'Pictures from content',
        multiple: true,
        group: 'data',
        description: 'Inside a collection or on a detail page: show every picture of a content field, such as an event’s photos. Pictures placed by hand follow them.',
      },
      {
        kind: 'text',
        name: 'alt',
        label: 'Description of the pictures',
        maxLength: 200,
        group: 'data',
        description: 'What the pictures from content show — read out as “description (1/5)”. Bind it to the item’s title.',
      },
    ],
    slots: [{ name: 'images', label: 'Pictures', allowed: ['dcms.image'] }],
  },
  {
    type: 'dcms.map',
    version: 1,
    label: 'Map',
    description: 'A map with a pin on your address — no account or key needed.',
    category: 'Media',
    keywords: ['location', 'address', 'directions', 'openstreetmap', 'where'],
    component: MapView,
    props: [
      { kind: 'number', name: 'latitude', label: 'Latitude', default: 50.0875, min: -90, max: 90, step: 0.0001, group: 'content', description: 'The pin’s position, north–south. Find it by right-clicking the place on openstreetmap.org.' },
      { kind: 'number', name: 'longitude', label: 'Longitude', default: 14.4213, min: -180, max: 180, step: 0.0001, group: 'content', description: 'The pin’s position, east–west.' },
      select('zoom', 'Zoom', ZOOMS, '16', { '12': 'City', '14': 'District', '16': 'Street', '18': 'Building' }, { group: 'style', description: 'How close the map starts.' }),
      select('height', 'Height', MAP_HEIGHTS, 'md', { sm: 'Short', md: 'Medium', lg: 'Tall' }, { group: 'layout', description: 'How tall the map is.' }),
      { kind: 'text', name: 'linkLabel', label: 'Link to the full map', default: 'Open in maps', maxLength: 60, group: 'content', description: 'The text of a link under the map. Leave empty for no link.' },
      { kind: 'text', name: 'title', label: 'Title (for screen readers)', default: 'Map', maxLength: 120, group: 'content', description: 'Says what the map shows, for people who cannot see it.' },
    ],
  },
  {
    type: 'dcms.embed',
    version: 1,
    label: 'Embed',
    description: `A player or widget from another service — ${EMBED_SERVICES.join(', ')} — from a link you paste.`,
    category: 'Media',
    keywords: ['spotify', 'soundcloud', 'calendly', 'booking', 'podcast', 'widget', 'iframe', 'form'],
    component: Embed,
    props: [
      { kind: 'url', name: 'url', label: 'Link', group: 'content', description: `The address from ${EMBED_SERVICES.join(', ')}. Other services are not allowed, to keep visitors safe.` },
      select('height', 'Height', EMBED_HEIGHTS, 'md', { sm: 'Short', md: 'Medium', lg: 'Tall', xl: 'Very tall' }, { group: 'layout', description: 'How tall the embedded area is.' }),
      { kind: 'text', name: 'title', label: 'Title (for screen readers)', maxLength: 120, group: 'content', description: 'Says what the embed is, for people who cannot see it.' },
    ],
  },
];
