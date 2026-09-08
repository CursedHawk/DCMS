import { CaseSensitive, ChevronDown, ChevronRight, Regex, Search, WholeWord } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { cn, Input, useDebounced } from '@dcms/ui';
import { groupByFile, MAX_MATCHES, searchFiles, type SearchOptions } from './search';
import { useVfs } from './vfs';

/**
 * Project-wide search, in the IDE's left sidebar.
 *
 * <p>Searches the working draft rather than the repository, and that is the point: the draft is
 * what the author is looking at and the only version they can act on. It is also already in
 * memory, so this needs no request and no worker — a site's few hundred files scan in
 * microseconds, behind a debounce so it happens once per pause rather than once per key.</p>
 */
export function SearchView() {
  const { t } = useTranslation();
  const files = useVfs((s) => s.files);
  const revealAt = useVfs((s) => s.revealAt);

  const [query, setQuery] = useState('');
  const [options, setOptions] = useState<SearchOptions>({
    caseSensitive: false,
    wholeWord: false,
    useRegex: false,
  });
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());

  const settled = useDebounced(query);
  const result = useMemo(() => searchFiles(files, settled, options), [files, settled, options]);
  const groups = useMemo(() => groupByFile(result.matches), [result.matches]);

  const toggle = (path: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="shrink-0 space-y-1.5 border-b p-2">
        <div className="relative">
          <Search
            className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground"
            aria-hidden
          />
          <Input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('ide.search.placeholder')}
            aria-label={t('ide.search.placeholder')}
            className="h-7 pl-7 text-xs"
          />
        </div>

        <div className="flex items-center gap-0.5">
          <Toggle
            active={options.caseSensitive}
            label={t('ide.search.caseSensitive')}
            onClick={() => setOptions((o) => ({ ...o, caseSensitive: !o.caseSensitive }))}
          >
            <CaseSensitive className="h-3.5 w-3.5" aria-hidden />
          </Toggle>
          <Toggle
            active={options.wholeWord}
            label={t('ide.search.wholeWord')}
            onClick={() => setOptions((o) => ({ ...o, wholeWord: !o.wholeWord }))}
          >
            <WholeWord className="h-3.5 w-3.5" aria-hidden />
          </Toggle>
          <Toggle
            active={options.useRegex}
            label={t('ide.search.regex')}
            onClick={() => setOptions((o) => ({ ...o, useRegex: !o.useRegex }))}
          >
            <Regex className="h-3.5 w-3.5" aria-hidden />
          </Toggle>

          <p className="ml-auto truncate text-[11px] text-muted-foreground" aria-live="polite">
            {settled.length === 0
              ? null
              : result.error
                ? t('ide.search.badPattern')
                : result.truncated
                  ? t('ide.search.tooMany', { count: MAX_MATCHES })
                  : t('ide.search.summary', {
                      matches: result.matches.length,
                      files: groups.length,
                    })}
          </p>
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        {groups.map((group) => {
          const open = !collapsed.has(group.path);
          return (
            <div key={group.path}>
              <button
                type="button"
                onClick={() => toggle(group.path)}
                aria-expanded={open}
                className="flex w-full items-center gap-1 px-2 py-1 text-left text-xs hover:bg-accent/50"
                title={group.path}
              >
                {open ? (
                  <ChevronDown className="h-3 w-3 shrink-0" aria-hidden />
                ) : (
                  <ChevronRight className="h-3 w-3 shrink-0" aria-hidden />
                )}
                <span className="min-w-0 flex-1 truncate font-medium">{group.path}</span>
                <span className="shrink-0 text-[10px] tabular-nums text-muted-foreground">
                  {group.matches.length}
                </span>
              </button>

              {open &&
                group.matches.map((match) => (
                  <button
                    key={`${match.line}:${match.column}`}
                    type="button"
                    onClick={() => revealAt(match.path, match.line, match.column)}
                    className="flex w-full items-baseline gap-1.5 py-0.5 pl-6 pr-2 text-left font-mono text-[11px] hover:bg-accent/50"
                  >
                    <span className="shrink-0 tabular-nums text-muted-foreground">{match.line}</span>
                    {/* Trimmed at the front only: leading indentation is never the interesting
                        part of a hit, and trimming both ends would move the highlight. */}
                    <span className="min-w-0 truncate">
                      {match.lineText.slice(0, match.start).trimStart()}
                      <mark className="rounded-sm bg-[hsl(var(--warning)/0.35)] text-foreground">
                        {match.lineText.slice(match.start, match.end)}
                      </mark>
                      {match.lineText.slice(match.end)}
                    </span>
                  </button>
                ))}
            </div>
          );
        })}

        {settled.length > 0 && !result.error && result.matches.length === 0 && (
          <p className="p-2 text-xs text-muted-foreground">{t('ide.search.noMatches')}</p>
        )}
      </div>
    </div>
  );
}

function Toggle({
  active,
  label,
  onClick,
  children,
}: {
  active: boolean;
  label: string;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      aria-label={label}
      aria-pressed={active}
      className={cn(
        'rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground',
        'focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
        active && 'bg-accent text-accent-foreground',
      )}
    >
      {children}
    </button>
  );
}
