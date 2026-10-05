import { Check, GitCommitHorizontal, Lock, PencilLine, RotateCcw } from 'lucide-react';
import { useState, type CSSProperties } from 'react';

/** `--at` is the step's start, in seconds of the sequence clock (see .demo in index.css). */
const at = (s: number) => ({ '--at': s }) as CSSProperties;

const blocks = ['Hero', 'Text', 'Gallery', 'Form'];

export function HeroDemo() {
  // Remounting restarts every CSS animation from zero; nothing else to reset.
  const [run, setRun] = useState(0);

  return (
    <div className="relative">
      <div
        key={run}
        role="img"
        aria-label="Example: four blocks are placed on a page, the change is committed to git, the site is built, a certificate is issued and the page goes live on its own domain."
        className="demo overflow-hidden rounded-2xl border bg-background shadow-[0_30px_80px_-30px_hsl(var(--primary)/0.45)] ring-1 ring-foreground/5"
      >
        {/* Title bar: the address flips from the editor to the live domain. */}
        <div className="flex items-center gap-3 border-b bg-secondary/60 px-4 py-3">
          <span className="flex gap-1.5" aria-hidden="true">
            <i className="size-2.5 rounded-full bg-foreground/15" />
            <i className="size-2.5 rounded-full bg-foreground/15" />
            <i className="size-2.5 rounded-full bg-foreground/15" />
          </span>
          <div className="relative h-7 min-w-0 flex-1 rounded-md bg-background font-mono text-[0.72rem] leading-7 text-muted-foreground">
            <span
              className="out absolute inset-0 flex items-center gap-1.5 truncate px-2.5"
              style={at(7.2)}
            >
              <PencilLine className="size-3 shrink-0" aria-hidden="true" />
              admin.highgeek.eu/sites/studio-nord/editor
            </span>
            <span
              className="step absolute inset-0 flex items-center gap-1.5 truncate px-2.5 text-foreground"
              style={at(7.3)}
            >
              <Lock className="size-3 shrink-0 text-success" aria-hidden="true" />
              https://studio-nord.eu
            </span>
          </div>
        </div>

        <div className="flex min-h-[17rem]">
          {/* Block palette */}
          <ul className="hidden w-28 shrink-0 space-y-1.5 border-r bg-secondary/30 p-3 text-xs text-muted-foreground min-[420px]:block">
            {blocks.map((b, i) => (
              <li
                key={b}
                className="pick flex items-center gap-2 rounded-md border bg-background px-2 py-1.5"
                style={at(0.8 + i * 0.8)}
              >
                <span className="size-2.5 rounded-[3px] bg-primary/60" aria-hidden="true" />
                {b}
              </li>
            ))}
          </ul>

          {/* Canvas */}
          <div className="flex-1 space-y-2.5 p-3 sm:p-4">
            <div
              className="drop rounded-lg bg-sidebar px-4 py-5 text-sidebar-foreground"
              style={at(1)}
            >
              <p className="text-[0.95rem] font-semibold leading-tight tracking-tight">
                Studio Nord
              </p>
              <p className="mt-1 text-[0.7rem] text-sidebar-muted">
                Furniture made to order in Brno
              </p>
              <span className="mt-3 inline-block rounded-full bg-sidebar-accent px-2.5 py-1 text-[0.62rem] font-medium text-white">
                Book a visit
              </span>
            </div>
            <div className="drop space-y-1.5 px-1" style={at(1.8)} aria-hidden="true">
              <i className="block h-1.5 w-11/12 rounded-full bg-foreground/15" />
              <i className="block h-1.5 w-4/5 rounded-full bg-foreground/15" />
              <i className="block h-1.5 w-3/5 rounded-full bg-foreground/15" />
            </div>
            <div className="drop grid grid-cols-3 gap-1.5" style={at(2.6)} aria-hidden="true">
              <i className="aspect-[4/3] rounded-md bg-gradient-to-br from-primary/35 to-primary/10" />
              <i className="aspect-[4/3] rounded-md bg-gradient-to-br from-success/35 to-success/10" />
              <i className="aspect-[4/3] rounded-md bg-gradient-to-br from-warning/40 to-warning/10" />
            </div>
            <div className="drop flex gap-1.5" style={at(3.4)} aria-hidden="true">
              <i className="h-6 flex-1 rounded-md border bg-secondary/50" />
              <i className="h-6 w-14 rounded-md bg-primary" />
            </div>
          </div>
        </div>

        {/* Publish log */}
        <ol className="space-y-1.5 border-t bg-secondary/40 px-4 py-3 font-mono text-[0.68rem] leading-5 text-muted-foreground sm:text-[0.72rem]">
          <li className="step flex items-center gap-2" style={at(4.2)}>
            <GitCommitHorizontal className="size-3.5 shrink-0 text-primary" aria-hidden="true" />
            <span className="truncate">
              <b className="font-semibold text-foreground">a41f9c2</b> Homepage: hero, gallery,
              booking form
            </span>
          </li>
          <li className="step flex items-center gap-2" style={at(4.8)}>
            <span
              className="relative h-1.5 w-20 shrink-0 overflow-hidden rounded-full bg-foreground/10"
              aria-hidden="true"
            >
              <span className="bar absolute inset-0 rounded-full bg-primary" style={at(4.9)} />
            </span>
            <span className="truncate">build release, 14 s</span>
          </li>
          <li className="step flex items-center gap-2" style={at(6.6)}>
            <Check className="size-3.5 shrink-0 text-success" aria-hidden="true" />
            <span className="truncate">certificate issued for studio-nord.eu</span>
          </li>
          <li className="step flex items-center gap-2 text-foreground" style={at(7.6)}>
            <span
              className="pulse ml-1 mr-0.5 size-2 shrink-0 rounded-full bg-success"
              style={at(7.6)}
              aria-hidden="true"
            />
            <span className="truncate">live</span>
          </li>
        </ol>
      </div>

      <button
        type="button"
        onClick={() => setRun((n) => n + 1)}
        className="mt-3 ml-auto flex items-center gap-1.5 rounded-full px-3 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
      >
        <RotateCcw className="size-3.5" aria-hidden="true" />
        Replay
      </button>
    </div>
  );
}
