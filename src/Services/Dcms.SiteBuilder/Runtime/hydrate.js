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
 *   layout      cards | list | article | video | audio | downloads
 *   titleField / bodyField / imageField / linkField
 *               which field fills each slot, instead of guessing from key names
 *   heading, emptyText
 *
 * The builder draws the same layouts in its canvas
 * (packages/gjs-blocks/src/preview.ts). The two are separate implementations —
 * this one ships to every published site and must stay dependency-free ES5 —
 * so a change to either belongs in both.
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

  function pick(data, keys) {
    if (!data || typeof data !== 'object') return undefined;
    for (var i = 0; i < keys.length; i++) {
      var v = data[keys[i]];
      if (v != null && v !== '') return v;
    }
    return undefined;
  }

  // A slot's value: the author's explicit field mapping when they made one,
  // otherwise the first conventionally-named field that holds something. The
  // mapping exists because the guess is only as good as the tenant's naming.
  function slot(data, explicit, keys) {
    if (explicit) {
      var v = data && data[explicit];
      return v === '' ? undefined : v;
    }
    return pick(data, keys);
  }

  function injectStylesOnce() {
    if (document.getElementById('dcms-hydrate-styles')) return;
    var style = document.createElement('style');
    style.id = 'dcms-hydrate-styles';
    style.textContent = [
      '.dcms-collection{display:grid;gap:1.25rem;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));width:100%;}',
      '.dcms-heading{margin:0 0 1rem;font:600 1.5rem/1.2 var(--font-heading,inherit);}',
      '.dcms-card{display:flex;flex-direction:column;overflow:hidden;border:1px solid var(--color-border,#e5e7eb);border-radius:var(--radius,12px);background:var(--color-card,#fff);}',
      '.dcms-card-img{width:100%;aspect-ratio:16/9;object-fit:cover;display:block;}',
      '.dcms-card-body{padding:1rem;display:flex;flex-direction:column;gap:.5rem;}',
      '.dcms-card-title{margin:0;font:600 1.1rem/1.3 var(--font-heading,inherit);}',
      '.dcms-card-text{margin:0;color:var(--color-muted,#6b7280);font-size:.95rem;line-height:1.5;}',
      '.dcms-article{max-width:48rem;margin:0 auto;}',
      '.dcms-article-title{font:700 2rem/1.2 var(--font-heading,inherit);margin:0 0 1rem;}',
      '.dcms-article-img{width:100%;border-radius:var(--radius,12px);margin:0 0 1rem;display:block;}',
      '.dcms-media{width:100%;border-radius:var(--radius,12px);display:block;}',
      '.dcms-downloads{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:.5rem;}',
      '.dcms-download a{display:inline-flex;align-items:center;gap:.5rem;color:var(--color-primary,#2563eb);text-decoration:none;}',
      '.dcms-empty{color:var(--color-muted,#6b7280);font-size:.95rem;padding:1rem 0;}',
      '.dcms-list{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:.75rem;}',
      '.dcms-row{display:flex;flex-direction:column;gap:.25rem;padding-bottom:.75rem;border-bottom:1px solid var(--color-border,#e5e7eb);}',
      '.dcms-row-title{font-weight:600;}',
      '.dcms-row-text{color:var(--color-muted,#6b7280);font-size:.95rem;}',
    ].join('');
    document.head.appendChild(style);
  }

  function cardHtml(item, props) {
    var d = (item && item.data) || item || {};
    var title = slot(d, props.titleField, TITLE_KEYS);
    var body = slot(d, props.bodyField, BODY_KEYS);
    var img = slot(d, props.imageField, IMAGE_KEYS);
    var href = slot(d, props.linkField, LINK_KEYS);
    var parts = ['<article class="dcms-card">'];
    if (img) parts.push('<img class="dcms-card-img" src="' + esc(mediaUrl(img)) + '" alt="' + esc(title || '') + '" loading="lazy" />');
    parts.push('<div class="dcms-card-body">');
    if (title) parts.push('<h3 class="dcms-card-title">' + esc(title) + '</h3>');
    if (body) parts.push('<p class="dcms-card-text">' + esc(body) + '</p>');
    parts.push('</div></article>');
    var html = parts.join('');
    return href ? '<a href="' + esc(href) + '" style="text-decoration:none;color:inherit;">' + html + '</a>' : html;
  }

  function collectionHtml(items, inner) {
    return '<div class="dcms-collection">' + items.map(inner).join('') + '</div>';
  }

  function rowHtml(item, props) {
    var d = (item && item.data) || item || {};
    var title = slot(d, props.titleField, TITLE_KEYS);
    var body = slot(d, props.bodyField, BODY_KEYS);
    var href = slot(d, props.linkField, LINK_KEYS);
    var inner = '<span class="dcms-row-title">' + esc(title || '(untitled)') + '</span>' +
      (body ? '<span class="dcms-row-text">' + esc(body) + '</span>' : '');
    return '<li class="dcms-row">' + (href ? '<a href="' + esc(href) + '">' + inner + '</a>' : inner) + '</li>';
  }

  function listHtml(items, props) {
    return '<ul class="dcms-list">' + items.map(function (item) {
      return rowHtml(item, props);
    }).join('') + '</ul>';
  }

  function articleHtml(item, props) {
    var d = (item && item.data) || item || {};
    var title = slot(d, props.titleField, TITLE_KEYS);
    var body = slot(d, props.bodyField, BODY_KEYS);
    var img = slot(d, props.imageField, IMAGE_KEYS);
    var out = ['<article class="dcms-article">'];
    if (title) out.push('<h1 class="dcms-article-title">' + esc(title) + '</h1>');
    if (img) out.push('<img class="dcms-article-img" src="' + esc(mediaUrl(img)) + '" alt="' + esc(title || '') + '" />');
    if (body) out.push('<div class="dcms-article-body">' + esc(body) + '</div>');
    out.push('</article>');
    return out.join('');
  }

  function mediaPlayerHtml(items, tag, props) {
    return collectionHtml(items, function (item) {
      var d = (item && item.data) || item || {};
      var src = mediaUrl(pick(d, MEDIA_KEYS));
      var title = slot(d, props.titleField, TITLE_KEYS);
      var out = ['<div class="dcms-card"><div class="dcms-card-body">'];
      if (title) out.push('<h3 class="dcms-card-title">' + esc(title) + '</h3>');
      if (src) out.push('<' + tag + ' class="dcms-media" controls preload="metadata" src="' + esc(src) + '"></' + tag + '>');
      out.push('</div></div>');
      return out.join('');
    });
  }

  function downloadsHtml(items, props) {
    return '<ul class="dcms-downloads">' + items.map(function (item) {
      var d = (item && item.data) || item || {};
      var src = mediaUrl(pick(d, MEDIA_KEYS));
      var title = slot(d, props.titleField, TITLE_KEYS) || src;
      return '<li class="dcms-download"><a href="' + esc(src) + '" download>' + esc(title) + '</a></li>';
    }).join('') + '</ul>';
  }

  // Layout by type, for pages published before the builder offered the choice.
  var TYPE_LAYOUT = {
    ArticleView: 'article',
    VideoPlayer: 'video',
    AudioPlayer: 'audio',
    DownloadList: 'downloads',
  };
  var LAYOUTS = ['cards', 'list', 'article', 'video', 'audio', 'downloads'];

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
      case 'list':
        return listHtml(items, props);
      case 'video':
        return mediaPlayerHtml(items, 'video', props);
      case 'audio':
        return mediaPlayerHtml(items, 'audio', props);
      case 'downloads':
        return downloadsHtml(items, props);
      default:
        return collectionHtml(items, function (item) {
          return cardHtml(item, props);
        });
    }
  }

  function render(el, type, props, items) {
    injectStylesOnce();
    var heading = props && props.heading;
    var html = (heading ? '<h2 class="dcms-heading">' + esc(heading) + '</h2>' : '') + renderBody(type, props || {}, items);
    el.innerHTML = html;
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
        var heading = props && props.heading;
        el.innerHTML = (heading ? '<h2 class="dcms-heading">' + esc(heading) + '</h2>' : '') +
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

  function run() {
    sendPageview();
    var nodes = document.querySelectorAll('[data-dcms-component]');
    for (var i = 0; i < nodes.length; i++) hydrate(nodes[i]);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', run);
  } else {
    run();
  }
})();
