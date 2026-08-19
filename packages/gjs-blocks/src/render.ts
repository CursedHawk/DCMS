import {
  BIND_ATTR,
  EMPTY_ATTR,
  FALLBACK_ATTR,
  FORMAT_ATTR,
  IF_ATTR,
  PREFIX_ATTR,
  REPEAT_ATTR,
  SUFFIX_ATTR,
  TEMPLATE_ATTRS,
  TRUNCATE_ATTR,
  UNLESS_ATTR,
  metaSource,
  parseBind,
  propSource,
  type BindTarget,
} from '@dcms/gjs-schema';
import { fieldsOf, formatMeta, mediaUrl, readField, truncate, type PreviewItem } from './preview';

/**
 * The renderer for tenant-authored components.
 *
 * A component's template is HTML carrying binding attributes, so rendering is a
 * DOM walk rather than string interpolation: clone the template, repeat the
 * marked subtree once per item, fill each bound element, drop the ones whose
 * condition failed, and strip the bookkeeping. Nothing is concatenated into
 * markup, so a field holding `<script>` lands as the text `<script>` — the one
 * exception being the `html` target, which is opt-in precisely because it is the
 * only binding that trusts its value.
 *
 * **This module and `renderCustom` in
 * `src/Services/Dcms.SiteBuilder/Runtime/hydrate.js` are two implementations of
 * one algorithm** — the runtime ships dependency-free ES5 to every published
 * site, this runs in the admin bundle — so a change to either belongs in both.
 * `runtimeParity.test.ts` reads the runtime as text and fails if they stop
 * agreeing about the vocabulary.
 *
 * The document is a parameter rather than the ambient global because the canvas
 * renders inside its own iframe: building nodes with the parent document and
 * inserting them into the frame works today and is exactly the kind of thing
 * that stops working under a stricter document policy.
 */

/** The item slots a `#source` reads: the envelope, not the content. */
const META_KEYS = ['id', 'slug', 'index'];

/** Targets whose value is a URL, so a bare asset id has to be resolved. */
const MEDIA_TARGETS = new Set<BindTarget>(['src', 'style:background-image']);

export interface RenderOptions {
  /**
   * Called with every media reference the template resolves. The canvas uses it
   * to swap in an authenticated blob URL; the published page has no need for one
   * because its media is served from the same origin as the page.
   */
  onMedia?: (url: string) => string;
}

/**
 * Render a template against the items and props of one component instance.
 *
 * Returns the markup for the component's *contents*: the caller owns the
 * placeholder element these go inside, exactly as `previewHtml` does for the
 * built-in layouts.
 */
export function renderTemplate(
  doc: Document,
  template: string,
  items: readonly PreviewItem[],
  props: Record<string, unknown> = {},
  options: RenderOptions = {},
): string {
  const holder = doc.createElement('div');
  holder.innerHTML = template;

  // A template's root is usually the component's own wrapper, which the
  // placeholder element already provides — rendering it would nest a second
  // wrapper inside the first on every hydration. Unwrapping one level here is
  // what keeps the canvas and the published DOM the same depth.
  const root = holder.children.length === 1 ? (holder.firstElementChild as HTMLElement) : holder;

  const repeat = root.querySelector<HTMLElement>(`[${REPEAT_ATTR}]`);
  const ambient = items[0];

  if (repeat) {
    const parent = repeat.parentNode;
    if (parent) {
      const rendered = items.map((item, index) => {
        const clone = repeat.cloneNode(true) as HTMLElement;
        clone.removeAttribute(REPEAT_ATTR);
        applyBindings(clone, item, index, props, options);
        // Marked so the ambient pass below leaves it alone — see applyBindings.
        clone.setAttribute(RENDERED_ATTR, '');
        return clone;
      });
      for (const node of rendered) parent.insertBefore(node, repeat);
      parent.removeChild(repeat);
    }
  }

  // Everything outside the repeat resolves against the first item, which is what
  // a detail view is entirely made of and what a list's heading usually wants.
  applyBindings(root, ambient, 0, props, options, true);
  pruneEmptyMarkers(root, items.length === 0);
  stripTemplateAttrs(root);

  return root === holder ? root.innerHTML : root.outerHTML;
}

/**
 * `[data-dcms-empty]` is the component's own empty state: kept when nothing was
 * fetched, removed when something was. Two markers rather than one flag because
 * "no posts yet" and the list itself are different markup, and an author should
 * be able to lay both out and see the right one.
 */
function pruneEmptyMarkers(root: HTMLElement, isEmpty: boolean): void {
  for (const el of Array.from(root.querySelectorAll<HTMLElement>(`[${EMPTY_ATTR}]`))) {
    if (!isEmpty) el.remove();
  }
}

/**
 * Fill every bound element under `root`.
 *
 * `skipRepeated` stops the second pass from touching what the repeat pass
 * already rendered: those clones hold their own item, and re-binding them
 * against the ambient one would overwrite every card with the first item's
 * values — the failure that looks like "my list shows the same post six times".
 */
function applyBindings(
  root: HTMLElement,
  item: PreviewItem | undefined,
  index: number,
  props: Record<string, unknown>,
  options: RenderOptions,
  skipRepeated = false,
): void {
  const candidates: HTMLElement[] = [];
  if (root.hasAttribute(BIND_ATTR) || hasCondition(root)) candidates.push(root);
  const selector = `[${BIND_ATTR}],[${IF_ATTR}],[${UNLESS_ATTR}]`;
  for (const el of Array.from(root.querySelectorAll<HTMLElement>(selector))) {
    if (skipRepeated && el.closest(`[${RENDERED_ATTR}]`)) continue;
    candidates.push(el);
  }

  for (const el of candidates) {
    // A parent removed by its own condition takes its children with it, and
    // binding into a detached node is wasted work.
    if (el !== root && !root.contains(el)) continue;
    if (!passesCondition(el, item, index, props)) {
      el.remove();
      continue;
    }
    const bind = el.getAttribute(BIND_ATTR);
    if (bind) applyBind(el, bind, item, index, props, options);
  }
}

function hasCondition(el: HTMLElement): boolean {
  return el.hasAttribute(IF_ATTR) || el.hasAttribute(UNLESS_ATTR);
}

/** Marks a rendered repeat clone. Internal, and stripped from the output. */
const RENDERED_ATTR = 'data-dcms-rendered';

function passesCondition(
  el: HTMLElement,
  item: PreviewItem | undefined,
  index: number,
  props: Record<string, unknown>,
): boolean {
  const ifSource = el.getAttribute(IF_ATTR);
  if (ifSource && isBlank(resolve(ifSource, item, index, props))) return false;
  const unlessSource = el.getAttribute(UNLESS_ATTR);
  if (unlessSource && !isBlank(resolve(unlessSource, item, index, props))) return false;
  return true;
}

function isBlank(value: unknown): boolean {
  if (value == null || value === '' || value === false) return true;
  return Array.isArray(value) && value.length === 0;
}

function applyBind(
  el: HTMLElement,
  spec: string,
  item: PreviewItem | undefined,
  index: number,
  props: Record<string, unknown>,
  options: RenderOptions,
): void {
  const parsed = parseBind(spec);
  if (!parsed) return;

  const raw = resolve(parsed.source, item, index, props);
  const fallback = el.getAttribute(FALLBACK_ATTR) ?? '';
  let text = isBlank(raw) ? fallback : format(el, parsed.target, raw);

  // An empty binding leaves the element exactly as the author drew it: a button
  // still says "Read more", an image keeps its placeholder rather than becoming
  // a broken one. Removing the element when its data is missing is what
  // `data-dcms-if` is for, and it should be the author's choice.
  if (!text) return;

  const prefix = el.getAttribute(PREFIX_ATTR) ?? '';
  const suffix = el.getAttribute(SUFFIX_ATTR) ?? '';
  if (prefix || suffix) text = `${prefix}${text}${suffix}`;

  if (MEDIA_TARGETS.has(parsed.target) && options.onMedia) {
    text = options.onMedia(text);
  }

  switch (parsed.target) {
    case 'text':
      el.textContent = text;
      break;
    case 'html':
      el.innerHTML = text;
      break;
    case 'style:background-image':
      el.style.backgroundImage = text ? `url("${text.replace(/"/g, '%22')}")` : '';
      break;
    case 'class':
      if (text) el.classList.add(...text.split(/\s+/).filter(Boolean));
      break;
    default:
      el.setAttribute(parsed.target, text);
      break;
  }
}

/**
 * A resolved value as a string, honouring the element's formatting attributes.
 *
 * `media` and `date` are explicit, but the common cases are inferred: a value
 * landing in `src` is a media reference whether or not the author said so, and
 * an ISO date in a text slot is formatted for a reader — the same two guesses
 * the built-in layouts make, so a hand-built card and a generated one show a
 * date the same way.
 */
function format(el: HTMLElement, target: BindTarget, value: unknown): string {
  const explicit = el.getAttribute(FORMAT_ATTR);
  if (explicit === 'media' || (MEDIA_TARGETS.has(target) && explicit !== 'raw')) {
    return mediaUrl(value);
  }
  if (explicit === 'date') return formatMeta(value);

  const text = Array.isArray(value) ? value.map((v) => String(v)).join(', ') : String(value);
  // A date in a text slot is formatted for the reader; a date in an `href` is a
  // value the browser has to keep verbatim. `raw` turns the guess off.
  const readable = target === 'text' || target === 'html';
  const formatted = readable && explicit !== 'raw' ? formatMeta(text) : text;
  const limit = Number(el.getAttribute(TRUNCATE_ATTR) ?? 0) || 0;
  return limit ? truncate(formatted, limit) : formatted;
}

/**
 * What a binding source names.
 *
 * Three namespaces, distinguished by a sigil rather than by lookup order,
 * because a content type is free to have a field called `heading` and a
 * component is free to have a prop called `heading`, and silently preferring one
 * would make the other unreachable.
 */
export function resolve(
  source: string,
  item: PreviewItem | undefined,
  index: number,
  props: Record<string, unknown>,
): unknown {
  const prop = propSource(source);
  if (prop !== null) return props[prop];

  const meta = metaSource(source);
  if (meta !== null) {
    if (meta === 'index') return index;
    return META_KEYS.includes(meta) ? (item as Record<string, unknown> | undefined)?.[meta] : undefined;
  }

  if (!item) return undefined;
  return readField(fieldsOf(item), source);
}

/** Remove every attribute the renderer consumed, so the output is clean markup. */
function stripTemplateAttrs(root: HTMLElement): void {
  const selector = TEMPLATE_ATTRS.map((a) => `[${a}]`).join(',');
  const targets = [root, ...Array.from(root.querySelectorAll<HTMLElement>(selector))];
  for (const el of targets) {
    for (const attr of TEMPLATE_ATTRS) el.removeAttribute(attr);
  }
  const marked = [root, ...Array.from(root.querySelectorAll<HTMLElement>(`[${RENDERED_ATTR}]`))];
  for (const el of marked) el.removeAttribute(RENDERED_ATTR);
}

/**
 * Substitute a snippet component's props into its template, once, at drop time.
 *
 * A component with no data source has nothing to hydrate, so it publishes as
 * real markup rather than as a placeholder waiting for JavaScript — a static
 * page that needs a script to show its own header is a static page that renders
 * empty to a crawler. The trade is that its props are baked in and the markup is
 * the author's from then on, which is what "save this as a block" should mean.
 */
export function expandSnippet(
  doc: Document,
  template: string,
  props: Record<string, unknown> = {},
): string {
  return renderTemplate(doc, template, [], props);
}
