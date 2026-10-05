import { pageIdFromPath, sourceKey } from '@dcms/site-runtime';
import { ChevronDown, ChevronRight, FileText, Home, LayoutTemplate, Plus, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
  Textarea,
  cn,
} from '@dcms/ui';
import { useContentCatalog } from './data';
import { useVfs } from '../site-source';
import {
  createPage,
  deletePage,
  inMainMenu,
  parseStateValue,
  readPage,
  setRoutePath,
  toggleMainMenu,
  updateApp,
  updatePage,
  type Result,
} from './documents';
import { sameTarget, useVisual } from './store';
import { PAGE_TEMPLATES, pageBody } from './templates/pages';

function report(result: Result, t: (k: string) => string): boolean {
  if (!result.ok) toast.error(result.error || t('errors.generic'));
  return result.ok;
}

/**
 * The site's structure: its routes, the page each one shows, the app shell around them, and
 * the SEO each page carries. Every change is a schema-checked edit of `dcms/app.json` or a page
 * file (see documents.ts), so a route that would clash with another is refused here with the
 * same reason the site would give.
 */
export function PagesPanel() {
  const { t } = useTranslation();
  const app = useVisual((s) => s.app);
  const target = useVisual((s) => s.target);
  const setTarget = useVisual((s) => s.setTarget);
  const files = useVfs((s) => s.files);
  const [open, setOpen] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  const pages = useMemo(
    () => Object.keys(files).map(pageIdFromPath).filter((id): id is string => id !== null),
    [files],
  );
  const routed = new Set(app?.routes.map((r) => r.page));
  const orphans = pages.filter((id) => !routed.has(id));

  if (!app) return <p className="p-4 text-sm text-muted-foreground">{t('visual.pages.noApp')}</p>;

  return (
    <div className="flex h-full flex-col overflow-y-auto">
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('builder.pages')}</span>
        <Button size="sm" variant="ghost" onClick={() => setAdding((v) => !v)}>
          <Plus className="h-4 w-4" /> {t('builder.addPage')}
        </Button>
      </div>

      {adding && <AddPage onDone={(id) => { setAdding(false); if (id) setTarget({ kind: 'page', id }); }} />}

      <button
        type="button"
        onClick={() => setTarget({ kind: 'shell' })}
        className={cn(
          'flex items-center gap-2 border-b px-3 py-2 text-left text-sm hover:bg-muted/60',
          sameTarget(target, { kind: 'shell' }) && 'bg-muted font-medium',
        )}
      >
        <LayoutTemplate className="h-4 w-4 text-muted-foreground" />
        <span className="min-w-0 flex-1">{t('visual.pages.shell')}</span>
        <span className="text-xs text-muted-foreground">{t('visual.pages.everyPage')}</span>
      </button>

      <ul className="divide-y">
        {app.routes.map((route) => {
          const page = readPage(route.page);
          const active = sameTarget(target, { kind: 'page', id: route.page });
          const expanded = open === route.id;
          return (
            <li key={route.id}>
              <div className={cn('flex items-center gap-1 px-1 py-1', active && 'bg-muted')}>
                <button
                  type="button"
                  aria-label={expanded ? t('visual.collapse') : t('visual.expand')}
                  onClick={() => setOpen(expanded ? null : route.id)}
                  className="rounded p-1 text-muted-foreground hover:bg-muted"
                >
                  {expanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                </button>
                <button
                  type="button"
                  onClick={() => setTarget({ kind: 'page', id: route.page })}
                  className="flex min-w-0 flex-1 items-center gap-2 py-1 text-left text-sm"
                >
                  {route.path === '/' ? <Home className="h-4 w-4 shrink-0" /> : <FileText className="h-4 w-4 shrink-0 text-muted-foreground" />}
                  <span className={cn('min-w-0 truncate', active && 'font-medium')}>{page?.title ?? route.page}</span>
                  <span className="ml-auto truncate text-xs text-muted-foreground">{route.path}</span>
                </button>
              </div>
              {expanded && page && <PageSettings routeId={route.id} path={route.path} pageId={route.page} />}
            </li>
          );
        })}
      </ul>

      {orphans.length > 0 && (
        <div className="border-t p-3 text-xs text-muted-foreground">
          {t('visual.pages.unrouted')}: {orphans.join(', ')}
        </div>
      )}

      <SiteSeo />
    </div>
  );
}

function AddPage({ onDone }: { onDone: (id?: string) => void }) {
  const { t } = useTranslation();
  const [title, setTitle] = useState('');
  const [path, setPath] = useState('');
  const [menu, setMenu] = useState(true);
  const [template, setTemplate] = useState(PAGE_TEMPLATES[0]!);
  const registry = useVisual((s) => s.registry);
  const derived = `/${title.toLowerCase().normalize('NFKD').replace(/[̀-ͯ]/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')}`;

  const submit = () => {
    if (!title.trim()) return;
    const result = createPage(title.trim(), path.trim() || derived, menu, pageBody(template, registry));
    if (report(result, t)) onDone(result.id);
  };

  return (
    <form
      className="space-y-2 border-b bg-muted/30 p-3"
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <div role="radiogroup" aria-label={t('visual.pages.startWith')} className="grid grid-cols-2 gap-1.5">
        {PAGE_TEMPLATES.map((p) => (
          <button
            key={p.id}
            type="button"
            role="radio"
            aria-checked={template.id === p.id}
            title={p.description}
            onClick={() => {
              // A suggested title and address, unless the author has typed their own.
              if (!title.trim() || title === template.title) setTitle(p.title);
              if (!path.trim() || path === template.path) setPath(p.path);
              setTemplate(p);
            }}
            className={cn(
              'rounded-md border bg-background px-2 py-1.5 text-left text-xs',
              template.id === p.id ? 'border-primary ring-1 ring-primary' : 'hover:border-primary/60',
            )}
          >
            <span className="block font-medium">{p.label}</span>
            <span className="line-clamp-2 text-muted-foreground">{p.description}</span>
          </button>
        ))}
      </div>
      <Label htmlFor="new-page-title">{t('builder.pageTitle')}</Label>
      <Input id="new-page-title" value={title} onChange={(e) => setTitle(e.target.value)} />
      <Label htmlFor="new-page-path">{t('visual.pages.address')}</Label>
      <Input id="new-page-path" value={path} placeholder={derived === '/' ? '/about' : derived} onChange={(e) => setPath(e.target.value)} />
      <p className="text-xs text-muted-foreground">{t('builder.pagePathHint')}</p>
      <label className="flex items-center gap-2 text-sm">
        <Switch checked={menu} onCheckedChange={setMenu} /> {t('visual.pages.inMenu')}
      </label>
      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={!title.trim()}>
          {t('actions.create')}
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={() => onDone()}>
          {t('actions.cancel')}
        </Button>
      </div>
    </form>
  );
}

function PageSettings({ routeId, path, pageId }: { routeId: string; path: string; pageId: string }) {
  const { t } = useTranslation();
  const app = useVisual((s) => s.app);
  const page = readPage(pageId);
  const [address, setAddress] = useState(path);
  if (!page || !app) return null;

  const seo = page.seo ?? {};
  const setSeo = (key: 'title' | 'description' | 'ogImage', value: string) =>
    report(updatePage(pageId, (p) => ({ ...p, seo: { ...p.seo, [key]: value || undefined } })), t);

  return (
    <div className="space-y-3 bg-muted/20 px-3 pb-3 pt-1 text-sm">
      <Field label={t('builder.pageTitle')} value={page.title} onCommit={(title) => title && report(updatePage(pageId, (p) => ({ ...p, title })), t)} />
      <div className="space-y-1">
        <Label htmlFor={`path-${routeId}`}>{t('visual.pages.address')}</Label>
        <Input
          id={`path-${routeId}`}
          value={address}
          disabled={path === '/'}
          onChange={(e) => setAddress(e.target.value)}
          onBlur={() => {
            if (address !== path && !report(setRoutePath(routeId, address), t)) setAddress(path);
          }}
        />
      </div>
      <label className="flex items-center gap-2">
        <Switch checked={inMainMenu(app, path)} onCheckedChange={() => report(toggleMainMenu(path, page.title), t)} /> {t('visual.pages.inMenu')}
      </label>

      <DetailSettings pageId={pageId} path={path} />
      <StateSettings pageId={pageId} />

      <div className="space-y-2 border-t pt-2">
        <div className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">SEO</div>
        <Field label={t('visual.pages.seoTitle')} value={seo.title ?? ''} placeholder={page.title} onCommit={(v) => setSeo('title', v)} />
        <Field label={t('visual.pages.seoDescription')} value={seo.description ?? ''} multiline onCommit={(v) => setSeo('description', v)} />
        <Field label={t('visual.pages.seoImage')} value={seo.ogImage ?? ''} placeholder="/api/media/…/original" onCommit={(v) => setSeo('ogImage', v)} />
        <label className="flex items-center gap-2">
          <Switch
            checked={seo.noIndex === true}
            onCheckedChange={(noIndex) => report(updatePage(pageId, (p) => ({ ...p, seo: { ...p.seo, noIndex: noIndex || undefined } })), t)}
          />
          {t('visual.pages.noIndex')}
        </label>
      </div>

      {path !== '/' && (
        <Button
          size="sm"
          variant="ghost"
          className="text-destructive"
          onClick={() => {
            if (window.confirm(t('visual.pages.confirmDelete', { title: page.title }))) report(deletePage(pageId), t);
          }}
        >
          <Trash2 className="h-4 w-4" /> {t('actions.delete')}
        </Button>
      )}
    </div>
  );
}

/** Defaults every page inherits: the title pattern and the description used where a page has none. */
function SiteSeo() {
  const { t } = useTranslation();
  const app = useVisual((s) => s.app);
  if (!app) return null;
  const set = (key: 'titleTemplate' | 'description', value: string) =>
    report(updateApp((a) => ({ ...a, seo: { ...a.seo, [key]: value || undefined } })), t);
  return (
    <div className="mt-auto space-y-2 border-t p-3 text-sm">
      <div className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('visual.pages.siteSeo')}</div>
      <Field label={t('visual.pages.titleTemplate')} value={app.seo?.titleTemplate ?? ''} placeholder="%s · Acme" onCommit={(v) => set('titleTemplate', v)} />
      <Field label={t('visual.pages.defaultDescription')} value={app.seo?.description ?? ''} multiline onCommit={(v) => set('description', v)} />
      <Field
        label={t('visual.pages.locale')}
        value={app.locale ?? ''}
        placeholder="cs, en-GB"
        onCommit={(v) => report(updateApp((a) => ({ ...a, locale: v.trim() || undefined })), t)}
      />
    </div>
  );
}

function Field({
  label,
  value,
  onCommit,
  placeholder,
  multiline,
}: {
  label: string;
  value: string;
  onCommit: (value: string) => void;
  placeholder?: string;
  multiline?: boolean;
}) {
  const [draft, setDraft] = useState(value);
  const [seen, setSeen] = useState(value);
  if (seen !== value) {
    setSeen(value);
    setDraft(value);
  }
  const id = `f-${label.replace(/\W+/g, '-')}`;
  const commit = () => draft !== value && onCommit(draft.trim());
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{label}</Label>
      {multiline ? (
        <Textarea id={id} rows={2} value={draft} placeholder={placeholder} onChange={(e) => setDraft(e.target.value)} onBlur={commit} />
      ) : (
        <Input
          id={id}
          value={draft}
          placeholder={placeholder}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => e.key === 'Enter' && commit()}
        />
      )}
    </div>
  );
}

/**
 * A detail page shows one item: the one whose slug is in its address (`/events/:slug`). Its
 * components bind to that item exactly as they would inside a collection.
 */
/**
 * The page's state (backlog #123): named values its buttons set and its components show `when`.
 * Typed as `true`, `false`, a number or text; a toggle needs true/false.
 */
function StateSettings({ pageId }: { pageId: string }) {
  const { t } = useTranslation();
  const [name, setName] = useState('');
  const page = readPage(pageId);
  if (!page) return null;
  const state = page.state ?? {};
  const write = (next: Record<string, boolean | number | string>) =>
    report(
      updatePage(pageId, (p) => {
        const { state: _old, ...rest } = p;
        return Object.keys(next).length ? { ...rest, state: next } : rest;
      }),
      t,
    );
  return (
    <div className="space-y-2 border-t pt-2">
      <Label>{t('visual.state.title')}</Label>
      {Object.entries(state).map(([key, value]) => (
        <div key={key} className="flex items-end gap-1">
          <div className="min-w-0 flex-1">
            <Field label={key} value={String(value)} onCommit={(v) => write({ ...state, [key]: parseStateValue(v) })} />
          </div>
          <Button
            size="icon"
            variant="ghost"
            title={t('actions.delete')}
            onClick={() => {
              const next = { ...state };
              delete next[key];
              write(next);
            }}
          >
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      ))}
      <form
        className="flex gap-1"
        onSubmit={(e) => {
          e.preventDefault();
          const key = name.trim();
          if (!key || key in state) return;
          write({ ...state, [key]: false });
          setName('');
        }}
      >
        <Input aria-label={t('visual.state.newName')} placeholder={t('visual.state.newName')} value={name} onChange={(e) => setName(e.target.value)} />
        <Button type="submit" size="icon" variant="outline" disabled={!name.trim()} aria-label={t('visual.state.add')}>
          <Plus className="h-4 w-4" />
        </Button>
      </form>
      <p className="text-xs text-muted-foreground">{t('visual.state.hint')}</p>
    </div>
  );
}

function DetailSettings({ pageId, path }: { pageId: string; path: string }) {
  const { t } = useTranslation();
  const catalog = useContentCatalog();
  const page = readPage(pageId);
  if (!page) return null;
  const param = /:([A-Za-z][A-Za-z0-9]*)/.exec(path)?.[1];
  const current = page.data ? sourceKey(page.data.source) : '__none';

  return (
    <div className="space-y-1 border-t pt-2">
      <Label>{t('visual.data.detailPage')}</Label>
      <Select
        value={current}
        onValueChange={(key) => {
          const found = catalog.sources.find((s) => sourceKey(s.source) === key)?.source;
          report(
            updatePage(pageId, (p) => {
              const next = { ...p, data: found && param ? { source: found, param } : undefined };
              if (!next.data) delete next.data;
              return next;
            }),
            t,
          );
        }}
      >
        <SelectTrigger aria-label={t('visual.data.detailPage')}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="__none">{t('visual.data.notDetail')}</SelectItem>
          {param &&
            catalog.sources.map((s) => (
              <SelectItem key={sourceKey(s.source)} value={sourceKey(s.source)}>
                {s.label}
              </SelectItem>
            ))}
        </SelectContent>
      </Select>
      <p className="text-xs text-muted-foreground">
        {param ? t('visual.data.detailHint', { param }) : t('visual.data.needsParam')}
      </p>
    </div>
  );
}
