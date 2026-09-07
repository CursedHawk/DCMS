import { Check, Eye, PencilLine, TriangleAlert } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, cn } from '@dcms/ui';
import { BufferEditor } from './BufferEditor';
import {
  buildConflictText,
  hasUnresolvedMarkers,
  resolvedCount,
  type ConflictFile,
  type Resolution,
  type ResolutionKind,
} from './conflict';
import { useVfs } from './vfs';

/**
 * One resolution flow, used by every path that can conflict.
 *
 * <p>There were three before, and all three offered the same two choices: keep the whole file
 * from one side or the whole file from the other. That is only ever right by luck — two people
 * editing different parts of the same file have both done work worth keeping, and picking a
 * side discards one of them without saying so.</p>
 *
 * <p>The third option is the one that was missing: the file with git's own conflict markers in
 * it, in Monaco, editable. The "complete" button stays disabled while any marker remains,
 * because committing them produces source that does not parse and a build that fails minutes
 * later, by which point the merge is already on the branch.</p>
 */
export function ConflictResolver({
  files,
  resolutions,
  onChange,
  mineLabel,
  theirsLabel,
}: {
  files: readonly ConflictFile[];
  resolutions: Readonly<Record<string, Resolution>>;
  onChange: (next: Record<string, Resolution>) => void;
  /** Names the sides in the reader's terms — a branch name, or "your edits". */
  mineLabel: string;
  theirsLabel: string;
}) {
  const { t } = useTranslation();
  const openDiff = useVfs((s) => s.openDiff);
  const [expanded, setExpanded] = useState<string | null>(null);

  const set = (path: string, resolution: Resolution) =>
    onChange({ ...resolutions, [path]: resolution });

  const choose = (file: ConflictFile, kind: ResolutionKind) => {
    if (kind === 'manual') {
      const existing = resolutions[file.path];
      set(file.path, {
        kind: 'manual',
        content:
          existing?.kind === 'manual'
            ? existing.content
            : buildConflictText(file, mineLabel, theirsLabel),
      });
      setExpanded(file.path);
      return;
    }
    set(file.path, { kind });
    if (expanded === file.path) setExpanded(null);
  };

  const done = resolvedCount(files, resolutions);

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-sm">
        <TriangleAlert className="h-4 w-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
        <span className="text-muted-foreground">
          {t('ide.git.conflictIntro', { count: files.length })}
        </span>
        <Badge tone={done === files.length ? 'success' : 'warning'} className="ml-auto shrink-0">
          {t('ide.git.resolvedCount', { done, total: files.length })}
        </Badge>
      </div>

      <ul className="space-y-2">
        {files.map((file) => {
          const resolution = resolutions[file.path];
          const kind = resolution?.kind;
          const markersLeft =
            resolution?.kind === 'manual' && hasUnresolvedMarkers(resolution.content);
          const settled = !!resolution && !markersLeft;

          return (
            <li key={file.path} className="rounded border">
              <div className="flex items-center gap-1.5 border-b px-2 py-1.5">
                {settled ? (
                  <Check className="h-3.5 w-3.5 shrink-0 text-[hsl(var(--success))]" aria-hidden />
                ) : (
                  <span
                    aria-hidden
                    className="h-1.5 w-1.5 shrink-0 rounded-full bg-[hsl(var(--warning))]"
                  />
                )}
                <span className="min-w-0 flex-1 truncate font-mono text-xs" title={file.path}>
                  {file.path}
                </span>
                <button
                  type="button"
                  onClick={() =>
                    openDiff({
                      path: file.path,
                      status: 'modified',
                      original: file.theirs ?? '',
                      modified: file.mine ?? '',
                    })
                  }
                  aria-label={t('ide.git.viewDiff')}
                  title={t('ide.git.viewDiff')}
                  className="shrink-0 rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
                >
                  <Eye className="h-3.5 w-3.5" aria-hidden />
                </button>
              </div>

              <div className="flex flex-wrap gap-1.5 p-2">
                <SideButton active={kind === 'mine'} onClick={() => choose(file, 'mine')}>
                  {t('ide.git.useBranch', { branch: mineLabel })}
                </SideButton>
                <SideButton active={kind === 'theirs'} onClick={() => choose(file, 'theirs')}>
                  {t('ide.git.useBranch', { branch: theirsLabel })}
                </SideButton>
                <SideButton active={kind === 'manual'} onClick={() => choose(file, 'manual')}>
                  <PencilLine className="h-3 w-3" aria-hidden />
                  {t('ide.git.mergeByHand')}
                </SideButton>
              </div>

              {expanded === file.path && resolution?.kind === 'manual' ? (
                <div className="border-t">
                  {markersLeft ? (
                    <p className="border-b bg-[hsl(var(--warning)/0.1)] px-2 py-1 text-xs">
                      {t('ide.git.markersRemain')}
                    </p>
                  ) : null}
                  <div className="h-64">
                    <BufferEditor
                      path={file.path}
                      value={resolution.content}
                      onChange={(content) => set(file.path, { kind: 'manual', content })}
                    />
                  </div>
                </div>
              ) : null}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function SideButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={cn(
        'inline-flex flex-1 items-center justify-center gap-1 rounded border px-2 py-1 text-xs transition-colors',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        active
          ? 'border-primary bg-primary/10 text-primary'
          : 'text-muted-foreground hover:bg-accent/50',
      )}
    >
      {children}
    </button>
  );
}
