/*
 * Mode A client hydration runtime (dependency-free).
 *
 * The prerenderer emits data-bound / plugin components as inert placeholders:
 *   <div data-dcms-component="BlogList"
 *        data-dcms-props='{"heading":"Latest posts"}'
 *        data-dcms-bindings='[{"propPath":"items","instanceSlug":"blog",
 *                              "query":{"contentType":"post","pageSize":10}}]'></div>
 *
 * On load we walk those placeholders, fetch each binding's published content from
 * the tenant delivery API (/api/{slug}/{contentType}), and render the result in
 * place. The delivery API is reached through the site-host proxy on the same
 * origin, so no auth header or absolute base URL is needed.
 *
 * `props` may carry presentation choices made in the builder:
 *   layout      cards | tiles | list | compact | feature | article | video |
 *               audio | downloads
 *   arrangement grid | masonry | carousel   (cards and tiles only)
 *   columns     1..4, or absent to fit as many as fit
 *   cardVariant outline | raised | soft | plain
 *   titleField / bodyField / imageField / linkField / metaField / tagsField
 *               which field fills each slot, instead of guessing from key names;
 *               "-" means leave the slot empty
 *   excerptLength, heading, subheading, moreLabel, moreHref, linkLabel, emptyText
 *
 * The builder draws the same layouts in its canvas
 * (packages/gjs-blocks/src/preview.ts). The two are separate implementations —
 * this one ships to every published site and must stay dependency-free ES5 —
 * so a change to either belongs in both.
 *
 * **Styling belongs to the site, not to this file.** Every class emitted here is
 * styled by the block stylesheet in the site's own styles/global.css, from theme
 * tokens, so plugin content follows the site's design kit and the author can
 * restyle it. What this file injects is a `:where()` fallback at zero
 * specificity, for pages whose stylesheet predates those rules — the site's own
 * CSS always wins, whatever order the sheets load in.
 */
(function () {
  'use strict';

  var GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  // Fallback content type per data-bound component (mirrors the site-components
  // registry) for bindings saved before the query carried contentType.
  var CONTENT_TYPE = {
    BlogList: 'post',
    ArticleView: 'article',
    GalleryGrid: 'gallery',
    CarouselView: 'slide',
    VideoPlayer: 'video',
    AudioPlayer: 'track',
    DownloadList: 'file',
    SearchBox: 'result',
  };
  var TITLE_KEYS = ['title', 'name', 'heading', 'headline', 'label', 'artist'];
  var BODY_KEYS = ['excerpt', 'summary', 'description', 'body', 'content', 'text', 'caption'];
  var IMAGE_KEYS = ['coverImage', 'heroImage', 'image', 'images', 'cover', 'thumbnail', 'thumb', 'photo', 'poster', 'src'];
  var MEDIA_KEYS = ['source', 'asset', 'url', 'src', 'file', 'audio', 'video', 'track', 'media', 'href'];
  var LINK_KEYS = ['linkUrl', 'href', 'url', 'link'];
  var META_KEYS = ['publishedAt', 'date', 'publishedOn', 'category', 'author', 'kind'];
  var TAG_KEYS = ['tags', 'categories', 'keywords', 'labels'];
  // Field mapping meaning "leave this slot empty" — empty already means "guess".
  var FIELD_NONE = '-';

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function parseJSON(value, fallback) {
    if (!value) return fallback;
    try {
      return JSON.parse(value);
    } catch (e) {
      return fallback;
    }
  }

  // Resolve a media reference to a URL. Accepts a bare asset GUID (→ delivery
  // endpoint), an existing URL, an array (→ first element), or an object holding
  // a media key / id / url.
  function mediaUrl(ref) {
    if (!ref) return '';
    if (Array.isArray(ref)) return ref.length ? mediaUrl(ref[0]) : '';
    if (typeof ref === 'object') ref = pick(ref, MEDIA_KEYS) || ref.id || ref.assetId || '';
    ref = String(ref);
    return GUID.test(ref) ? '/api/media/' + ref + '/original' : ref;
  }

  // Read a field by path, following dots — mirrors readField() in preview.ts.
  //
  // Tenant-defined fields do not sit beside the plugin's own: a content type
  // that declares custom fields nests them under one key (`values.barva`),
  // because the plugin cannot know their names in advance. A flat lookup found
  // exactly the fields every tenant shares and none of the ones that make a site
  // specific. The trailing search is the other half: an unqualified `barva` also
  // finds `values.barva`, so the field name the author sees in the plugin's own
  // admin screen is the one that works here.
  function readField(data, path) {
    if (!data || typeof data !== 'object' || !path) return undefined;
    if (path.indexOf('.') !== -1) {
      var parts = path.split('.');
      var current = data;
      for (var i = 0; i < parts.length; i++) {
        if (current == null || typeof current !== 'object') return undefined;
        current = current[parts[i]];
      }
      return current;
    }

    var direct = data[path];
    if (direct != null && direct !== '') return direct;

    for (var key in data) {
      if (!Object.prototype.hasOwnProperty.call(data, key)) continue;
      var bag = data[key];
      if (bag && typeof bag === 'object' && !Array.isArray(bag)) {
        var nested = bag[path];
        if (nested != null && nested !== '') return nested;
      }
    }
    return direct;
  }

  function pick(data, keys) {
    if (!data || typeof data !== 'object') return undefined;
    for (var i = 0; i < keys.length; i++) {
      var v = readField(data, keys[i]);
      if (v != null && v !== '') return v;
    }
    return undefined;
  }

  // A slot's value: the author's explicit field mapping when they made one,
  // otherwise the first conventionally-named field that holds something. The
  // mapping exists because the guess is only as good as the tenant's naming.
  function slot(data, explicit, keys) {
    if (explicit === FIELD_NONE) return undefined;
    if (explicit) {
      var v = readField(data, explicit);
      return v === '' ? undefined : v;
    }
    return pick(data, keys);
  }

  // A date rendered the way a reader expects. Anything that is not a date is
  // passed through, because the meta slot is as often a category as a timestamp.
  function formatMeta(value) {
    if (value == null) return '';
    var text = String(value);
    if (!/^\d{4}-\d{2}-\d{2}/.test(text)) return text;
    var date = new Date(text);
    if (isNaN(date.getTime())) return text;
    try {
      return date.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
    } catch (e) {
      return text;
    }
  }

  function truncate(text, max) {
    if (!max || text.length <= max) return text;
    var cut = text.slice(0, max);
    var space = cut.lastIndexOf(' ');
    return (space > max * 0.6 ? cut.slice(0, space) : cut).replace(/[\s,;:.–—-]+$/, '') + '…';
  }

  function tagsOf(value) {
    if (value == null) return [];
    var list = Array.isArray(value) ? value : String(value).split(/\s*[,;]\s*/);
    var out = [];
    for (var i = 0; i < list.length && out.length < 6; i++) {
      var tag = String(list[i]).replace(/^\s+|\s+$/g, '');
      if (tag) out.push(tag);
    }
    return out;
  }

  // Every slot of one item, resolved once. Mirrors slotsOf() in preview.ts.
  function slotsOf(item, props) {
    var d = (item && item.data) || item || {};
    var body = slot(d, props.bodyField, BODY_KEYS);
    var meta = slot(d, props.metaField, META_KEYS);
    var title = slot(d, props.titleField, TITLE_KEYS);
    var link = slot(d, props.linkField, LINK_KEYS);
    return {
      title: title == null ? '' : String(title),
      body: body == null ? '' : truncate(String(body), Number(props.excerptLength || 0) || 0),
      image: mediaUrl(slot(d, props.imageField, IMAGE_KEYS)),
      link: link == null ? '' : String(link),
      media: mediaUrl(slot(d, undefined, MEDIA_KEYS)),
      meta: meta == null ? '' : formatMeta(meta),
      tags: tagsOf(slot(d, props.tagsField, TAG_KEYS)),
    };
  }

  function tagsHtml(tags) {
    if (!tags.length) return '';
    return '<div class="dcms-tags">' + tags.map(function (t) {
      return '<span class="dcms-tag">' + esc(t) + '</span>';
    }).join('') + '</div>';
  }

  /*
   * A fallback sheet, at zero specificity.
   *
   * The real styling for all of these classes lives in the site's own
   * styles/global.css, generated from its design kit — that is what makes plugin
   * content look like the rest of the site instead of like a widget. This exists
   * only for a page whose stylesheet predates those rules, so that a block still
   * renders as something rather than as a stack of unstyled text.
   *
   * Every selector is wrapped in `:where()`, which contributes no specificity at
   * all, so a single site rule beats all of it regardless of source order. The
   * custom properties are the ones the theme actually emits (`--dcms-*`); the
   * literals after the comma are only reached when no theme is loaded.
   */
  function injectStylesOnce() {
    if (document.getElementById('dcms-hydrate-styles')) return;
    var muted = 'var(--dcms-color-muted,#6b7280)';
    var border = 'var(--dcms-color-border,#e5e7eb)';
    var radius = 'var(--dcms-radius,.5rem)';
    var gap = 'var(--dcms-space-md,1rem)';
    var style = document.createElement('style');
    style.id = 'dcms-hydrate-styles';
    style.textContent = [
      ':where(.dcms-collection){display:grid;gap:' + gap + ';grid-template-columns:repeat(auto-fill,minmax(16rem,1fr));width:100%;}',
      ':where(.dcms-collection[data-columns="1"]){grid-template-columns:minmax(0,1fr);}',
      ':where(.dcms-collection[data-columns="2"]){grid-template-columns:repeat(2,minmax(0,1fr));}',
      ':where(.dcms-collection[data-columns="3"]){grid-template-columns:repeat(3,minmax(0,1fr));}',
      ':where(.dcms-collection[data-columns="4"]){grid-template-columns:repeat(4,minmax(0,1fr));}',
      ':where(.dcms-collection[data-layout="masonry"]){display:block;columns:3;column-gap:' + gap + ';}',
      ':where(.dcms-collection[data-layout="masonry"]>*){break-inside:avoid;margin-bottom:' + gap + ';}',
      ':where(.dcms-collection[data-layout="carousel"]){grid-auto-flow:column;grid-auto-columns:minmax(16rem,1fr);overflow-x:auto;scroll-snap-type:x mandatory;}',
      ':where(.dcms-collection-head){display:flex;flex-wrap:wrap;align-items:baseline;justify-content:space-between;gap:.5rem;margin-bottom:' + gap + ';}',
      ':where(.dcms-heading){margin:0;font-family:var(--dcms-font-heading,inherit);font-size:1.5rem;}',
      ':where(.dcms-more){font-weight:600;text-decoration:none;white-space:nowrap;}',
      ':where(.dcms-card){display:flex;flex-direction:column;overflow:hidden;border:1px solid ' + border + ';border-radius:' + radius + ';background:var(--dcms-color-surface,#fff);}',
      ':where(.dcms-card-img){width:100%;aspect-ratio:16/9;object-fit:cover;display:block;}',
      ':where(.dcms-card-body){padding:' + gap + ';display:flex;flex-direction:column;gap:.5rem;}',
      ':where(.dcms-card-title){margin:0;font-family:var(--dcms-font-heading,inherit);font-size:1.1rem;}',
      ':where(.dcms-card-text){margin:0;color:' + muted + ';font-size:.95rem;line-height:1.5;}',
      ':where(.dcms-card-meta){margin:0;color:' + muted + ';font-size:.8rem;text-transform:uppercase;letter-spacing:.06em;}',
      ':where(a.dcms-card-link){text-decoration:none;color:inherit;display:block;}',
      ':where(.dcms-tile){position:relative;display:block;overflow:hidden;border-radius:' + radius + ';}',
      ':where(.dcms-tile img){width:100%;height:100%;object-fit:cover;display:block;}',
      ':where(.dcms-tile-caption){position:absolute;inset-inline:0;bottom:0;padding:1.5rem .75rem .75rem;background:linear-gradient(transparent,rgba(0,0,0,.65));color:#fff;font-size:.9rem;font-weight:600;}',
      ':where(.dcms-list){list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:.75rem;}',
      ':where(.dcms-row){display:flex;flex-direction:column;gap:.25rem;padding-bottom:.75rem;border-bottom:1px solid ' + border + ';}',
      ':where(.dcms-row-media){flex-direction:row;align-items:flex-start;gap:' + gap + ';}',
      ':where(.dcms-row-media img){width:6rem;flex:none;aspect-ratio:4/3;object-fit:cover;border-radius:' + radius + ';}',
      ':where(.dcms-row-title){font-weight:600;display:block;}',
      ':where(.dcms-row-text){color:' + muted + ';font-size:.95rem;}',
      ':where(.dcms-row-meta){color:' + muted + ';font-size:.8rem;display:block;}',
      ':where(.dcms-feature-item){display:grid;gap:1.5rem;align-items:center;grid-template-columns:repeat(auto-fit,minmax(18rem,1fr));margin-bottom:' + gap + ';}',
      ':where(.dcms-feature-item img){width:100%;aspect-ratio:16/9;object-fit:cover;border-radius:' + radius + ';}',
      ':where(.dcms-article){max-width:48rem;margin:0 auto;}',
      ':where(.dcms-article-title){font-family:var(--dcms-font-heading,inherit);font-size:2rem;margin:0 0 1rem;}',
      ':where(.dcms-article-meta){color:' + muted + ';font-size:.9rem;margin:0 0 1rem;}',
      ':where(.dcms-article-img){width:100%;border-radius:' + radius + ';margin:0 0 1rem;display:block;}',
      ':where(.dcms-media){width:100%;border-radius:' + radius + ';display:block;}',
      ':where(.dcms-downloads){list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:.5rem;}',
      ':where(.dcms-download a){display:flex;align-items:center;gap:.5rem;padding:.5rem .75rem;border:1px solid ' + border + ';border-radius:' + radius + ';color:var(--dcms-color-brand,#2563eb);text-decoration:none;}',
      ':where(.dcms-tags){display:flex;flex-wrap:wrap;gap:.25rem;}',
      ':where(.dcms-tag){padding:.15em .6em;border-radius:999px;background:var(--dcms-color-surface-sunken,#f1f5f9);color:' + muted + ';font-size:.8rem;}',
      ':where(.dcms-empty){color:' + muted + ';font-size:.95rem;padding:1rem 0;}',
      '@media (max-width:640px){:where(.dcms-collection[data-columns]){grid-template-columns:minmax(0,1fr);}}',
    ].join('');
    document.head.appendChild(style);
  }

  function linked(html, href, className) {
    return href ? '<a class="' + className + '" href="' + esc(href) + '">' + html + '</a>' : html;
  }

  function cardHtml(item, props) {
    var s = slotsOf(item, props);
    var parts = ['<article class="dcms-card" data-variant="' + esc(props.cardVariant || 'outline') + '" data-hover="lift">'];
    if (s.image) parts.push('<img class="dcms-card-img" src="' + esc(s.image) + '" alt="' + esc(s.title) + '" loading="lazy" />');
    parts.push('<div class="dcms-card-body">');
    if (s.meta) parts.push('<p class="dcms-card-meta">' + esc(s.meta) + '</p>');
    if (s.title) parts.push('<h3 class="dcms-card-title">' + esc(s.title) + '</h3>');
    if (s.body) parts.push('<p class="dcms-card-text">' + esc(s.body) + '</p>');
    parts.push(tagsHtml(s.tags));
    parts.push('</div></article>');
    return linked(parts.join(''), s.link, 'dcms-card-link');
  }

  function tileHtml(item, props) {
    var s = slotsOf(item, props);
    var inner = '<img src="' + esc(s.image) + '" alt="' + esc(s.title) + '" loading="lazy" />' +
      (s.title ? '<span class="dcms-tile-caption">' + esc(s.title) + '</span>' : '');
    return s.link
      ? '<a class="dcms-tile" href="' + esc(s.link) + '">' + inner + '</a>'
      : '<figure class="dcms-tile">' + inner + '</figure>';
  }

  // The wrapper every multi-item layout shares, carrying columns and arrangement.
  function collectionHtml(items, props, inner) {
    var attrs = '';
    if (props.columns) attrs += ' data-columns="' + esc(props.columns) + '"';
    if (props.arrangement && props.arrangement !== 'grid') attrs += ' data-layout="' + esc(props.arrangement) + '"';
    return '<div class="dcms-collection"' + attrs + '>' + items.map(inner).join('') + '</div>';
  }

  function rowHtml(item, props) {
    var s = slotsOf(item, props);
    var inner = (s.meta ? '<span class="dcms-row-meta">' + esc(s.meta) + '</span>' : '') +
      '<span class="dcms-row-title">' + esc(s.title || '(untitled)') + '</span>' +
      (s.body ? '<span class="dcms-row-text">' + esc(s.body) + '</span>' : '');
    return '<li class="dcms-row">' + (s.link ? '<a href="' + esc(s.link) + '">' + inner + '</a>' : inner) + '</li>';
  }

  function compactHtml(item, props) {
    var s = slotsOf(item, props);
    var text = '<span>' + (s.meta ? '<span class="dcms-row-meta">' + esc(s.meta) + '</span>' : '') +
      '<span class="dcms-row-title">' + esc(s.title || '(untitled)') + '</span></span>';
    var inner = (s.image ? '<img src="' + esc(s.image) + '" alt="' + esc(s.title) + '" loading="lazy" />' : '') + text;
    return '<li class="dcms-row dcms-row-media">' +
      (s.link ? '<a href="' + esc(s.link) + '">' + inner + '</a>' : inner) + '</li>';
  }

  function listHtml(items, props, inner) {
    return '<ul class="dcms-list" data-style="none">' + items.map(function (item) {
      return inner(item, props);
    }).join('') + '</ul>';
  }

  function featureItemHtml(item, props) {
    var s = slotsOf(item, props);
    var out = ['<article class="dcms-feature-item">'];
    if (s.image) out.push('<img src="' + esc(s.image) + '" alt="' + esc(s.title) + '" loading="lazy" />');
    out.push('<div>');
    if (s.meta) out.push('<span class="dcms-row-meta">' + esc(s.meta) + '</span>');
    if (s.title) out.push('<h3>' + esc(s.title) + '</h3>');
    if (s.body) out.push('<p>' + esc(s.body) + '</p>');
    if (s.link) out.push('<a class="dcms-button" data-variant="link" href="' + esc(s.link) + '">' + esc(props.linkLabel || 'Read more') + '</a>');
    out.push('</div></article>');
    return out.join('');
  }

  function articleHtml(item, props) {
    var s = slotsOf(item, props);
    var out = ['<article class="dcms-article">'];
    if (s.title) out.push('<h1 class="dcms-article-title">' + esc(s.title) + '</h1>');
    if (s.meta) out.push('<p class="dcms-article-meta">' + esc(s.meta) + '</p>');
    if (s.image) out.push('<img class="dcms-article-img" src="' + esc(s.image) + '" alt="' + esc(s.title) + '" />');
    if (s.body) out.push('<div class="dcms-article-body">' + esc(s.body) + '</div>');
    out.push(tagsHtml(s.tags));
    out.push('</article>');
    return out.join('');
  }

  function mediaPlayerHtml(items, tag, props) {
    return collectionHtml(items, props, function (item) {
      var s = slotsOf(item, props);
      var out = ['<div class="dcms-card" data-variant="outline"><div class="dcms-card-body">'];
      if (s.title) out.push('<h3 class="dcms-card-title">' + esc(s.title) + '</h3>');
      if (s.meta) out.push('<p class="dcms-card-meta">' + esc(s.meta) + '</p>');
      if (s.media) out.push('<' + tag + ' class="dcms-media" controls preload="metadata" src="' + esc(s.media) + '"></' + tag + '>');
      out.push('</div></div>');
      return out.join('');
    });
  }

  function downloadsHtml(items, props) {
    return '<ul class="dcms-downloads">' + items.map(function (item) {
      var s = slotsOf(item, props);
      var label = s.title || s.media;
      return '<li class="dcms-download"><a href="' + esc(s.media) + '" download>' + esc(label) +
        (s.meta ? '<span class="dcms-row-meta">' + esc(s.meta) + '</span>' : '') + '</a></li>';
    }).join('') + '</ul>';
  }

  // Layout by type, for pages published before the builder offered the choice.
  var TYPE_LAYOUT = {
    ArticleView: 'article',
    VideoPlayer: 'video',
    AudioPlayer: 'audio',
    DownloadList: 'downloads',
    GalleryGrid: 'tiles',
  };
  var LAYOUTS = ['cards', 'tiles', 'list', 'compact', 'feature', 'article', 'video', 'audio', 'downloads'];

  function layoutOf(type, props) {
    var requested = props && props.layout;
    if (LAYOUTS.indexOf(requested) !== -1) return requested;
    return TYPE_LAYOUT[type] || 'cards';
  }

  function renderBody(type, props, items) {
    if (!items.length) {
      return '<p class="dcms-empty">' + esc((props && props.emptyText) || 'No content published yet.') + '</p>';
    }
    switch (layoutOf(type, props)) {
      case 'article':
        return articleHtml(items[0], props);
      case 'tiles':
        return collectionHtml(items, props, function (item) {
          return tileHtml(item, props);
        });
      case 'list':
        return listHtml(items, props, rowHtml);
      case 'compact':
        return listHtml(items, props, compactHtml);
      case 'feature':
        return items.map(function (item) {
          return featureItemHtml(item, props);
        }).join('');
      case 'video':
        return mediaPlayerHtml(items, 'video', props);
      case 'audio':
        return mediaPlayerHtml(items, 'audio', props);
      case 'downloads':
        return downloadsHtml(items, props);
      default:
        return collectionHtml(items, props, function (item) {
          return cardHtml(item, props);
        });
    }
  }

  // The heading, its supporting line and the "see all" link, as one row — so the
  // link sits beside the heading rather than orphaned under the last card.
  function headHtml(props) {
    var heading = props.heading ? '<h2 class="dcms-heading">' + esc(props.heading) + '</h2>' : '';
    var sub = props.subheading ? '<p class="dcms-section-lead">' + esc(props.subheading) + '</p>' : '';
    var more = props.moreLabel && props.moreHref
      ? '<a class="dcms-more" href="' + esc(props.moreHref) + '">' + esc(props.moreLabel) + '</a>'
      : '';
    if (!heading && !sub && !more) return '';
    return '<div class="dcms-collection-head"><div>' + heading + sub + '</div>' + more + '</div>';
  }

  /* ------------------------------------------------------------------ *
   * Tenant-authored components.
   *
   * A component the author built in the component builder ships its template
   * with the page, in a JSON registry the assembler embeds, and renders here by
   * walking the template's binding attributes. This is a second implementation
   * of packages/gjs-blocks/src/render.ts — that one runs in the admin bundle,
   * this one ships to every site and must stay dependency-free ES5 — so a change
   * to either belongs in both. runtimeParity.test.ts fails when the two stop
   * agreeing about the vocabulary below.
   *
   * Values land as text nodes and attribute values, never as markup, so a field
   * holding a `<script>` renders as the characters `<script>`. The one exception
   * is the `html` target, which is opt-in for exactly that reason.
   * ------------------------------------------------------------------ */

  var BIND_ATTR = 'data-dcms-bind';
  var REPEAT_ATTR = 'data-dcms-repeat';
  var IF_ATTR = 'data-dcms-if';
  var UNLESS_ATTR = 'data-dcms-unless';
  var FORMAT_ATTR = 'data-dcms-format';
  var TRUNCATE_ATTR = 'data-dcms-truncate';
  var PREFIX_ATTR = 'data-dcms-prefix';
  var SUFFIX_ATTR = 'data-dcms-suffix';
  var FALLBACK_ATTR = 'data-dcms-fallback';
  var EMPTY_ATTR = 'data-dcms-empty';
  var RENDERED_ATTR = 'data-dcms-rendered';
  var TEMPLATE_ATTRS = [
    BIND_ATTR, REPEAT_ATTR, IF_ATTR, UNLESS_ATTR, FORMAT_ATTR,
    TRUNCATE_ATTR, PREFIX_ATTR, SUFFIX_ATTR, FALLBACK_ATTR, EMPTY_ATTR,
  ];
  // Longest-first, so `style:background-image` is not read as `style`.
  var BIND_TARGETS = ['style:background-image', 'text', 'html', 'src', 'href', 'alt', 'title', 'class'];
  var MEDIA_TARGETS = { src: 1, 'style:background-image': 1 };

  // The registry the assembler embeds: component name → definition.
  var componentRegistry = null;

  function components() {
    if (componentRegistry) return componentRegistry;
    var el = document.getElementById('dcms-components');
    componentRegistry = (el && parseJSON(el.textContent, null)) || {};
    return componentRegistry;
  }

  function definitionFor(type) {
    if (type.indexOf('custom:') !== 0) return null;
    return components()[type.slice('custom:'.length)] || null;
  }

  function parseBind(value) {
    var trimmed = String(value || '').replace(/^\s+|\s+$/g, '');
    if (!trimmed) return null;
    for (var i = 0; i < BIND_TARGETS.length; i++) {
      var target = BIND_TARGETS[i];
      if (trimmed.indexOf(target + ':') === 0) {
        return { target: target, source: trimmed.slice(target.length + 1).replace(/^\s+|\s+$/g, '') };
      }
    }
    return trimmed.indexOf(':') === -1 ? { target: 'text', source: trimmed } : null;
  }

  function isBlank(value) {
    if (value == null || value === '' || value === false) return true;
    return Array.isArray(value) && value.length === 0;
  }

  // Three namespaces, told apart by a sigil rather than by lookup order: a
  // content type may have a field called `heading` and a component a prop called
  // `heading`, and preferring one silently would make the other unreachable.
  function resolveSource(source, item, index, props) {
    if (source.charAt(0) === '@') return props[source.slice(1)];
    if (source.charAt(0) === '#') {
      var key = source.slice(1);
      if (key === 'index') return index;
      return item ? item[key] : undefined;
    }
    if (!item) return undefined;
    return readField(item.data || item, source);
  }

  function formatBound(el, target, value) {
    var explicit = el.getAttribute(FORMAT_ATTR);
    if (explicit === 'media' || (MEDIA_TARGETS[target] && explicit !== 'raw')) return mediaUrl(value);
    if (explicit === 'date') return formatMeta(value);

    var text = Array.isArray(value) ? value.join(', ') : String(value);
    // A date in a text slot is formatted for the reader; a date in an href is a
    // value the browser has to keep verbatim. `raw` turns the guess off.
    var readable = target === 'text' || target === 'html';
    var formatted = readable && explicit !== 'raw' ? formatMeta(text) : text;
    var limit = Number(el.getAttribute(TRUNCATE_ATTR) || 0) || 0;
    return limit ? truncate(formatted, limit) : formatted;
  }

  function applyBind(el, spec, item, index, props) {
    var parsed = parseBind(spec);
    if (!parsed) return;

    var raw = resolveSource(parsed.source, item, index, props);
    var fallback = el.getAttribute(FALLBACK_ATTR) || '';
    var text = isBlank(raw) ? fallback : formatBound(el, parsed.target, raw);

    // An empty binding leaves the element exactly as the author drew it: a
    // button still says "Read more", an image keeps its placeholder rather than
    // becoming a broken one. Removing it is what data-dcms-if is for.
    if (!text) return;

    var prefix = el.getAttribute(PREFIX_ATTR) || '';
    var suffix = el.getAttribute(SUFFIX_ATTR) || '';
    if (prefix || suffix) text = prefix + text + suffix;

    if (parsed.target === 'text') el.textContent = text;
    else if (parsed.target === 'html') el.innerHTML = text;
    else if (parsed.target === 'style:background-image') el.style.backgroundImage = 'url("' + text.replace(/"/g, '%22') + '")';
    else if (parsed.target === 'class') {
      var classes = text.split(/\s+/);
      for (var i = 0; i < classes.length; i++) if (classes[i]) el.className += ' ' + classes[i];
    } else el.setAttribute(parsed.target, text);
  }

  function passesCondition(el, item, index, props) {
    var ifSource = el.getAttribute(IF_ATTR);
    if (ifSource && isBlank(resolveSource(ifSource, item, index, props))) return false;
    var unlessSource = el.getAttribute(UNLESS_ATTR);
    if (unlessSource && !isBlank(resolveSource(unlessSource, item, index, props))) return false;
    return true;
  }

  // `skipRepeated` stops the ambient pass from touching what the repeat pass
  // rendered: those clones hold their own item, and re-binding them against the
  // ambient one shows the same post in every card.
  function applyBindings(root, item, index, props, skipRepeated) {
    var candidates = [];
    if (root.getAttribute(BIND_ATTR) || root.getAttribute(IF_ATTR) || root.getAttribute(UNLESS_ATTR)) {
      candidates.push(root);
    }
    var found = root.querySelectorAll('[' + BIND_ATTR + '],[' + IF_ATTR + '],[' + UNLESS_ATTR + ']');
    for (var i = 0; i < found.length; i++) {
      if (skipRepeated && closestAttr(found[i], RENDERED_ATTR)) continue;
      candidates.push(found[i]);
    }

    for (var j = 0; j < candidates.length; j++) {
      var el = candidates[j];
      // A parent removed by its own condition took its children with it.
      if (el !== root && !root.contains(el)) continue;
      if (!passesCondition(el, item, index, props)) {
        if (el.parentNode) el.parentNode.removeChild(el);
        continue;
      }
      var bind = el.getAttribute(BIND_ATTR);
      if (bind) applyBind(el, bind, item, index, props);
    }
  }

  // Element.closest is not universal enough to rely on for a published page.
  function closestAttr(el, attr) {
    for (var node = el; node && node.nodeType === 1; node = node.parentNode) {
      if (node.getAttribute(attr) !== null) return node;
    }
    return null;
  }

  function stripTemplateAttrs(root) {
    var selector = [];
    for (var i = 0; i < TEMPLATE_ATTRS.length; i++) selector.push('[' + TEMPLATE_ATTRS[i] + ']');
    var targets = [root];
    var found = root.querySelectorAll(selector.join(',') + ',[' + RENDERED_ATTR + ']');
    for (var j = 0; j < found.length; j++) targets.push(found[j]);
    for (var k = 0; k < targets.length; k++) {
      for (var a = 0; a < TEMPLATE_ATTRS.length; a++) targets[k].removeAttribute(TEMPLATE_ATTRS[a]);
      targets[k].removeAttribute(RENDERED_ATTR);
    }
  }

  function renderTemplate(template, items, props) {
    var holder = document.createElement('div');
    holder.innerHTML = template;

    // The template's root is the component's own wrapper, which the placeholder
    // element already is — rendering it would nest a second wrapper on every
    // hydration, and the canvas would be one level shallower than the page.
    var root = holder.children.length === 1 ? holder.children[0] : holder;

    var repeat = root.querySelector('[' + REPEAT_ATTR + ']');
    if (repeat && repeat.parentNode) {
      var parent = repeat.parentNode;
      for (var i = 0; i < items.length; i++) {
        var clone = repeat.cloneNode(true);
        clone.removeAttribute(REPEAT_ATTR);
        applyBindings(clone, items[i], i, props, false);
        clone.setAttribute(RENDERED_ATTR, '');
        parent.insertBefore(clone, repeat);
      }
      parent.removeChild(repeat);
    }

    // Everything outside the repeat resolves against the first item — which is
    // all a detail view is, and what a list's heading usually wants.
    applyBindings(root, items[0], 0, props, true);

    var markers = root.querySelectorAll('[' + EMPTY_ATTR + ']');
    for (var m = markers.length - 1; m >= 0; m--) {
      if (items.length && markers[m].parentNode) markers[m].parentNode.removeChild(markers[m]);
    }

    stripTemplateAttrs(root);
    return root === holder ? root.innerHTML : root.outerHTML;
  }

  function render(el, type, props, items) {
    var definition = definitionFor(type);
    if (definition) {
      el.innerHTML = renderTemplate(definition.template, items, props || {});
      el.setAttribute('data-dcms-hydrated', 'true');
      return;
    }
    injectStylesOnce();
    el.innerHTML = headHtml(props || {}) + renderBody(type, props || {}, items);
    el.setAttribute('data-dcms-hydrated', 'true');
  }

  // Which item a detail component shows: an explicitly pinned slug, else the last
  // segment of the page URL — the convention a /blog/my-post route implies.
  function itemSlugFor(query) {
    if (query.itemSlug) return String(query.itemSlug);
    var parts = (location.pathname || '').split('/').filter(Boolean);
    var last = parts.length ? parts[parts.length - 1] : '';
    return last.replace(/\.html?$/i, '');
  }

  function fetchBinding(binding, type) {
    var slug = binding.instanceSlug || (binding.source && binding.source.instanceSlug);
    var query = binding.query || (binding.source && binding.source.query) || {};
    var contentType = query.contentType || query.type || CONTENT_TYPE[type];
    if (!slug || !contentType) return Promise.resolve([]);

    var url = '/api/' + encodeURIComponent(slug) + '/' + encodeURIComponent(contentType);

    // A binding that fills a single prop is a detail view: it wants one item by
    // slug, not the first page of a list.
    if (binding.propPath === 'item') {
      var itemSlug = itemSlugFor(query);
      if (!itemSlug) return Promise.resolve([]);
      url += '/' + encodeURIComponent(itemSlug);
    } else {
      var qs = [];
      if (query.pageSize) qs.push('pageSize=' + encodeURIComponent(query.pageSize));
      if (query.page) qs.push('page=' + encodeURIComponent(query.page));
      if (qs.length) url += '?' + qs.join('&');
    }

    return fetch(url, { headers: { Accept: 'application/json' }, credentials: 'same-origin' })
      .then(function (res) {
        if (!res.ok) throw new Error('HTTP ' + res.status);
        return res.json();
      })
      .then(function (payload) {
        if (Array.isArray(payload)) return payload;
        if (payload && Array.isArray(payload.items)) return payload.items;
        return payload ? [payload] : [];
      });
  }

  function hydrate(el) {
    var type = el.getAttribute('data-dcms-component') || '';
    var props = parseJSON(el.getAttribute('data-dcms-props'), {});
    var bindings = parseJSON(el.getAttribute('data-dcms-bindings'), []);
    if (!bindings.length) return;

    fetchBinding(bindings[0], type)
      .then(function (items) {
        render(el, type, props, items);
      })
      .catch(function () {
        injectStylesOnce();
        el.innerHTML = headHtml(props || {}) +
          '<p class="dcms-empty">Content is temporarily unavailable.</p>';
      });
  }

  // Anonymous pageview beacon. Posts to the same-origin collect endpoint, which
  // resolves the tenant (host) and its analytics instance server-side; it 202s
  // even when analytics is disabled, so this never surfaces an error. A per-tab
  // session id lets the backend derive a (hashed) visitor without cookies.
  function sessionId() {
    try {
      var k = 'dcms-sid';
      var v = sessionStorage.getItem(k);
      if (!v) {
        v = (Date.now().toString(36) + Math.random().toString(36).slice(2, 10));
        sessionStorage.setItem(k, v);
      }
      return v;
    } catch (e) {
      return null;
    }
  }

  function sendPageview() {
    var payload = {
      type: 'pageview',
      path: location.pathname || '/',
      referrer: document.referrer || null,
      sessionId: sessionId(),
    };
    try {
      fetch('/api/collect', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
        credentials: 'same-origin',
        keepalive: true,
      }).catch(function () {});
    } catch (e) {
      /* best-effort */
    }
  }

  // --- Page-aware navigation ----------------------------------------------
  //
  // Mirrors packages/gjs-blocks/src/nav.ts. A breadcrumb bar or a menu lives in
  // a shared region and is therefore the same markup on every page; the trail
  // and the "you are here" mark can only be resolved here, against the URL the
  // visitor actually asked for. Kept in step by runtimeParity.test.ts.

  var NAV_ATTR = 'data-dcms-nav';
  var HOME_LABEL_ATTR = 'data-home-label';
  var routeTable = null;

  function routes() {
    if (routeTable) return routeTable;
    var el = document.getElementById('dcms-routes');
    routeTable = (el && parseJSON(el.textContent, null)) || [];
    return routeTable;
  }

  function normalizePath(path) {
    var clean = (path || '/').split('?')[0].split('#')[0];
    clean = clean.replace(/index.html$/, '');
    if (clean.length > 1 && clean.charAt(clean.length - 1) === '/') clean = clean.slice(0, -1);
    return clean === '' ? '/' : clean;
  }

  function titleForPath(path) {
    var list = routes();
    for (var i = 0; i < list.length; i++) {
      if (normalizePath(list[i].path) === normalizePath(path)) return list[i].title;
    }
    return null;
  }

  function humanize(segment) {
    var words = decodeURIComponent(segment).replace(/[-_]+/g, ' ').replace(/^s+|s+$/g, '');
    return words.charAt(0).toUpperCase() + words.slice(1);
  }

  function breadcrumbTrail(path, homeLabel) {
    var clean = normalizePath(path);
    var crumbs = [{ path: '/', label: titleForPath('/') || homeLabel || 'Home', current: clean === '/' }];
    if (clean === '/') return crumbs;

    var segments = clean.split('/');
    var walked = '';
    var real = [];
    for (var i = 0; i < segments.length; i++) if (segments[i]) real.push(segments[i]);
    for (var j = 0; j < real.length; j++) {
      walked += '/' + real[j];
      crumbs.push({
        path: walked,
        // An intermediate segment often has no page of its own, so the slug is
        // humanised rather than dropped — a trail with a gap reads as a bug.
        label: titleForPath(walked) || humanize(real[j]),
        current: j === real.length - 1,
      });
    }
    return crumbs;
  }

  function fillBreadcrumbs(el, path) {
    var list = el.querySelector('ol,ul') || el;
    var crumbs = breadcrumbTrail(path, el.getAttribute(HOME_LABEL_ATTR) || 'Home');
    list.innerHTML = '';
    for (var i = 0; i < crumbs.length; i++) {
      var item = document.createElement('li');
      if (crumbs[i].current) {
        item.setAttribute('aria-current', 'page');
        item.textContent = crumbs[i].label;
      } else {
        var link = document.createElement('a');
        link.setAttribute('href', crumbs[i].path);
        link.textContent = crumbs[i].label;
        item.appendChild(link);
      }
      list.appendChild(item);
    }
  }

  function markCurrent(el, path) {
    var here = normalizePath(path);
    var links = el.querySelectorAll('a[href]');
    for (var i = 0; i < links.length; i++) {
      var href = links[i].getAttribute('href') || '';
      // Only same-site paths can be "here"; an absolute URL never is.
      if (href.charAt(0) !== '/') continue;
      if (normalizePath(href) === here) links[i].setAttribute('aria-current', 'page');
      else links[i].removeAttribute('aria-current');
    }
  }

  function applyNav(root, path) {
    var nodes = root.querySelectorAll('[' + NAV_ATTR + ']');
    for (var i = 0; i < nodes.length; i++) {
      var kind = nodes[i].getAttribute(NAV_ATTR);
      if (kind === 'breadcrumbs') fillBreadcrumbs(nodes[i], path);
      else if (kind === 'menu') markCurrent(nodes[i], path);
    }
  }

  function run() {
    sendPageview();
    applyNav(document, location.pathname || '/');
    var nodes = document.querySelectorAll('[data-dcms-component]');
    for (var i = 0; i < nodes.length; i++) hydrate(nodes[i]);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', run);
  } else {
    run();
  }
})();
