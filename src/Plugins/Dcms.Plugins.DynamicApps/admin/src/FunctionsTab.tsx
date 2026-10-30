import { useQueries, useQuery } from '@tanstack/react-query';
import { Code, Sparkles } from 'lucide-react';
import { Badge, Button, Callout, CenteredSpinner } from '@dcms/ui';
import { usePluginApi, usePluginHost, usePluginT } from '@dcms/plugin-ui';

interface Instance {
  id: string;
  pluginId: string;
  slug: string;
  name: string;
  enabled: boolean;
  aiToolsEnabled?: boolean;
}

interface CodeFunction {
  name: string;
  description?: string | null;
  triggers: Array<Record<string, unknown>>;
  http: string[];
  uses: { tables?: Record<string, string[]> };
}

/**
 * What runs on this app's data (ADR 0029): the functions of the workspace's Functions apps that
 * this app's tables trigger or that use them, a way into their code, and the assistant to write
 * one. Functions live in their own app; this is where a person working on the data finds them.
 */
export function FunctionsTab({ slug, appName }: { slug: string; appName: string }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const host = usePluginHost();
  const instances = useQuery({
    queryKey: ['plugin-instances'],
    queryFn: () => api.get<Instance[]>('/admin/plugins/instances'),
  });
  const apps = (instances.data ?? []).filter((i) => i.pluginId === 'functions' && i.enabled);
  const overviews = useQueries({
    queries: apps.map((app) => ({
      queryKey: ['functions', app.slug, 'overview'],
      queryFn: () =>
        api.post<{ live: number | null; functions: CodeFunction[] }>(
          `/admin/contracts/functions.code@1/Overview?instance=${encodeURIComponent(app.slug)}`, {}),
    })),
  });

  const ours = (f: CodeFunction) =>
    f.triggers.some((tr) => String(tr.table ?? tr.relationship ?? '').startsWith(`${slug}.`))
    || Object.keys(f.uses.tables ?? {}).some((table) => table.startsWith(`${slug}.`));

  const automate = async (app: Instance) => {
    if (!host.assistant) return;
    if (!app.aiToolsEnabled) await host.assistant.enableTools(app.id);
    host.assistant.open(t('functions.prompt', { app: appName, slug, functions: app.name }));
  };

  if (instances.isLoading) return <CenteredSpinner />;
  if (apps.length === 0) {
    return (
      <div className="space-y-3 pt-2">
        <Callout>{t('functions.none')}</Callout>
        <Button variant="outline" onClick={() => host.navigate('/marketplace')}>{t('functions.install')}</Button>
      </div>
    );
  }

  return (
    <div className="space-y-6 pt-2">
      <p className="text-sm text-muted-foreground">{t('functions.hint')}</p>
      {apps.map((app, i) => {
        const functions = (overviews[i]?.data?.functions ?? []).filter(ours);
        return (
          <section key={app.id} className="space-y-3 rounded-lg border p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h3 className="text-sm font-semibold">{app.name}</h3>
              <div className="flex gap-2">
                <Button variant="outline" size="sm" onClick={() => host.navigate(`/apps/${app.slug}/code`)}>
                  <Code className="h-4 w-4" /> {t('functions.open')}
                </Button>
                {host.assistant ? (
                  <Button size="sm" onClick={() => void automate(app)}>
                    <Sparkles className="h-4 w-4" /> {t('functions.automate')}
                  </Button>
                ) : null}
              </div>
            </div>
            {overviews[i]?.isError ? (
              <p className="text-sm text-muted-foreground">{t('functions.unreadable')}</p>
            ) : functions.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t('functions.empty')}</p>
            ) : (
              <ul className="divide-y">
                {functions.map((f) => (
                  <li key={f.name} className="flex flex-wrap items-center gap-2 py-2">
                    <span className="font-mono text-sm">{f.name}</span>
                    {f.triggers.map((tr, k) => <Badge key={k} tone="outline">{String(tr.kind)}{tr.event ? ` ${String(tr.event)}` : ''}</Badge>)}
                    {f.description ? <span className="text-sm text-muted-foreground">{f.description}</span> : null}
                  </li>
                ))}
              </ul>
            )}
          </section>
        );
      })}
    </div>
  );
}
