import type { Editor } from 'grapesjs';
import { Database, Palette, Settings, SlidersHorizontal } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { BindingPanel } from './panels/BindingPanel';
import { SettingsPanel } from './panels/SettingsPanel';
import { StylesPanel } from './panels/StylesPanel';
import { TraitsPanel } from './panels/TraitsPanel';
import { useBuilder } from './store';

type InspectorTab = 'settings' | 'styles' | 'page' | 'data';

/**
 * The right-hand inspector: what the selected component *is* (its traits and
 * classes), how it *looks* (its styles), and what the page itself is (SEO and
 * theme). Split three ways rather than stacked, because a long scroll of
 * unrelated controls is how a builder becomes unusable on a laptop screen.
 */
export function Inspector({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const [tab, setTab] = useState<InspectorTab>('settings');
  const editingComponent = useBuilder((s) => s.activeKind === 'component');

  const tabs: { id: InspectorTab; icon: typeof Settings; label: string }[] = [
    { id: 'settings', icon: SlidersHorizontal, label: t('builder.settings') },
    // Only while a component template is open: binding attributes mean nothing
    // on a page, and a tab that is inert three quarters of the time teaches an
    // author to ignore it.
    ...(editingComponent
      ? [{ id: 'data' as const, icon: Database, label: t('builder.components.data') }]
      : []),
    { id: 'styles', icon: Palette, label: t('builder.styles') },
    { id: 'page', icon: Settings, label: t('builder.pageSettings') },
  ];

  return (
    <div className="flex h-full flex-col">
      <div className="flex shrink-0 border-b">
        {tabs.map(({ id, icon: Icon, label }) => (
          <button
            key={id}
            type="button"
            title={label}
            onClick={() => setTab(id)}
            className={cn(
              'flex flex-1 items-center justify-center gap-1.5 border-b-2 px-2 py-2 text-xs',
              tab === id
                ? 'border-primary text-foreground'
                : 'border-transparent text-muted-foreground hover:text-foreground',
            )}
          >
            <Icon className="h-4 w-4" />
            <span className="truncate">{label}</span>
          </button>
        ))}
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        {tab === 'settings' && <TraitsPanel editor={editor} />}
        {tab === 'data' && <BindingPanel editor={editor} />}
        {tab === 'styles' && <StylesPanel editor={editor} />}
        {tab === 'page' && <SettingsPanel />}
      </div>
    </div>
  );
}
