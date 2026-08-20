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

/** The item slots a `#source` reads off the envelope rather than the content. */
const META_KEYS = ['id', 'slug', 'value'];

/**
 * The item a binding resolves against, and where it sits in the list it came
 * from.
 *
 * The position travels with the item because a repeat is not always the
 * component's fetched list: a nested repeat iterates an array on the item above
 * it, and `#index` inside it must count that array, not the outer one.
 */
export interface ItemContext {
  item: PreviewItem | undefined;
  index: number;
  count: number;
}

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
  const ambient: ItemContext = { item: items[0], index: 0, count: items.length };

  expandRepeats(root, ambient, items, props, options);
  // Everything outside the repeats resolves against the first item, which is what
  // a detail view is entirely made of and what a list's heading usually wants.
  applyBindings(root, ambient, props, options, true);
  pruneEmptyMarkers(root, items.length === 0);
  stripTemplateAttrs(root);

  return root === holder ? root.innerHTML : root.outerHTML;
}

/**
 * The repeats directly inside `root` — the ones not already contained by
 * another repeat.
 *
 * The inner ones are deliberately left alone: they are expanded again inside
 * each clone, against the item that clone holds, which is what makes
 * `<li data-dcms-repeat="crew">` inside `<article data-dcms-repeat>` list *that*
 * event's crew instead of the same names under every event.
 */
function outermostRepeats(root: HTMLElement): HTMLElement[] {
  const all = Array.from(root.querySelectorAll<HTMLElement>(`[${REPEAT_ATTR}]`));
  return all.filter((el) => !all.some((other) => other !== el && other.contains(el)));
}

/**
 * Whatever a repeat's source resolved to, as a list of items.
 *
 * An array of objects is a list of items already; a single object is a list of
 * one (a `detail` binding whose field holds one record); primitives are wrapped
 * so a repeat over an array of strings can still bind them, through `#value`.
 */
function asItems(value: unknown): PreviewItem[] {
  if (value == null || value === '') return [];
  const list = Array.isArray(value) ? value : [value];
  return list.map((entry) =>
    entry !== null && typeof entry === 'object' ? (entry as PreviewItem) : ({ value: entry } as PreviewItem),
  );
}

/**
 * Expand every repeat under `root`, recursively.
 *
 * `items` is the component's own fetched list and is only meaningful at the top
 * level: a bare `data-dcms-repeat` there means "once per fetched item". A repeat
 * carrying a source (`data-dcms-repeat="crew"`, `data-dcms-repeat="@names"`)
 * iterates that value on whichever item is in scope, at any depth.
 */
function expandRepeats(
  root: HTMLElement,
  ctx: ItemContext,
  items: readonly PreviewItem[] | null,
  props: Record<string, unknown>,
  options: RenderOptions,
): void {
  for (const template of outermostRepeats(root)) {
    const parent = template.parentNode;
    if (!parent) continue;

    const source = (template.getAttribute(REPEAT_ATTR) ?? '').trim();
    const list = source ? asItems(resolve(source, ctx, props)) : (items ?? []);
    template.removeAttribute(REPEAT_ATTR);

    list.forEach((item, index) => {
      const clone = template.cloneNode(true) as HTMLElement;
      const child: ItemContext = { item, index, count: list.length };
      // Inner repeats first: binding the clone before expanding them would fill
      // the inner template's single row and then copy that one row per entry.
      expandRepeats(clone, child, null, props, options);
      applyBindings(clone, child, props, options, true);
      // Marked so the ambient pass leaves it alone — see applyBindings.
      clone.setAttribute(RENDERED_ATTR, '');
      parent.insertBefore(clone, template);
    });

    claimEmptyMarkers(parent, list.length === 0);
    parent.removeChild(template);
  }
}

/**
 * An `[data-dcms-empty]` sibling of a repeat is *that repeat's* empty state, so
 * it is settled here rather than by the component-wide pass — otherwise a nested
 * list with no entries would keep or drop its "none yet" line based on whether
 * the outer list had items, which has nothing to do with it.
 *
 * Dropping the attribute is how a kept marker says it has been decided.
 */
function claimEmptyMarkers(parent: ParentNode, isEmpty: boolean): void {
  for (const child of Array.from(parent.children)) {
    if (!child.hasAttribute(EMPTY_ATTR)) continue;
    if (isEmpty) child.removeAttribute(EMPTY_ATTR);
    else child.remove();
  }
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
  ctx: ItemContext,
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
    if (!passesCondition(el, ctx, props)) {
      el.remove();
      continue;
    }
    const bind = el.getAttribute(BIND_ATTR);
    if (bind) applyBind(el, bind, ctx, props, options);
  }
}

function hasCondition(el: HTMLElement): boolean {
  return el.hasAttribute(IF_ATTR) || el.hasAttribute(UNLESS_ATTR);
}

/** Marks a rendered repeat clone. Internal, and stripped from the output. */
const RENDERED_ATTR = 'data-dcms-rendered';

function passesCondition(
  el: HTMLElement,
  ctx: ItemContext,
  props: Record<string, unknown>,
): boolean {
  const ifSource = el.getAttribute(IF_ATTR);
  if (ifSource && isBlank(resolve(ifSource, ctx, props))) return false;
  const unlessSource = el.getAttribute(UNLESS_ATTR);
  if (unlessSource && !isBlank(resolve(unlessSource, ctx, props))) return false;
  return true;
}

function isBlank(value: unknown): boolean {
  if (value == null || value === '' || value === false) return true;
  return Array.isArray(value) && value.length === 0;
}

function applyBind(
  el: HTMLElement,
  spec: string,
  ctx: ItemContext,
  props: Record<string, unknown>,
  options: RenderOptions,
): void {
  const parsed = parseBind(spec);
  if (!parsed) return;

  const raw = resolve(parsed.source, ctx, props);
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
  ctx: ItemContext,
  props: Record<string, unknown>,
): unknown {
  const prop = propSource(source);
  if (prop !== null) return props[prop];

  const meta = metaSource(source);
  if (meta !== null) return resolveMeta(meta, ctx);

  if (!ctx.item) return undefined;
  return readField(fieldsOf(ctx.item), source);
}

/**
 * The `#` namespace: the item's envelope plus its position.
 *
 * The positional ones exist because a list template otherwise has no way to say
 * "the first one is the big card" or to stripe its rows — the author's only
 * recourse was `:nth-child` in a stylesheet, which cannot reach a class name or
 * an attribute. Most are booleans, meant for `data-dcms-if` / `data-dcms-unless`;
 * `#parity` is the one that reads well as a value, in a `class` binding.
 */
function resolveMeta(key: string, { item, index, count }: ItemContext): unknown {
  switch (key) {
    case 'index':
      return index;
    case 'number':
      return index + 1;
    case 'count':
      return count;
    case 'first':
      return index === 0;
    case 'last':
      return count > 0 && index === count - 1;
    case 'even':
      return index % 2 === 0;
    case 'odd':
      return index % 2 === 1;
    case 'parity':
      return index % 2 === 0 ? 'even' : 'odd';
    default:
      return META_KEYS.includes(key)
        ? (item as Record<string, unknown> | undefined)?.[key]
        : undefined;
  }
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
