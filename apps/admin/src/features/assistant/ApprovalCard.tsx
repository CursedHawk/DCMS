import { ShieldAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, cn } from '@dcms/ui';
import type { PendingApproval } from './useAssistantSession';
import { WorkDetail } from './WorkCard';

/**
 * The stop.
 *
 * <p>It sits on the warning ground rather than the primary one on purpose: primary is the
 * colour of the button you are meant to press, and this is not that. The arguments are shown in
 * the same detail view the transcript uses, because approving "update the article" is not
 * approving what it will contain — and because the operator should be reading one thing here,
 * not learning a second layout.</p>
 */
export function ApprovalCard({
  pending,
  onDecide,
}: {
  pending: PendingApproval;
  onDecide: (ok: boolean, alwaysAllow?: boolean) => void;
}) {
  const { t } = useTranslation();

  return (
    <div
      className={cn(
        'space-y-2 rounded-md border p-3 text-sm',
        pending.dangerous
          ? 'border-destructive/40 bg-destructive/5'
          : 'border-[hsl(var(--warning)/0.4)] bg-[hsl(var(--warning)/0.08)]',
      )}
    >
      <p className="flex items-center gap-1.5 font-medium">
        <ShieldAlert
          className={cn('h-4 w-4 shrink-0', pending.dangerous && 'text-destructive')}
          aria-hidden
        />
        {pending.tool.summarize?.(pending.step.input) ?? pending.tool.name}
      </p>

      <WorkDetail step={pending.step} />

      <div className="flex flex-wrap items-center gap-2">
        <Button
          size="sm"
          variant={pending.dangerous ? 'destructive' : 'default'}
          onClick={() => onDecide(true)}
        >
          {t('assistant.allow')}
        </Button>
        <Button size="sm" variant="outline" onClick={() => onDecide(false)}>
          {t('assistant.decline')}
        </Button>
        {/*
          The pressure valve. Without it the only way to stop being asked the same question
          twelve times in one bulk job is Full auto, which switches off the asking for
          everything — a much bigger yes than the operator meant to give.
        */}
        <button
          type="button"
          onClick={() => onDecide(true, true)}
          className="ml-auto text-xs text-muted-foreground underline-offset-2 hover:underline"
        >
          {t('assistant.alwaysAllow', { tool: pending.tool.name })}
        </button>
      </div>
    </div>
  );
}
