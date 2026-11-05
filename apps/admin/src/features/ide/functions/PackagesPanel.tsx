import { useEffect, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Package, Trash2 } from 'lucide-react';
import { Badge, Button, Input } from '@dcms/ui';
import { api } from '../../../lib/api';
import { useVfs } from '../../site-source';
import { declaredPackages, type Ecosystem } from './packages';

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

/**
 * The repository's packages (ADR 0031): what package.json and Functions.csproj declare, and a
 * search to add more. The server writes the manifest and its lock from the registries; they land in
 * the draft like any edit, for the person to commit.
 */
export function PackagesPanel({ functionsApi }: { functionsApi: string }) {
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
    mutationFn: (c: { ecosystem: Ecosystem; name: string; remove: boolean }) => {
      const current = useVfs.getState().files;
      const sent = Object.fromEntries(FILES[c.ecosystem].filter((f) => f in current).map((f) => [f, current[f]]));
      return api.post<{ files: Record<string, string> }>(`${functionsApi}/packages`, { ...c, files: sent });
    },
    onSuccess: (r) => {
      useVfs.getState().importFiles(Object.entries(r.files).map(([path, content]) => ({ path, content })));
      setQuery('');
    },
  });

  return (
    <div className="border-t pt-3" data-testid="functions-packages">
      <p className="flex items-center gap-1.5 font-medium">
        <Package className="h-4 w-4" /> {t('ide.fn.packages.title')}
      </p>
      {declared.length === 0 ? (
        <p className="text-xs text-muted-foreground">{t('ide.fn.packages.none')}</p>
      ) : (
        <ul className="mt-1 space-y-1">
          {declared.map((p) => (
            <li key={`${p.ecosystem}:${p.name}`} className="flex items-center gap-2">
              <Badge tone="secondary">{p.ecosystem === 'npm' ? 'npm' : 'NuGet'}</Badge>
              <span className="truncate font-mono text-xs">{p.name}</span>
              <span className="truncate font-mono text-xs text-muted-foreground">{p.version}</span>
              <span className="flex-1" />
              <Button
                size="icon"
                variant="ghost"
                aria-label={t('ide.fn.packages.remove', { name: p.name })}
                title={t('ide.fn.packages.remove', { name: p.name })}
                disabled={change.isPending}
                onClick={() => change.mutate({ ecosystem: p.ecosystem, name: p.name, remove: true })}
              >
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </li>
          ))}
        </ul>
      )}

      <div className="mt-2 flex gap-1">
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
      {change.isError && <p className="mt-1 text-xs text-destructive">{(change.error as Error).message}</p>}
      {search.isError && <p className="mt-1 text-xs text-destructive">{(search.error as Error).message}</p>}
      {debounced.length >= 2 && search.data && (
        <ul className="mt-1 max-h-56 space-y-1 overflow-y-auto" data-testid="functions-packages-found">
          {search.data.length === 0 && <li className="text-xs text-muted-foreground">{t('ide.fn.packages.noResults')}</li>}
          {search.data.map((p) => (
            <li key={p.name} className="rounded border p-1.5">
              <div className="flex items-center gap-2">
                <span className="truncate font-mono text-xs font-medium">{p.name}</span>
                <span className="font-mono text-xs text-muted-foreground">{p.version}</span>
                <span className="flex-1" />
                <Button
                  size="sm"
                  variant="outline"
                  className="h-6 px-2 text-xs"
                  disabled={change.isPending}
                  onClick={() => change.mutate({ ecosystem, name: p.name, remove: false })}
                >
                  {t('ide.fn.packages.add')}
                </Button>
              </div>
              {p.description && <p className="mt-0.5 line-clamp-2 text-xs text-muted-foreground">{p.description}</p>}
            </li>
          ))}
        </ul>
      )}
      <p className="mt-2 text-xs text-muted-foreground">{t('ide.fn.packages.hint')}</p>
    </div>
  );
}
