import { useEffect, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ArrowUpCircle, FileCode, Trash2 } from 'lucide-react';
import { Badge, Button, Input } from '@dcms/ui';
import { api } from '../../../lib/api';
import { useVfs } from '../../site-source';
import { declaredPackages, type DeclaredPackage, type Ecosystem } from './packages';

interface Found {
  name: string;
  version: string;
  description?: string | null;
}

/** The files a package change reads and writes: the manifest and its lock. */
const FILES: Record<Ecosystem, string[]> = {
  npm: ['package.json', 'package-lock.json'],
  nuget: ['Functions.csproj', 'packages.lock.json'],
};

interface Change {
  ecosystem: Ecosystem;
  name: string;
  /** A range (npm) or exact version (NuGet); absent takes the latest. */
  version?: string;
  remove: boolean;
}

/**
 * The Packages view of a Functions app's IDE (ADR 0031): what package.json and Functions.csproj
 * declare — each one's version changed, updated to the latest or removed — and a search to add
 * more. The server writes the manifest and its lock from the registries; they land in the draft
 * like any edit, for the person to commit.
 */
export function PackagesView({ functionsApi }: { functionsApi: string }) {
  const { t } = useTranslation();
  const files = useVfs((s) => s.files);
  const [ecosystem, setEcosystem] = useState<Ecosystem>('npm');
  const [query, setQuery] = useState('');
  const [debounced, setDebounced] = useState('');
  useEffect(() => {
    const id = setTimeout(() => setDebounced(query.trim()), 300);
    return () => clearTimeout(id);
  }, [query]);

  const declared = declaredPackages(files);
  const search = useQuery({
    queryKey: ['functions-packages-search', functionsApi, ecosystem, debounced],
    queryFn: () => api.get<Found[]>(`${functionsApi}/packages/search?ecosystem=${ecosystem}&q=${encodeURIComponent(debounced)}`),
    enabled: debounced.length >= 2,
    staleTime: 60_000,
  });
  const change = useMutation({
    mutationFn: (c: Change) => {
      const current = useVfs.getState().files;
      const sent = Object.fromEntries(FILES[c.ecosystem].filter((f) => f in current).map((f) => [f, current[f]]));
      return api.post<{ files: Record<string, string> }>(`${functionsApi}/packages`, { ...c, files: sent });
    },
    onSuccess: (r, c) => {
      useVfs.getState().importFiles(Object.entries(r.files).map(([path, content]) => ({ path, content })));
      if (!c.remove && declared.every((p) => p.name !== c.name)) setQuery('');
    },
  });

  const manifests = (['package.json', 'Functions.csproj'] as const).filter((f) => f in files);

  return (
    <div className="flex h-full flex-col overflow-hidden text-sm" data-testid="functions-packages">
      <div className="flex items-center gap-1 border-b px-3 py-2">
        <span className="font-medium">{t('ide.fn.packages.title')}</span>
        <span className="flex-1" />
        {manifests.map((f) => (
          <Button
            key={f}
            size="icon"
            variant="ghost"
            aria-label={t('ide.fn.packages.openManifest', { file: f })}
            title={t('ide.fn.packages.openManifest', { file: f })}
            onClick={() => useVfs.getState().open(f)}
          >
            <FileCode className="h-4 w-4" />
          </Button>
        ))}
      </div>

      <div className="flex-1 space-y-3 overflow-y-auto p-3">
        <section>
          <p className="mb-1 text-xs font-medium uppercase text-muted-foreground">{t('ide.fn.packages.installed', { count: declared.length })}</p>
          {declared.length === 0 ? (
            <p className="text-xs text-muted-foreground">{t('ide.fn.packages.none')}</p>
          ) : (
            <ul className="space-y-1">
              {declared.map((p) => (
                <DeclaredRow
                  key={`${p.ecosystem}:${p.name}`}
                  pkg={p}
                  busy={change.isPending}
                  onChange={(version) => change.mutate({ ecosystem: p.ecosystem, name: p.name, version, remove: false })}
                  onRemove={() => change.mutate({ ecosystem: p.ecosystem, name: p.name, remove: true })}
                />
              ))}
            </ul>
          )}
        </section>

        {change.isError && <p className="text-xs text-destructive">{(change.error as Error).message}</p>}

        <section className="border-t pt-3">
          <p className="mb-1 text-xs font-medium uppercase text-muted-foreground">{t('ide.fn.packages.addTitle')}</p>
          <div className="flex gap-1">
            <select
              aria-label={t('ide.fn.packages.ecosystem')}
              className="h-8 rounded-md border bg-background px-1 text-xs"
              value={ecosystem}
              onChange={(e) => setEcosystem(e.target.value as Ecosystem)}
            >
              <option value="npm">npm</option>
              <option value="nuget">NuGet</option>
            </select>
            <Input
              className="h-8 text-xs"
              placeholder={t('ide.fn.packages.search')}
              aria-label={t('ide.fn.packages.search')}
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
          </div>
          {search.isError && <p className="mt-1 text-xs text-destructive">{(search.error as Error).message}</p>}
          {debounced.length >= 2 && search.data && (
            <ul className="mt-1 space-y-1" data-testid="functions-packages-found">
              {search.data.length === 0 && <li className="text-xs text-muted-foreground">{t('ide.fn.packages.noResults')}</li>}
              {search.data.map((p) => {
                const have = declared.find((d) => d.ecosystem === ecosystem && d.name.toLowerCase() === p.name.toLowerCase());
                return (
                  <li key={p.name} className="rounded border p-1.5">
                    <div className="flex items-center gap-2">
                      <span className="truncate font-mono text-xs font-medium">{p.name}</span>
                      <span className="font-mono text-xs text-muted-foreground">{p.version}</span>
                      <span className="flex-1" />
                      {have ? (
                        <Badge tone="secondary">{t('ide.fn.packages.added')}</Badge>
                      ) : (
                        <Button
                          size="sm"
                          variant="outline"
                          className="h-6 px-2 text-xs"
                          disabled={change.isPending}
                          onClick={() => change.mutate({ ecosystem, name: p.name, remove: false })}
                        >
                          {t('ide.fn.packages.add')}
                        </Button>
                      )}
                    </div>
                    {p.description && <p className="mt-0.5 line-clamp-2 text-xs text-muted-foreground">{p.description}</p>}
                  </li>
                );
              })}
            </ul>
          )}
        </section>
        <p className="text-xs text-muted-foreground">{t('ide.fn.packages.hint')}</p>
      </div>
    </div>
  );
}

/** One declared package: its version edited in place (Enter applies it), updated to the latest, or removed. */
function DeclaredRow({
  pkg,
  busy,
  onChange,
  onRemove,
}: {
  pkg: DeclaredPackage;
  busy: boolean;
  onChange: (version?: string) => void;
  onRemove: () => void;
}) {
  const { t } = useTranslation();
  const [version, setVersion] = useState(pkg.version);
  useEffect(() => setVersion(pkg.version), [pkg.version]);
  const edited = version.trim() !== '' && version.trim() !== pkg.version;

  return (
    <li className="rounded border p-1.5" data-testid={`package-${pkg.name}`}>
      <div className="flex items-center gap-2">
        <Badge tone="secondary">{pkg.ecosystem === 'npm' ? 'npm' : 'NuGet'}</Badge>
        <span className="min-w-0 flex-1 truncate font-mono text-xs" title={pkg.name}>
          {pkg.name}
        </span>
        <Button
          size="icon"
          variant="ghost"
          className="h-6 w-6"
          aria-label={t('ide.fn.packages.update', { name: pkg.name })}
          title={t('ide.fn.packages.update', { name: pkg.name })}
          disabled={busy}
          onClick={() => onChange(undefined)}
        >
          <ArrowUpCircle className="h-3.5 w-3.5" />
        </Button>
        <Button
          size="icon"
          variant="ghost"
          className="h-6 w-6"
          aria-label={t('ide.fn.packages.remove', { name: pkg.name })}
          title={t('ide.fn.packages.remove', { name: pkg.name })}
          disabled={busy}
          onClick={onRemove}
        >
          <Trash2 className="h-3.5 w-3.5" />
        </Button>
      </div>
      <form
        className="mt-1 flex gap-1"
        onSubmit={(e) => {
          e.preventDefault();
          if (edited) onChange(version.trim());
        }}
      >
        <Input
          className="h-6 font-mono text-xs"
          aria-label={t('ide.fn.packages.version', { name: pkg.name })}
          value={version}
          onChange={(e) => setVersion(e.target.value)}
          onKeyDown={(e) => e.key === 'Escape' && setVersion(pkg.version)}
        />
        {edited && (
          <Button type="submit" size="sm" variant="outline" className="h-6 px-2 text-xs" disabled={busy}>
            {t('ide.fn.packages.apply')}
          </Button>
        )}
      </form>
    </li>
  );
}
