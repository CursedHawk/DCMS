import DOMPurify from 'dompurify';
import { createContext, useContext, useEffect, useId, useMemo, useState, useSyncExternalStore, type FormEvent, type ReactNode } from 'react';
import type { Action } from './actions';
import { writeState, type PageStateScope } from './state';
import {
  DataClientContext,
  DataError,
  ItemContext,
  isConnectionSource,
  listParser,
  listPath,
  parseItemList,
  sourceSchema,
  useData,
  type ContentItem,
} from './data';
import type { ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';

/**
 * Components that show or collect data: collections, rich text, forms, popups (P4).
 *
 * The same rule as every built-in: one implementation for the canvas and the site, reading
 * `RenderMode`. On the canvas a collection shows one editable item template filled with real
 * content and its loading/empty/error states laid out underneath, so all four can be designed;
 * on the site it shows whichever applies.
 */

type Props = Record<string, unknown>;

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

/**
 * Where a collection on the canvas reports the items it fetched, so the nodes inside its item
 * template — each its own React root on the canvas — can read the item they will show. The site
 * does not need it: there, React context carries the item.
 */
export interface PreviewItemsSink {
  publish(nodeId: string, items: readonly ContentItem[]): void;
}
export const PreviewItemsContext = createContext<PreviewItemsSink | null>(null);

function EditorNote({ children, tone = 'note' }: { children: ReactNode; tone?: 'note' | 'state' }) {
  return <div className={tone === 'state' ? 'dcms-editor-state' : 'dcms-editor-note'}>{children}</div>;
}

export function Collection({ nodeId, props, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const sink = useContext(PreviewItemsContext);
  const source = sourceSchema.safeParse(props.source);
  const limit = typeof props.limit === 'number' ? Math.min(Math.max(Math.round(props.limit), 1), 50) : 6;
  const tag = text(props.tag).trim() || undefined;
  const layout = props.layout === 'list' ? 'dcms-collection-list' : 'dcms-collection-grid';
  const paging = props.paging === 'more' || props.paging === 'pages' ? props.paging : 'none';
  // How many "Load more" has asked for, or which page the pager is on. Starts over when what the
  // collection shows changes.
  const [shown, setShown] = useState(1);
  const [page, setPage] = useState(1);
  const sourceJson = JSON.stringify(props.source ?? null);
  useEffect(() => {
    setShown(1);
    setPage(1);
  }, [sourceJson, tag, limit, paging]);

  // A plugin's delivery API pages itself; a connection's snapshot is all there is, sliced here.
  const connection = source.success && isConnectionSource(source.data);
  const want = paging === 'more' ? Math.min(limit * shown, 100) : limit;
  const atPage = paging === 'pages' ? page : 1;
  const list = useData(
    source.success ? listPath(source.data, connection ? {} : { limit: want, tag, page: atPage > 1 ? atPage : undefined }) : null,
    source.success ? listParser(source.data) : parseItemList,
  );
  const windowed = useMemo(
    () =>
      list.state === 'ready' && connection
        ? { ...list, value: { items: list.value.items.slice((atPage - 1) * limit, (atPage - 1) * limit + want), totalCount: list.value.totalCount } }
        : list,
    [list, connection, atPage, limit, want],
  );

  useEffect(() => {
    if (mode === 'edit' && windowed.state === 'ready') sink?.publish(nodeId, windowed.value.items);
  }, [mode, windowed, sink, nodeId]);

  if (mode === 'edit') {
    const items = windowed.state === 'ready' ? windowed.value.items : [];
    const note = !source.success
      ? 'Choose the content this collection shows.'
      : windowed.state === 'loading'
        ? 'Loading content…'
        : windowed.state === 'error'
          ? 'The content could not be loaded.'
          : items.length === 0
            ? 'There is no published content yet; the site will show the “empty” state below.'
            : `This item is the first of ${items.length}${windowed.value.totalCount > items.length ? ` (of ${windowed.value.totalCount})` : ''}; the site repeats it for each.` +
              (paging === 'more' ? ' The site adds a “Load more” button.' : paging === 'pages' ? ' The site adds page numbers.' : '');
    return (
      <div className="dcms-collection">
        <div className={layout}>
          <ItemContext.Provider value={items[0] ? { item: items[0], index: 0, count: items.length } : null}>
            {slot('item')}
          </ItemContext.Provider>
        </div>
        <EditorNote>{note}</EditorNote>
        <EditorNote tone="state">While loading</EditorNote>
        {slot('loading')}
        <EditorNote tone="state">When there is nothing to show</EditorNote>
        {slot('empty')}
        <EditorNote tone="state">If the content cannot be loaded</EditorNote>
        {slot('error')}
      </div>
    );
  }

  if (!source.success) return null;
  if (windowed.state === 'loading') return <div className="dcms-collection" aria-busy="true">{slot('loading')}</div>;
  if (windowed.state === 'error') return <div className="dcms-collection">{slot('error')}</div>;
  if (windowed.value.items.length === 0) return <div className="dcms-collection">{slot('empty')}</div>;
  const count = windowed.value.items.length;
  const total = windowed.value.totalCount;
  const pages = Math.max(1, Math.ceil(total / limit));
  return (
    <div className="dcms-collection" id={`dcms-collection-${nodeId}`}>
      <div className={layout}>
        {windowed.value.items.map((item, index) => (
          <ItemContext.Provider key={item.id || item.slug} value={{ item, index, count }}>
            {slot('item')}
          </ItemContext.Provider>
        ))}
      </div>
      {/* Only while the last request came back full: a plugin may cap its page size lower. */}
      {paging === 'more' && count < total && count === want && (
        <div className="dcms-collection-more">
          <button type="button" className="dcms-button dcms-button-secondary dcms-button-md" onClick={() => setShown((n) => n + 1)}>
            {text(props.moreLabel).trim() || 'Load more'}
          </button>
        </div>
      )}
      {paging === 'pages' && pages > 1 && (
        <nav className="dcms-pager" aria-label="Pages">
          <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-label="Previous page" disabled={atPage <= 1} onClick={() => turn(atPage - 1)}>
            ‹
          </button>
          <span aria-live="polite">
            {atPage} / {pages}
          </span>
          <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-label="Next page" disabled={atPage >= pages} onClick={() => turn(atPage + 1)}>
            ›
          </button>
        </nav>
      )}
    </div>
  );

  function turn(to: number) {
    setPage(to);
    document.getElementById(`dcms-collection-${nodeId}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
}

/**
 * HTML from an author or a content field, always through DOMPurify. On the canvas this runs in the
 * admin's own origin, so it is a security boundary, not a nicety; where DOMPurify cannot run (no
 * DOM) the content is shown as text rather than trusted.
 */
const noSubscribe = () => () => {};

export function RichText({ props }: ComponentRenderProps<Props>) {
  // False while prerendering and while hydrating what was prerendered: sanitising needs a DOM,
  // so the server leaves the box empty and the browser fills it — the two first renders match.
  const inBrowser = useSyncExternalStore(noSubscribe, () => true, () => false);
  const html = text(props.html);
  if (!html) return null;
  if (!inBrowser) return <div className="dcms-richtext" />;
  if (!DOMPurify.isSupported) return <div className="dcms-richtext">{html}</div>;
  const clean = DOMPurify.sanitize(html, { USE_PROFILES: { html: true }, FORBID_TAGS: ['style', 'form', 'input', 'button'] });
  return <div className="dcms-richtext" dangerouslySetInnerHTML={{ __html: clean }} />;
}

/** A form that posts to the Forms plugin. Its fields are ordinary children: `dcms.field` nodes. */
export function Form({ props, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const client = useContext(DataClientContext);
  const [state, setState] = useState<'idle' | 'sending' | 'sent' | 'error'>('idle');
  const [message, setMessage] = useState<string | null>(null);
  const instance = text(props.instance);
  const form = text(props.form);

  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    // Held before the await: React clears `currentTarget` once the handler yields.
    const element = e.currentTarget;
    if (mode === 'edit' || !instance || !form) return;
    const values: Record<string, unknown> = {};
    for (const [key, value] of new FormData(element).entries()) values[key] = typeof value === 'string' ? value : value.name;
    // A single checkbox is yes/no; a group of them is the list of what was ticked.
    for (const box of Array.from(element.querySelectorAll<HTMLInputElement>('input[type="checkbox"][name]'))) {
      if (box.hasAttribute('data-group')) {
        const ticked = (values[box.name] = Array.isArray(values[box.name]) ? values[box.name] : []) as string[];
        if (box.checked) ticked.push(box.value);
      } else values[box.name] = box.checked;
    }
    setState('sending');
    setMessage(null);
    try {
      await client.post(`/api/${encodeURIComponent(instance)}/forms/${encodeURIComponent(form)}`, values);
      setState('sent');
      element.reset();
    } catch (err) {
      setState('error');
      const body = err instanceof DataError ? (err.body as { error?: string; message?: string } | undefined) : undefined;
      setMessage(body?.message ?? body?.error ?? null);
    }
  };

  return (
    <form className="dcms-form" onSubmit={submit} noValidate={mode === 'edit'}>
      {slot('fields', { className: 'dcms-form-fields' })}
      {state === 'sent' ? (
        <p className="dcms-form-status" role="status">
          {text(props.successMessage, 'Thank you — your message was sent.')}
        </p>
      ) : (
        <>
          <button type="submit" className="dcms-button dcms-button-primary dcms-button-md" disabled={state === 'sending'}>
            {text(props.submitLabel, 'Send')}
          </button>
          {state === 'error' && (
            <p className="dcms-form-status dcms-form-error" role="alert">
              {message ?? 'Sending failed. Please try again.'}
            </p>
          )}
        </>
      )}
    </form>
  );
}

export const FIELD_TYPES = ['text', 'email', 'tel', 'number', 'textarea', 'date', 'select', 'radio', 'checkboxes', 'checkbox'] as const;
/** The field types that offer a list of options. */
export const CHOICE_FIELDS: readonly string[] = ['select', 'radio', 'checkboxes'];

/** A choice field's options: one per line, blanks dropped. */
export function fieldOptions(value: unknown): string[] {
  return text(value)
    .split('\n')
    .map((o) => o.trim())
    .filter(Boolean);
}

export function FormField({ props }: ComponentRenderProps<Props>) {
  const id = useId();
  const name = text(props.name, 'field');
  const label = text(props.label, name);
  const type = (FIELD_TYPES as readonly string[]).includes(text(props.type)) ? text(props.type) : 'text';
  const required = props.required === true;
  const placeholder = text(props.placeholder) || undefined;
  const help = text(props.help).trim();
  const helpId = help ? `${id}-help` : undefined;
  const min = typeof props.minLength === 'number' ? props.minLength : undefined;
  const max = typeof props.maxLength === 'number' ? props.maxLength : undefined;
  const options = fieldOptions(props.options);
  const hint = help ? (
    <p id={helpId} className="dcms-field-help">
      {help}
    </p>
  ) : null;

  if (type === 'checkbox') {
    return (
      <div className="dcms-field">
        <label className="dcms-field-check" htmlFor={id}>
          <input id={id} name={name} type="checkbox" required={required} aria-describedby={helpId} /> {label}
        </label>
        {hint}
      </div>
    );
  }
  if (type === 'radio' || type === 'checkboxes') {
    // A group: the legend is the question, each option its own labelled input.
    return (
      <fieldset className="dcms-field dcms-field-group" aria-describedby={helpId}>
        <legend>
          {label}
          {required ? ' *' : ''}
        </legend>
        {options.map((option, i) => (
          <label key={i} className="dcms-field-check">
            <input
              type={type === 'radio' ? 'radio' : 'checkbox'}
              name={name}
              value={option}
              data-group={type === 'checkboxes' ? '' : undefined}
              required={required && type === 'radio'}
            />{' '}
            {option}
          </label>
        ))}
        {hint}
      </fieldset>
    );
  }
  return (
    <div className="dcms-field">
      <label htmlFor={id}>
        {label}
        {required ? ' *' : ''}
      </label>
      {type === 'textarea' ? (
        <textarea id={id} name={name} required={required} placeholder={placeholder} rows={4} minLength={min} maxLength={max} aria-describedby={helpId} />
      ) : type === 'select' ? (
        <select id={id} name={name} required={required} defaultValue="" aria-describedby={helpId}>
          <option value="" disabled>
            {placeholder ?? 'Choose…'}
          </option>
          {options.map((option, i) => (
            <option key={i} value={option}>
              {option}
            </option>
          ))}
        </select>
      ) : (
        <input
          id={id}
          name={name}
          type={type}
          required={required}
          placeholder={placeholder}
          minLength={type === 'text' || type === 'email' || type === 'tel' ? min : undefined}
          maxLength={type === 'text' || type === 'email' || type === 'tel' ? max : undefined}
          aria-describedby={helpId}
        />
      )}
      {hint}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Popups and toasts: the action runner's own small state, shared by every root.
// ---------------------------------------------------------------------------

let openModal: string | null = null;
const listeners = new Set<() => void>();
function setOpenModal(id: string | null) {
  openModal = id;
  listeners.forEach((l) => l());
}
function subscribe(l: () => void) {
  listeners.add(l);
  return () => listeners.delete(l);
}

export function Modal({ nodeId, props, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const open = useSyncExternalStore(subscribe, () => openModal === nodeId, () => false);
  const title = text(props.title, 'Popup');
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpenModal(null);
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open]);

  // On the canvas a popup is laid out in place, labelled, so it can be designed like anything else.
  if (mode === 'edit') {
    return (
      <div className="dcms-modal-editor">
        <EditorNote tone="state">Popup: {title}</EditorNote>
        {slot('default', { className: 'dcms-modal-body dcms-flow' })}
      </div>
    );
  }
  if (!open) return null;
  return (
    <div className="dcms-modal-backdrop">
      {/* Clicking outside closes: a real button behind the dialog, so it works by keyboard too. */}
      <button type="button" className="dcms-modal-scrim" aria-label="Close" tabIndex={-1} onClick={() => setOpenModal(null)} />
      <div role="dialog" aria-modal="true" aria-label={title} className="dcms-modal">
        <button type="button" className="dcms-modal-close" aria-label="Close" onClick={() => setOpenModal(null)}>
          ×
        </button>
        {slot('default', { className: 'dcms-modal-body dcms-flow' })}
      </div>
    </div>
  );
}

function showToast(message: string, tone: 'info' | 'success' | 'error' = 'info') {
  let host = document.getElementById('dcms-toasts');
  if (!host) {
    host = document.createElement('div');
    host.id = 'dcms-toasts';
    host.className = 'dcms-toasts';
    document.body.appendChild(host);
  }
  const el = document.createElement('div');
  el.className = `dcms-toast dcms-toast-${tone}`;
  el.setAttribute('role', 'status');
  el.textContent = message;
  host.appendChild(el);
  setTimeout(() => el.remove(), 4000);
}

/** Run an action that is not a link. Links are rendered as links (see SiteLink). */
export function runAction(action: Action, pageState?: PageStateScope | null): void {
  switch (action.type) {
    case 'set-state':
      if (pageState) writeState(pageState, action.key, action.value);
      return;
    case 'toggle-state':
      if (pageState) writeState(pageState, action.key, (current) => !current);
      return;
    case 'scroll-to':
      document.querySelector(`[data-dcms-node="${CSS.escape(action.target)}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
      return;
    case 'open-modal':
      setOpenModal(action.modal);
      return;
    case 'show-toast':
      showToast(action.message, action.tone);
      return;
    default:
      return;
  }
}
