import { useEffect, useMemo, useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { AI_PROMPTS } from '@dcms/core';
import { Badge, Button, Input, Label, Textarea, cn, toastApiError } from '@dcms/ui';
import { can, useMe, Perm } from '../../lib/permissions';
import { type AgentPolicy, useAgentPolicy, useSetAgentPolicy } from './api';

/** A prompt as this page shows it, whichever side builds it. */
interface PromptDefinition {
  key: string;
  title: string;
  usedFor: string;
  placeholders: readonly string[];
  text: string;
}

/** The groups the prompts are listed under, by key. */
const GROUPS: { title: string; matches: (key: string) => boolean }[] = [
  { title: 'Site agents', matches: (key) => key.startsWith('site.') },
  { title: 'Console assistant', matches: (key) => key === 'assistant' || key.startsWith('assistant.') },
  { title: 'Site chatbot', matches: (key) => key === 'chatbot' || key.startsWith('chatbot.') },
  { title: 'Compaction', matches: (key) => key === 'compaction' },
];

/** A limit as typed: empty is the default. */
interface LimitField {
  id: string;
  label: string;
  hint: string;
  value: number | null;
  default: number;
  min: number;
  max: number;
}

/**
 * The tenant's AI agents: their turn limits, when a long conversation is compacted, and the
 * system prompts its agents and its site chatbot are given — each shown as shipped, and
 * replaceable for this tenant alone.
 *
 * <p>The browser agents' prompts come from `@dcms/core`, the catalogue the admin SPA builds its
 * agents' prompts from; the chatbot's come from admin-api, which builds them. Either way what is
 * shown here is what the model gets. Only overrides are stored.</p>
 */
export function AgentPolicySection({ tenantId }: { tenantId: string }) {
  const { t } = useTranslation();
  const me = useMe(true);
  const policy = useAgentPolicy(tenantId);
  const save = useSetAgentPolicy(tenantId);
  const mayChange = can(me.data, Perm.TenantsWrite);

  const [limits, setLimits] = useState<Record<string, string>>({});
  const [prompts, setPrompts] = useState<Record<string, string>>({});
  const [open, setOpen] = useState<string | null>(null);

  // Start from what is stored, and again after every save.
  useEffect(() => {
    if (!policy.data) return;
    setLimits({
      maxTurns: policy.data.maxTurns?.toString() ?? '',
      assistantMaxTurns: policy.data.assistantMaxTurns?.toString() ?? '',
      compactAtTokens: policy.data.compactAtTokens?.toString() ?? '',
    });
    setPrompts(policy.data.prompts ?? {});
  }, [policy.data]);

  const stored = policy.data;
  const definitions = useMemo<PromptDefinition[]>(
    () => [...AI_PROMPTS, ...(stored?.serverPrompts ?? [])],
    [stored?.serverPrompts],
  );
  const fields = stored ? limitFields(stored) : [];
  const values = Object.fromEntries(fields.map((f) => [f.id, parseLimit(limits[f.id])])) as Record<string, number | null>;
  const valid = fields.every((f) => {
    const value = values[f.id];
    return value === null || (Number.isInteger(value) && value >= f.min && value <= f.max);
  });
  const overrides = useMemo(() => normalise(prompts, definitions), [prompts, definitions]);
  const dirty =
    !!stored &&
    (fields.some((f) => values[f.id] !== f.value) ||
      JSON.stringify(overrides) !== JSON.stringify(normalise(stored.prompts ?? {}, definitions)));

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
      {
        maxTurns: values.maxTurns,
        assistantMaxTurns: values.assistantMaxTurns,
        compactAtTokens: values.compactAtTokens,
        prompts: overrides,
      },
      { onSuccess: () => toast.success('AI agent policy saved'), onError: (e) => toastApiError(e, t) },
    );

  return (
    <Section
      action={
        mayChange && (
          <Button size="sm" disabled={!dirty || !valid || save.isPending} onClick={submit}>
            Save
          </Button>
        )
      }
    >
      <div className="mb-5 grid grid-cols-1 gap-4 sm:grid-cols-3">
        {fields.map((field) => {
          const value = values[field.id];
          const ok = value === null || (Number.isInteger(value) && value >= field.min && value <= field.max);
          return (
            <div key={field.id} className="space-y-1.5">
              <Label htmlFor={`policy-${field.id}`}>{field.label}</Label>
              <Input
                id={`policy-${field.id}`}
                type="number"
                inputMode="numeric"
                min={field.min}
                max={field.max}
                placeholder={`${field.default} (default)`}
                value={limits[field.id] ?? ''}
                disabled={!mayChange}
                aria-invalid={!ok}
                onChange={(e) => setLimits((all) => ({ ...all, [field.id]: e.target.value }))}
              />
              <p className={cn('text-xs', ok ? 'text-muted-foreground' : 'text-destructive')}>
                {field.min}–{field.max}. Empty is the default, {field.default}. {field.hint}
              </p>
            </div>
          );
        })}
      </div>

      <h3 className="mb-2 text-sm font-medium">System prompts</h3>
      <div className="space-y-4">
        {GROUPS.map((group) => {
          const members = definitions.filter((d) => group.matches(d.key));
          if (members.length === 0) return null;
          return (
            <div key={group.title}>
              <h4 className="mb-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground">{group.title}</h4>
              <ul className="divide-y rounded-md border">
                {members.map((prompt) => (
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
            </div>
          );
        })}
      </div>
    </Section>
  );
}

function limitFields(policy: AgentPolicy): LimitField[] {
  return [
    {
      id: 'maxTurns',
      label: 'Turn limit per site agent run',
      hint: 'A run that reaches it pauses and can be continued.',
      value: policy.maxTurns,
      default: policy.defaultMaxTurns,
      min: 1,
      max: policy.maxMaxTurns,
    },
    {
      id: 'assistantMaxTurns',
      label: 'Turn limit per assistant question',
      hint: 'The console assistant answers within it.',
      value: policy.assistantMaxTurns,
      default: policy.defaultAssistantMaxTurns,
      min: 1,
      max: policy.maxAssistantMaxTurns,
    },
    {
      id: 'compactAtTokens',
      label: 'Compact conversations at (tokens)',
      hint: 'A longer conversation is summarized and continued from the summary.',
      value: policy.compactAtTokens,
      default: policy.defaultCompactAtTokens,
      min: policy.minCompactAtTokens,
      max: policy.maxCompactAtTokens,
    },
  ];
}

function parseLimit(text: string | undefined): number | null {
  return !text || text.trim() === '' ? null : Number(text);
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
  prompt: PromptDefinition;
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
function isOverride(prompt: PromptDefinition, value: string | undefined): value is string {
  return value !== undefined && value.trim() !== '' && value !== prompt.text;
}

/** Only real overrides, in catalogue order, so "changed" compares like with like. */
function normalise(prompts: Record<string, string>, definitions: readonly PromptDefinition[]): Record<string, string> {
  const out: Record<string, string> = {};
  for (const prompt of definitions) {
    const value = prompts[prompt.key];
    if (isOverride(prompt, value)) out[prompt.key] = value;
  }
  return out;
}
