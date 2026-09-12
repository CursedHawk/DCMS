import { Command } from 'cmdk';
import { FileCode2, Terminal } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Dialog, DialogContent, DialogTitle, cn } from '@dcms/ui';
import { highlightRuns, rankCommands, rankPaths, type IdeCommand, type Ranked } from './commands';

export type PaletteMode = 'files' | 'commands';

/**
 * The IDE's own palette: go to a file, or run a command.
 *
 * <p>Separate from the app-wide `⌘K` palette on purpose. That one navigates the console and
 * knows nothing about a project; this one is scoped to the site being edited and is gone the
 * moment you leave it. Sharing one palette would mean either the console's palette carrying
 * a file list it cannot have, or the IDE's carrying every admin route, and neither is what
 * somebody with a keyboard in an editor is reaching for.</p>
 *
 * <p><b>One dialog, two modes</b>, switched by a leading <code>&gt;</code> exactly as VS Code
 * does — so `⌘P` then typing `>` gets to commands without a second shortcut, and `⌘⇧P` simply
 * opens with the prefix already there. Two dialogs would be two focus traps and two places to
 * fix a bug.</p>
 *
 * <p>Filtering is ours (`commands.ts`), not cmdk's: a path needs name-before-folder ranking
 * that a generic string scorer does not do. cmdk is kept for what it is genuinely good at —
 * the listbox semantics, arrow-key movement and the aria wiring that a hand-rolled menu gets
 * subtly wrong.</p>
 */
export function IdeCommandPalette({
  open,
  mode,
  onOpenChange,
  files,
  commands,
  onOpenFile,
}: {
  open: boolean;
  /** Which mode to open in. Typing `>` switches to commands regardless. */
  mode: PaletteMode;
  onOpenChange: (open: boolean) => void;
  files: readonly string[];
  commands: readonly IdeCommand[];
  onOpenFile: (path: string) => void;
}) {
  const { t } = useTranslation();
  const [query, setQuery] = useState('');

  // Reset on every open. A palette that reopens holding the last search is a palette that
  // shows yesterday's answer to a question nobody asked twice.
  useEffect(() => {
    if (open) setQuery(mode === 'commands' ? '>' : '');
  }, [open, mode]);

  const isCommandMode = query.startsWith('>');
  const term = isCommandMode ? query.slice(1) : query;

  const fileHits = useMemo(
    () => (isCommandMode ? [] : rankPaths(files, term)),
    [files, term, isCommandMode],
  );
  const commandHits = useMemo(
    () => (isCommandMode ? rankCommands(commands, term) : []),
    [commands, term, isCommandMode],
  );

  const choose = (run: () => void) => {
    onOpenChange(false);
    run();
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="overflow-hidden p-0" wide>
        {/* Radix requires a title for the dialog's accessible name; the palette shows none. */}
        <DialogTitle className="sr-only">
          {isCommandMode ? t('ide.palette.commandsTitle') : t('ide.palette.filesTitle')}
        </DialogTitle>

        <Command shouldFilter={false} className="[&_[cmdk-input]]:h-12">
          <div className="flex items-center gap-2 border-b px-4">
            {isCommandMode ? (
              <Terminal className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            ) : (
              <FileCode2 className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            )}
            <Command.Input
              value={query}
              onValueChange={setQuery}
              placeholder={t('ide.palette.placeholder')}
              className="w-full bg-transparent py-3 text-sm outline-none placeholder:text-muted-foreground"
            />
          </div>

          <Command.List className="max-h-96 overflow-y-auto p-2">
            <Command.Empty className="py-6 text-center text-sm text-muted-foreground">
              {isCommandMode ? t('ide.palette.noCommands') : t('ide.palette.noFiles')}
            </Command.Empty>

            {fileHits.map((hit) => (
              <FileRow
                key={hit.item}
                hit={hit}
                onSelect={() => choose(() => onOpenFile(hit.item))}
              />
            ))}

            {commandHits.map((hit) => (
              <Command.Item
                key={hit.item.id}
                value={hit.item.id}
                disabled={hit.item.disabled}
                onSelect={() => !hit.item.disabled && choose(hit.item.run)}
                className={cn(
                  'flex cursor-pointer items-center gap-3 rounded-md px-3 py-2 text-sm',
                  'data-[selected=true]:bg-accent data-[selected=true]:text-accent-foreground',
                  hit.item.disabled && 'cursor-not-allowed opacity-50',
                )}
              >
                <span className="min-w-0 flex-1 truncate">
                  <Highlighted text={hit.item.label} positions={hit.positions} />
                </span>
                {hit.item.shortcut ? (
                  <kbd className="shrink-0 rounded border bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                    {hit.item.shortcut}
                  </kbd>
                ) : null}
              </Command.Item>
            ))}
          </Command.List>

          <div className="border-t px-4 py-2 text-[11px] text-muted-foreground">
            {t('ide.palette.hint')}
          </div>
        </Command>
      </DialogContent>
    </Dialog>
  );
}

/** A file row: the name, emphasised, with its folder trailing in the muted colour. */
function FileRow({ hit, onSelect }: { hit: Ranked<string>; onSelect: () => void }) {
  const slash = hit.item.lastIndexOf('/');
  const folder = slash === -1 ? '' : hit.item.slice(0, slash + 1);
  const name = hit.item.slice(slash + 1);
  // The positions index the whole path, so the name's own highlights have to be shifted back.
  const nameHits = hit.positions.filter((p) => p > slash).map((p) => p - slash - 1);
  const folderHits = hit.positions.filter((p) => p <= slash);

  return (
    <Command.Item
      value={hit.item}
      onSelect={onSelect}
      className="flex cursor-pointer items-baseline gap-2 rounded-md px-3 py-2 text-sm data-[selected=true]:bg-accent data-[selected=true]:text-accent-foreground"
    >
      <span className="truncate font-medium">
        <Highlighted text={name} positions={nameHits} />
      </span>
      <span className="min-w-0 flex-1 truncate text-xs text-muted-foreground">
        <Highlighted text={folder} positions={folderHits} />
      </span>
    </Command.Item>
  );
}

function Highlighted({ text, positions }: { text: string; positions: readonly number[] }) {
  return (
    <>
      {highlightRuns(text, positions).map((run, i) =>
        run.hit ? (
          <mark key={i} className="bg-transparent font-semibold text-primary">
            {run.text}
          </mark>
        ) : (
          <span key={i}>{run.text}</span>
        ),
      )}
    </>
  );
}
