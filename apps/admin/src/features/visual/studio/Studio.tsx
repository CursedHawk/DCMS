import type { Registry } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { Check, Puzzle } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, TourTarget, cn } from '@dcms/ui';
import { PropsPanel } from '../PropsPanel';
import { componentUsage, readApp } from '../documents';
import { useVisual } from '../store';
import { SettingsDesigner, SlotDesigner } from './Designers';
import { InstancePreview, VersionsPanel } from './StudioSide';
import { useStudioDoc } from './useStudioDoc';

/**
 * The component studio's header (Mode D v2, U4.1): which component, what it is called and filed
 * under, which version, and Done — back to the page it was opened from, with its instance
 * selected.
 */
export function StudioBar() {
  const { t } = useTranslation();
  const studio = useStudioDoc();
  const back = useVisual((s) => s.studioReturn);
  if (!studio) return null;
  const { doc, change, name, version } = studio;
  const used = componentUsage(name).get(version) ?? 0;
  const done = () => {
    if (back) return useVisual.getState().leaveStudio();
    const home = readApp()?.routes.find((r) => r.path === '/');
    if (home) useVisual.getState().setTarget({ kind: 'page', id: home.page });
  };
  return (
    <div role="region" aria-label={t('visual.studio.title')} className="flex shrink-0 flex-wrap items-center gap-2 border-b bg-primary/5 px-3 py-1.5 text-sm">
      <Puzzle className="h-4 w-4 text-primary" />
      <span className="font-medium">{t('visual.studio.title')}</span>
      <Commit label={t('visual.studio.name')} value={doc.label} onCommit={(label) => label && change((d) => ({ ...d, label }))} className="w-44" />
      <Commit label={t('visual.studio.category')} value={doc.category ?? ''} placeholder={t('visual.studio.categoryHint')} onCommit={(category) => change((d) => ({ ...d, category: category || undefined }))} className="w-32" />
      <span className="rounded border px-1.5 py-0.5 text-xs text-muted-foreground">
        v{version} · {used ? t('visual.mine.used', { count: used }) : t('visual.studio.unused')}
      </span>
      <div className="flex-1" />
      <TourTarget id="studio.done">
        <Button size="sm" onClick={done}>
          <Check className="h-4 w-4" /> {back ? t('visual.studio.done') : t('visual.studio.close')}
        </Button>
      </TourTarget>
    </div>
  );
}

/** A text field that writes when it is left or Enter is pressed, not on every key. */
function Commit({ label, value, placeholder, onCommit, className }: { label: string; value: string; placeholder?: string; onCommit: (v: string) => void; className?: string }) {
  const [draft, setDraft] = useState<string | null>(null);
  const commit = () => {
    if (draft !== null && draft.trim() !== value) onCommit(draft.trim());
    setDraft(null);
  };
  return (
    <Input
      aria-label={label}
      title={label}
      value={draft ?? value}
      placeholder={placeholder}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => e.key === 'Enter' && commit()}
      className={cn('h-7 text-xs', className)}
    />
  );
}

type StudioTab = 'part' | 'settings' | 'slots' | 'preview' | 'versions';
const TABS: StudioTab[] = ['part', 'settings', 'slots', 'preview', 'versions'];

/**
 * The studio's right side: the selected part's own settings, and the four things that make a
 * component — which settings pages get, which places pages fill, what an instance looks like,
 * and its versions.
 */
export function StudioPanel({ editor, registry }: { editor: Editor | null; registry: Registry }) {
  const { t } = useTranslation();
  const [tab, setTab] = useState<StudioTab>('settings');
  const studio = useStudioDoc();
  const panels: Record<StudioTab, ReactNode> = {
    part: <PropsPanel editor={editor} registry={registry} />,
    settings: studio && <SettingsDesigner editor={editor} registry={registry} studio={studio} />,
    slots: studio && <SlotDesigner editor={editor} registry={registry} studio={studio} />,
    preview: studio && <InstancePreview registry={registry} studio={studio} />,
    versions: studio && <VersionsPanel studio={studio} />,
  };
  return (
    <div className="flex h-full flex-col">
      <div role="tablist" aria-label={t('visual.studio.title')} className="flex shrink-0 border-b px-1">
        {TABS.map((k) => (
          <TourTarget key={k} id={`studio.tab.${k}`}>
            <button
              type="button"
              role="tab"
              aria-selected={tab === k}
              onClick={() => setTab(k)}
              className={cn('-mb-px border-b-2 px-2 py-2 text-xs', tab === k ? 'border-primary font-medium' : 'border-transparent text-muted-foreground hover:text-foreground')}
            >
              {t(`visual.studio.tabs.${k}`)}
            </button>
          </TourTarget>
        ))}
      </div>
      <div role="tabpanel" className="min-h-0 flex-1 overflow-y-auto">
        {panels[tab]}
      </div>
    </div>
  );
}
