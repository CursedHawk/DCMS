import { useEffect, useMemo, useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { AI_PROMPTS, type AiPromptDefinition } from '@dcms/core';
import { Badge, Button, Input, Label, Textarea, cn, toastApiError } from '@dcms/ui';
import { can, useMe, Perm } from '../../lib/permissions';
import { useAgentPolicy, useSetAgentPolicy } from './api';

/**
 * The tenant's AI agents: how many turns a site agent may take in one run, and the system prompts
 * its agents are given — each shown as shipped, and replaceable for this tenant alone.
 *
 * <p>The shipped prompts come from `@dcms/core`, the same catalogue the admin SPA builds its
 * agents' prompts from, so what is shown here is what an agent gets. Only overrides are stored.</p>
 */
export function AgentPolicySection({ tenantId }: { tenantId: string }) {
  const { t } = useTranslation();
  const me = useMe(true);
  const policy = useAgentPolicy(tenantId);
  const save = useSetAgentPolicy(tenantId);
  const mayChange = can(me.data, Perm.TenantsWrite);

  const [turns, setTurns] = useState('');
  const [prompts, setPrompts] = useState<Record<string, string>>({});
  const [open, setOpen] = useState<string | null>(null);

  // Start from what is stored, and again after every save.
  useEffect(() => {
    if (!policy.data) return;
    setTurns(policy.data.maxTurns?.toString() ?? '');
    setPrompts(policy.data.prompts ?? {});
  }, [policy.data]);

  const stored = policy.data;
  const maxTurns = turns.trim() === '' ? null : Number(turns);
  const turnsValid = maxTurns === null || (Number.isInteger(maxTurns) && maxTurns >= 1 && maxTurns <= (stored?.maxMaxTurns ?? 500));
  const overrides = useMemo(() => normalise(prompts), [prompts]);
  const dirty =
    !!stored && (maxTurns !== stored.maxTurns || JSON.stringify(overrides) !== JSON.stringify(normalise(stored.prompts ?? {})));

  if (policy.isLoading) {
    return (
      <Section>
        <p className="text-sm text-muted-foreground">Loading…</p>
      </Section>
    );
  }
  if (!stored) {
    return (
      <Section>
        <p className="text-sm text-muted-foreground">The AI agent policy could not be loaded.</p>
      </Section>
    );
  }

  const submit = () =>
    save.mutate(
      { maxTurns, prompts: overrides },
      { onSuccess: () => toast.success('AI agent policy saved'), onError: (e) => toastApiError(e, t) },
    );

  return (
    <Section
      action={
        mayChange && (
          <Button size="sm" disabled={!dirty || !turnsValid || save.isPending} onClick={submit}>
            Save
          </Button>
        )
      }
    >
      <div className="mb-5 max-w-xs space-y-1.5">
        <Label htmlFor="agent-max-turns">Turn limit per site agent run</Label>
        <Input
          id="agent-max-turns"
          type="number"
          inputMode="numeric"
          min={1}
          max={stored.maxMaxTurns}
          placeholder={`${stored.defaultMaxTurns} (default)`}
          value={turns}
          disabled={!mayChange}
          aria-invalid={!turnsValid}
          onChange={(e) => setTurns(e.target.value)}
        />
        <p className={cn('text-xs', turnsValid ? 'text-muted-foreground' : 'text-destructive')}>
          1–{stored.maxMaxTurns}. Empty is the default, {stored.defaultMaxTurns}. A run that reaches it pauses and can be
          continued.
        </p>
      </div>

      <h3 className="mb-2 text-sm font-medium">System prompts</h3>
      <ul className="divide-y rounded-md border">
        {AI_PROMPTS.map((prompt) => (
          <PromptRow
            key={prompt.key}
            prompt={prompt}
            value={prompts[prompt.key]}
            expanded={open === prompt.key}
            readOnly={!mayChange}
            onToggle={() => setOpen((was) => (was === prompt.key ? null : prompt.key))}
            onChange={(text) => setPrompts((all) => ({ ...all, [prompt.key]: text }))}
            onReset={() =>
              setPrompts((all) => {
                const { [prompt.key]: _, ...rest } = all;
                return rest;
              })
            }
          />
        ))}
      </ul>
    </Section>
  );
}

function PromptRow({
  prompt,
  value,
  expanded,
  readOnly,
  onToggle,
  onChange,
  onReset,
}: {
  prompt: AiPromptDefinition;
  value: string | undefined;
  expanded: boolean;
  readOnly: boolean;
  onToggle: () => void;
  onChange: (text: string) => void;
  onReset: () => void;
}) {
  const customised = isOverride(prompt, value);
  return (
    <li>
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={expanded}
        className="flex w-full items-start gap-2 px-3 py-2 text-left text-sm hover:bg-accent/50"
      >
        <ChevronRight
          className={cn('mt-0.5 h-4 w-4 shrink-0 text-muted-foreground transition-transform', expanded && 'rotate-90')}
          aria-hidden
        />
        <span className="min-w-0 flex-1">
          <span className="flex flex-wrap items-center gap-2">
            {prompt.title}
            {customised ? <Badge tone="warning">Customised</Badge> : null}
          </span>
          <span className="block text-xs text-muted-foreground">{prompt.usedFor}</span>
        </span>
        <span className="font-mono text-xs text-muted-foreground">{prompt.key}</span>
      </button>
      {expanded ? (
        <div className="space-y-2 border-t px-3 py-3">
          <Textarea
            aria-label={`${prompt.title} prompt`}
            className="min-h-64 font-mono text-xs"
            value={value ?? prompt.text}
            readOnly={readOnly}
            onChange={(e) => onChange(e.target.value)}
          />
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="text-xs text-muted-foreground">
              {prompt.placeholders.length > 0
                ? `Filled in when the prompt is built: ${prompt.placeholders.map((p) => `{{${p}}}`).join(', ')}.`
                : 'Used as written.'}
            </p>
            {!readOnly && customised ? (
              <Button variant="ghost" size="sm" onClick={onReset}>
                Restore shipped prompt
              </Button>
            ) : null}
          </div>
        </div>
      ) : null}
    </li>
  );
}

function Section({ action, children }: { action?: React.ReactNode; children: React.ReactNode }) {
  return (
    <section className="mb-8">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-base font-semibold">AI agents</h2>
        {action}
      </div>
      {children}
    </section>
  );
}

/** A text that differs from the shipped one, and is not blank. */
function isOverride(prompt: AiPromptDefinition, value: string | undefined): value is string {
  return value !== undefined && value.trim() !== '' && value !== prompt.text;
}

/** Only real overrides, in catalogue order, so "changed" compares like with like. */
function normalise(prompts: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const prompt of AI_PROMPTS) {
    const value = prompts[prompt.key];
    if (isOverride(prompt, value)) out[prompt.key] = value;
  }
  return out;
}
