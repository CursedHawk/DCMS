import { type ComponentNode, effectiveLayout } from '@dcms/editor-core';
import { findRegistration } from '@dcms/site-components';
import { ArrowDownToLine, ArrowUpToLine, Copy, MousePointerClick, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Label } from '../../components/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../components/ui/select';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { MediaPicker } from '../media/MediaPicker';
import type { PluginInstance } from '../plugins/api';
import { useEditor } from './store';

interface SchemaProp { type?: string; title?: string; enum?: string[] }

export function Inspector({ instances }: { instances: PluginInstance[] }) {
  const { t } = useTranslation();
  const nodes = useEditor((s) => s.currentNodes());
  const selectedId = useEditor((s) => s.selectedId);
  const node = nodes.find((n) => n.id === selectedId) ?? null;

  return (
    <aside className="flex w-72 shrink-0 flex-col border-l bg-card">
      <Tabs defaultValue="element" className="flex flex-1 flex-col">
        <TabsList className="m-2">
          <TabsTrigger value="element" className="flex-1">
            {t('editor.inspect')}
          </TabsTrigger>
          <TabsTrigger value="page" className="flex-1">
            {t('editor.pages')}
          </TabsTrigger>
          <TabsTrigger value="theme" className="flex-1">
            {t('editor.theme')}
          </TabsTrigger>
        </TabsList>
        <div className="flex-1 overflow-y-auto p-3">
          <TabsContent value="element" className="mt-0">
            {node ? <ElementInspector node={node} instances={instances} /> : <EmptyInspector />}
          </TabsContent>
          <TabsContent value="page" className="mt-0">
            <PageInspector />
          </TabsContent>
          <TabsContent value="theme" className="mt-0">
            <ThemeInspector />
          </TabsContent>
        </div>
      </Tabs>
    </aside>
  );
}

function EmptyInspector() {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col items-center gap-2 py-10 text-center text-sm text-muted-foreground">
      <MousePointerClick className="h-6 w-6" />
      {t('editor.inspect')}
    </div>
  );
}

function ElementInspector({ node, instances }: { node: ComponentNode; instances: PluginInstance[] }) {
  const { t } = useTranslation();
  const breakpoint = useEditor((s) => s.breakpoint);
  const updateProps = useEditor((s) => s.updateSelectedProps);
  const updateLayout = useEditor((s) => s.updateLayout);
  const setBinding = useEditor((s) => s.setBinding);
  const removeSelected = useEditor((s) => s.removeSelected);
  const duplicate = useEditor((s) => s.duplicateSelected);
  const bringToFront = useEditor((s) => s.bringToFront);
  const sendToBack = useEditor((s) => s.sendToBack);

  const reg = findRegistration(node.type);
  const props = ((reg?.propSchema as { properties?: Record<string, SchemaProp> })?.properties ?? {}) as Record<
    string,
    SchemaProp
  >;
  const values = node.props as Record<string, string>;
  const box = effectiveLayout(node, breakpoint) ?? { x: 0, y: 0, w: 0, h: 0 };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <span className="font-semibold">{reg?.displayName ?? node.type}</span>
        <div className="flex gap-1">
          <Button size="icon" variant="ghost" title={t('editor.duplicate')} onClick={duplicate}>
            <Copy className="h-4 w-4" />
          </Button>
          <Button size="icon" variant="ghost" className="text-destructive" onClick={removeSelected}>
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {/* Position & size */}
      <div>
        <Label className="mb-1.5 block text-xs uppercase text-muted-foreground">
          {t('editor.position')} · {t('editor.size')}
          {breakpoint !== 'desktop' ? ` (${t(`editor.${breakpoint}`)})` : ''}
        </Label>
        <div className="grid grid-cols-2 gap-2">
          <NumField label="X" value={box.x} onChange={(v) => updateLayout(node.id, { x: v })} />
          <NumField label="Y" value={box.y} onChange={(v) => updateLayout(node.id, { y: v })} />
          <NumField label="W" value={box.w} onChange={(v) => updateLayout(node.id, { w: v })} />
          <NumField label="H" value={box.h} onChange={(v) => updateLayout(node.id, { h: v })} />
        </div>
        <div className="mt-2 flex gap-2">
          <Button size="sm" variant="outline" className="flex-1" onClick={bringToFront}>
            <ArrowUpToLine className="h-3.5 w-3.5" /> {t('editor.bringToFront')}
          </Button>
          <Button size="sm" variant="outline" className="flex-1" onClick={sendToBack}>
            <ArrowDownToLine className="h-3.5 w-3.5" /> {t('editor.sendToBack')}
          </Button>
        </div>
      </div>

      {/* Props */}
      <div className="space-y-3">
        {Object.entries(props).map(([name, schema]) => {
          if (node.type === 'Image' && name === 'src') {
            return (
              <div key={name} className="space-y-1.5">
                <Label>{schema.title ?? name}</Label>
                <MediaPicker
                  value={values[name]}
                  category="Image"
                  onChange={(id) => updateProps({ [name]: id ?? '' })}
                />
              </div>
            );
          }
          return (
            <div key={name} className="space-y-1.5">
              <Label>{schema.title ?? name}</Label>
              {schema.enum ? (
                <Select value={values[name] ?? ''} onValueChange={(v) => updateProps({ [name]: v })}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {schema.enum.map((o) => (
                      <SelectItem key={o} value={o}>
                        {o}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              ) : (
                <Input value={values[name] ?? ''} onChange={(e) => updateProps({ [name]: e.target.value })} />
              )}
            </div>
          );
        })}
      </div>

      {/* Data binding */}
      {reg?.binding ? (
        <div className="space-y-1.5">
          <Label>
            {t('editor.dataSource')} ({reg.binding.contentType})
          </Label>
          <Select
            value={node.bindings?.[0]?.source.instanceSlug ?? ''}
            onValueChange={(v) => setBinding(node.id, reg.binding!.propPath, v || null)}
          >
            <SelectTrigger>
              <SelectValue placeholder="—" />
            </SelectTrigger>
            <SelectContent>
              {instances
                .filter((i) => i.enabled && i.pluginId === reg.requiredPluginId)
                .map((i) => (
                  <SelectItem key={i.id} value={i.slug}>
                    {i.name}
                  </SelectItem>
                ))}
            </SelectContent>
          </Select>
        </div>
      ) : null}
    </div>
  );
}

/** Layout values are whole grid units, so a typed fraction is committed rounded. */
function toGridUnits(text: string): number | undefined {
  const n = Number(text);
  return text.trim() === '' || !Number.isFinite(n) ? undefined : Math.round(n);
}

/**
 * One layout box number (X/Y/W/H).
 *
 * The text is held locally rather than re-derived from `value` on every
 * keystroke: bound directly, an emptied field parsed as `Number('') === 0` and
 * snapped the element to the origin — or to zero size — before a new number
 * could be typed, and a lone `-` parsed as `NaN` and poisoned the layout. An
 * incomplete entry now simply commits nothing, and blur restores the last value.
 */
function NumField({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  const external = Math.round(value);
  const [draft, setDraft] = useState(String(external));
  const [seen, setSeen] = useState(external);

  // Adopt a value changed on the canvas (drag, resize, another node selected),
  // but leave a draft that already means it — "12.5" commits as 13 — alone.
  if (seen !== external) {
    setSeen(external);
    if (toGridUnits(draft) !== external) setDraft(String(external));
  }

  return (
    <label className="flex items-center gap-1.5 rounded-md border px-2 text-xs">
      <span className="text-muted-foreground">{label}</span>
      <input
        type="number"
        value={draft}
        onChange={(e) => {
          setDraft(e.target.value);
          const n = toGridUnits(e.target.value);
          if (n !== undefined) onChange(n);
        }}
        onBlur={() => setDraft(String(Math.round(value)))}
        className="w-full bg-transparent py-1.5 outline-none"
      />
    </label>
  );
}

function PageInspector() {
  const { t } = useTranslation();
  const page = useEditor((s) => s.currentPage());
  const update = useEditor((s) => s.updatePageMeta);
  if (!page) return null;
  return (
    <div className="space-y-3">
      <div className="space-y-1.5">
        <Label>{t('common.name')}</Label>
        <Input value={page.title} onChange={(e) => update({ title: e.target.value })} />
      </div>
      <div className="space-y-1.5">
        <Label>Path</Label>
        <Input value={page.path} onChange={(e) => update({ path: e.target.value })} />
      </div>
      <div className="space-y-1.5">
        <Label>SEO title</Label>
        <Input value={page.seo.title} onChange={(e) => update({ seoTitle: e.target.value })} />
      </div>
      <div className="space-y-1.5">
        <Label>SEO description</Label>
        <Input
          value={page.seo.description ?? ''}
          onChange={(e) => update({ seoDescription: e.target.value })}
        />
      </div>
    </div>
  );
}

function ThemeInspector() {
  const theme = useEditor((s) => s.history.present.theme);
  const updateTheme = useEditor((s) => s.updateTheme);
  const setColor = (key: string, value: string) =>
    updateTheme({ colors: { ...theme.colors, [key]: value } });

  const colors = ['primary', 'background', 'foreground', 'accent'];
  return (
    <div className="space-y-3">
      {colors.map((key) => (
        <div key={key} className="flex items-center justify-between gap-2">
          <Label className="capitalize">{key}</Label>
          <input
            type="color"
            value={theme.colors[key] ?? '#6366f1'}
            onChange={(e) => setColor(key, e.target.value)}
            className="h-8 w-12 cursor-pointer rounded border bg-transparent"
          />
        </div>
      ))}
      <div className="space-y-1.5">
        <Label>Radius</Label>
        <Input
          value={theme.radius ?? ''}
          placeholder="0.5rem"
          onChange={(e) => updateTheme({ radius: e.target.value })}
        />
      </div>
    </div>
  );
}
