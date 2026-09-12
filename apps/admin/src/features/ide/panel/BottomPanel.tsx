import { ChevronDown } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { Resizer } from '../../site-source';
import { countBySeverity, type BuildProblem } from '../preview/problems';
import { ProblemsView } from '../ProblemsView';
import { BuildLogView } from './BuildLogView';
import { ConsoleView } from './ConsoleView';
import { OutputView } from './OutputView';
import { PANEL_TABS, type PanelTab, type PanelState } from './panelState';

/**
 * The bottom panel: what the code is saying, under the code it is saying it about.
 *
 * <p>Problems lived in the left sidebar, which was the wrong shape for it twice over. A problem
 * row is `path:line  message` — it reads across, and a 260px column made every one of them
 * wrap. And it competed with the file tree for the same space, so you could look at the error
 * or at the file it was in, never both.</p>
 *
 * <p>The four tabs here are the log-shaped surfaces: what the compiler said, what the workspace
 * did, what the running page said, and what every build this session concluded. <b>The agent is
 * deliberately not among them</b> — a conversation is tall and narrow, and this panel is short
 * and wide.</p>
 */
export function BottomPanel({
  state,
  height,
  onHeightDelta,
  onHeightReset,
  problems,
  building,
  previewEnabled,
  onEnablePreview,
}: {
  state: PanelState;
  height: number;
  onHeightDelta: (dy: number) => void;
  onHeightReset: () => void;
  problems: readonly BuildProblem[];
  building: boolean;
  previewEnabled: boolean;
  onEnablePreview: () => void;
}) {
  const { t } = useTranslation();
  const { errors, warnings } = countBySeverity(problems);

  // Collapsed: just the tab strip, so the panel is always one click away and its counts stay
  // readable. A bottom panel you have to remember exists is one nobody opens.
  const tabStrip = (
    <div className="flex h-8 shrink-0 items-stretch gap-0.5 border-t bg-card px-1.5">
      {PANEL_TABS.map((tab) => (
        <TabButton
          key={tab}
          tab={tab}
          active={state.open && state.tab === tab}
          label={t(`ide.panel.tabs.${tab}`)}
          badge={tab === 'problems' ? errors + warnings : 0}
          badgeTone={errors > 0 ? 'error' : 'warn'}
          onClick={() => state.toggle(tab)}
        />
      ))}
      <div className="flex-1" />
      {state.open ? (
        <button
          type="button"
          onClick={state.close}
          aria-label={t('ide.panel.hide')}
          title={t('ide.panel.hide')}
          className="my-1 rounded px-1.5 text-muted-foreground hover:bg-accent hover:text-foreground"
        >
          <ChevronDown className="h-3.5 w-3.5" aria-hidden />
        </button>
      ) : null}
    </div>
  );

  if (!state.open) return tabStrip;

  return (
    <>
      <Resizer
        orientation="horizontal"
        ariaLabel={t('ide.panel.resize')}
        onDelta={(d) => onHeightDelta(-d)}
        onReset={onHeightReset}
      />
      <div className="flex shrink-0 flex-col" style={{ height: `${height}px` }}>
        {tabStrip}
        <div className="min-h-0 flex-1 overflow-hidden bg-background">
          {state.tab === 'problems' && (
            <ProblemsView
              problems={problems}
              building={building}
              previewEnabled={previewEnabled}
              onEnablePreview={onEnablePreview}
            />
          )}
          {state.tab === 'output' && <OutputView />}
          {state.tab === 'console' && <ConsoleView previewEnabled={previewEnabled} />}
          {state.tab === 'build' && (
            <BuildLogView
              building={building}
              previewEnabled={previewEnabled}
              onEnablePreview={onEnablePreview}
            />
          )}
        </div>
      </div>
    </>
  );
}

function TabButton({
  tab,
  active,
  label,
  badge,
  badgeTone,
  onClick,
}: {
  tab: PanelTab;
  active: boolean;
  label: string;
  badge: number;
  badgeTone: 'error' | 'warn';
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-selected={active}
      role="tab"
      data-tab={tab}
      className={cn(
        // The active tab is marked by a rule on its own edge rather than a filled pill: the
        // panel sits directly under the editor, and a block of colour down here pulls the eye
        // away from the code that everything in the panel is about.
        'relative px-2.5 text-[11px] font-medium tracking-wide transition-colors',
        active
          ? 'text-foreground after:absolute after:inset-x-2 after:bottom-0 after:h-px after:bg-primary'
          : 'text-muted-foreground hover:text-foreground',
      )}
    >
      <span className="flex items-center gap-1.5">
        {label}
        {badge > 0 ? (
          <span
            className={cn(
              'rounded-full px-1.5 py-px text-[10px] font-semibold tabular-nums',
              badgeTone === 'error'
                ? 'bg-destructive/15 text-destructive'
                : 'bg-[hsl(var(--warning)/0.18)] text-[hsl(var(--warning))]',
            )}
          >
            {badge}
          </span>
        ) : null}
      </span>
    </button>
  );
}
