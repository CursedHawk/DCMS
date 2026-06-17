import { useMutation, useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import {
  ArrowLeft,
  Monitor,
  Redo2,
  Rocket,
  Smartphone,
  Sparkles,
  Tablet,
  Undo2,
} from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '../../components/ui/button';
import { Hint } from '../../components/ui/tooltip';
import { CenteredSpinner } from '../../components/ui/spinner';
import { cn } from '../../lib/cn';
import { api } from '../../lib/api';
import { usePluginInstances } from '../plugins/api';
import { AiPanel } from './AiPanel';
import { Canvas } from './Canvas';
import { Inspector } from './Inspector';
import { LeftPanel } from './LeftPanel';
import { BREAKPOINTS } from './constants';
import { useEditor } from './store';
import type { Breakpoint } from '@dcms/editor-core';

const bpIcon = { desktop: Monitor, tablet: Tablet, mobile: Smartphone } as const;
// Widen for TanStack Link typing (sibling routes are registered via a helper).
const sitesPath: string = '/sites';

export function EditorPage({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const editor = useEditor();
  const breakpoint = useEditor((s) => s.breakpoint);
  const dirty = useEditor((s) => s.dirty);
  const def = useEditor((s) => s.history.present);
  const [aiOpen, setAiOpen] = useState(false);
  const [status, setStatus] = useState('');
  const loadedFor = useRef<string | null>(null);

  const site = useQuery({
    queryKey: ['site', siteId],
    queryFn: () => api.get<{ name: string; definition: never }>(`/admin/sites/${siteId}`),
  });
  const instances = usePluginInstances();
  const enabledPluginIds = new Set(
    (instances.data ?? []).filter((i) => i.enabled).map((i) => i.pluginId),
  );

  // Load the definition into the store once per site.
  useEffect(() => {
    if (site.data && loadedFor.current !== siteId) {
      loadedFor.current = siteId;
      editor.load(site.data.definition);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [site.data, siteId]);

  const save = useMutation({
    mutationFn: () => api.put(`/admin/sites/${siteId}/definition`, editor.definition()),
    onSuccess: () => {
      editor.markSaved();
      setStatus(t('common.saved'));
    },
  });

  // Debounced autosave on any change.
  useEffect(() => {
    if (!dirty || loadedFor.current !== siteId) return;
    const id = setTimeout(() => save.mutate(), 1200);
    return () => clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [def, dirty]);

  const publish = useMutation({
    mutationFn: async () => {
      await api.put(`/admin/sites/${siteId}/definition`, editor.definition());
      editor.markSaved();
      return api.post<{ buildId: string }>(`/admin/sites/${siteId}/publish`);
    },
    onSuccess: () => toast.success(t('editor.publishQueued')),
    onError: () => toast.error(t('errors.generic')),
  });

  if (site.isLoading) return <CenteredSpinner label={t('common.loading')} />;

  return (
    <div className="flex h-[calc(100vh-3.5rem)] flex-col">
      {/* Toolbar */}
      <div className="flex h-12 shrink-0 items-center gap-2 border-b bg-card px-3">
        <Link to={sitesPath}>
          <Button size="icon" variant="ghost">
            <ArrowLeft className="h-4 w-4" />
          </Button>
        </Link>
        <span className="font-medium">{site.data?.name}</span>

        <div className="mx-2 flex items-center gap-1 rounded-md border bg-muted p-0.5">
          {BREAKPOINTS.map((bp) => {
            const Icon = bpIcon[bp];
            return (
              <Hint key={bp} label={t(`editor.${bp}`)}>
                <button
                  type="button"
                  onClick={() => editor.setBreakpoint(bp as Breakpoint)}
                  className={cn(
                    'flex h-7 w-8 items-center justify-center rounded',
                    breakpoint === bp ? 'bg-background shadow-sm' : 'text-muted-foreground',
                  )}
                >
                  <Icon className="h-4 w-4" />
                </button>
              </Hint>
            );
          })}
        </div>

        <Button size="icon" variant="ghost" disabled={!editor.canUndo()} onClick={editor.undo}>
          <Undo2 className="h-4 w-4" />
        </Button>
        <Button size="icon" variant="ghost" disabled={!editor.canRedo()} onClick={editor.redo}>
          <Redo2 className="h-4 w-4" />
        </Button>

        <div className="flex-1" />
        <span className="text-xs text-muted-foreground">{dirty ? t('common.saving') : status}</span>
        <Button variant="outline" onClick={() => setAiOpen(true)}>
          <Sparkles className="h-4 w-4" /> {t('editor.aiAssist')}
        </Button>
        <Button onClick={() => publish.mutate()} disabled={publish.isPending}>
          <Rocket className="h-4 w-4" /> {t('actions.publish')}
        </Button>
      </div>

      {/* Workspace */}
      <div className="flex flex-1 overflow-hidden">
        <LeftPanel enabledPluginIds={enabledPluginIds} />
        <Canvas />
        <Inspector instances={instances.data ?? []} />
      </div>

      <AiPanel open={aiOpen} onOpenChange={setAiOpen} />
    </div>
  );
}
