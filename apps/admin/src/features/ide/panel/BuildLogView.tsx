import { CircleCheck, CircleX, EyeOff, Loader2 } from 'lucide-react';
import { useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { buildHistory, subscribeBuilds, type BuildSnapshot } from '../preview/buildBroker';
import { countBySeverity } from '../preview/problems';
import { PanelUnchecked } from './chrome';
import { clockTime } from './clockTime';

/**
 * Every build this session, newest first.
 *
 * <p>The broker kept only the latest result, which answers "is it building now" and nothing
 * else. The question an author actually has after a failure is "did this just break, or has it
 * been broken since I touched that file?" — and one data point cannot answer it.</p>
 */
export function BuildLogView({
  building,
  previewEnabled,
  onEnablePreview,
}: {
  building: boolean;
  previewEnabled: boolean;
  onEnablePreview: () => void;
}) {
  const { t } = useTranslation();
  const history = useSyncExternalStore(subscribeBuilds, buildHistory, buildHistory);

  // The build runs inside the preview pane, so with it closed there is no build history for a
  // reason that has nothing to do with the code. An empty list here would read as "nothing has
  // been built", which is true but hides the part the author can act on.
  if (!previewEnabled) {
    return (
      <PanelUnchecked
        icon={EyeOff}
        title={t('ide.panel.build.off')}
        description={t('ide.panel.build.offHint')}
        action={t('ide.showPreview')}
        onAction={onEnablePreview}
      />
    );
  }

  if (history.length === 0) {
    return (
      <PanelUnchecked
        icon={building ? Loader2 : undefined}
        title={building ? t('ide.panel.build.running') : t('ide.panel.build.empty')}
      />
    );
  }

  return (
    <ol className="h-full overflow-auto p-1.5">
      {[...history].reverse().map((build) => (
        <Row key={`${build.at}-${build.revision}`} build={build} />
      ))}
    </ol>
  );
}

function Row({ build }: { build: BuildSnapshot }) {
  const { t } = useTranslation();
  const { errors, warnings } = countBySeverity(build.problems);

  return (
    <li className="flex items-baseline gap-2 rounded px-2 py-1 text-xs">
      {build.ok ? (
        <CircleCheck
          className="h-3.5 w-3.5 shrink-0 translate-y-0.5 text-[hsl(var(--success))]"
          aria-hidden
        />
      ) : (
        <CircleX className="h-3.5 w-3.5 shrink-0 translate-y-0.5 text-destructive" aria-hidden />
      )}
      <span className={cn('font-medium', build.ok ? 'text-foreground' : 'text-destructive')}>
        {build.ok ? t('ide.panel.build.succeeded') : t('ide.panel.build.failed')}
      </span>
      <span className="min-w-0 flex-1 truncate text-muted-foreground">
        {errors > 0 ? t('ide.problems.errors', { count: errors }) : null}
        {errors > 0 && warnings > 0 ? ', ' : null}
        {warnings > 0 ? t('ide.problems.warnings', { count: warnings }) : null}
      </span>
      <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground/70">
        {clockTime(build.at)}
      </span>
    </li>
  );
}
