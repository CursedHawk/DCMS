import { APP_JSON, THEME_JSON, appSchema, pageIdFromPath, pageSchema, themeTokensSchema } from '@dcms/site-runtime';
import { FileJson, GitCompare, X } from 'lucide-react';
import type * as Monaco from 'monaco-editor';
import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { cn } from '@dcms/ui';
import { DiffEditor, MonacoEditor, monaco, setGeneratedPathPredicate, setupMonaco, useVfs } from '../site-source';
import { isGeneratedPath } from '../ide/generated/generatedLayer';

/**
 * The documents behind the canvas, as text.
 *
 * Monaco edits the same working draft the canvas writes, so the two are one document: a change
 * here reloads the canvas (VisualCanvas sees the file move), and a canvas edit lands here. The
 * JSON Schemas come from the zod schemas the builder and the published site parse with — the
 * code view cannot accept a document the canvas would then refuse.
 */

let schemasRegistered = false;

function registerSchemas(m: typeof Monaco): void {
  if (schemasRegistered) return;
  schemasRegistered = true;
  const json = (schema: z.ZodType) => z.toJSONSchema(schema, { io: 'input', target: 'draft-7', unrepresentable: 'any' }) as object;
  m.languages.json.jsonDefaults.setDiagnosticsOptions({
    validate: true,
    enableSchemaRequest: false,
    schemas: [
      { uri: 'https://dcms.local/schemas/app.json', fileMatch: [`file:///${APP_JSON}`], schema: json(appSchema) },
      { uri: 'https://dcms.local/schemas/page.json', fileMatch: ['file:///dcms/pages/*.json'], schema: json(pageSchema) },
      { uri: 'https://dcms.local/schemas/theme.json', fileMatch: [`file:///${THEME_JSON}`], schema: json(themeTokensSchema) },
    ],
  });
}

export function VisualCodeView({ activePage }: { activePage: string | null }) {
  const { t } = useTranslation();
  const keys = useVfs((s) => Object.keys(s.files).sort().join('\n'));
  const activePath = useVfs((s) => s.activePath);
  const activeDiff = useVfs((s) => s.activeDiff);
  const openDiffs = useVfs((s) => s.openDiffs);

  const files = useMemo(
    () => keys.split('\n').filter((p) => p === APP_JSON || p === THEME_JSON || pageIdFromPath(p) !== null),
    [keys],
  );

  useEffect(() => {
    setupMonaco();
    registerSchemas(monaco);
    // The runtime, the API client and openapi.json are DCMS's; Monaco shows them read-only.
    setGeneratedPathPredicate(isGeneratedPath);
  }, []);

  // Follow the canvas: opening a page there shows its file here, unless the author chose another.
  useEffect(() => {
    const target = activePage ? `dcms/pages/${activePage}.json` : APP_JSON;
    const { files: all, activePath: current } = useVfs.getState();
    if (all[target] !== undefined && (current === null || pageIdFromPath(current) !== null)) useVfs.getState().open(target);
  }, [activePage]);

  const diff = openDiffs.find((d) => d.path === activeDiff);

  return (
    <div className="flex h-full flex-col">
      <div className="flex shrink-0 items-center gap-1 overflow-x-auto border-b px-2 py-1.5">
        {files.map((path) => (
          <button
            key={path}
            type="button"
            onClick={() => useVfs.getState().open(path)}
            className={cn(
              'flex shrink-0 items-center gap-1.5 rounded-md px-2 py-1 text-xs',
              !activeDiff && activePath === path ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-accent/50',
            )}
          >
            <FileJson className="h-3.5 w-3.5" />
            {path.replace(/^dcms\//, '')}
          </button>
        ))}
        {openDiffs.map((d) => (
          <span
            key={`diff:${d.path}`}
            className={cn(
              'flex shrink-0 items-center gap-1 rounded-md pl-2 pr-1 text-xs',
              activeDiff === d.path ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-accent/50',
            )}
          >
            <button type="button" onClick={() => useVfs.getState().setActiveDiff(d.path)} className="flex items-center gap-1.5 py-1">
              <GitCompare className="h-3.5 w-3.5" />
              {d.path.slice(d.path.lastIndexOf('/') + 1)}
            </button>
            <button
              type="button"
              onClick={() => useVfs.getState().closeDiff(d.path)}
              title={t('actions.close')}
              className="rounded p-1 hover:text-foreground"
            >
              <X className="h-3 w-3" />
            </button>
          </span>
        ))}
      </div>
      <div className="min-h-0 flex-1">
        {diff ? <DiffEditor path={diff.path} original={diff.original} modified={diff.modified} /> : <MonacoEditor />}
      </div>
    </div>
  );
}
