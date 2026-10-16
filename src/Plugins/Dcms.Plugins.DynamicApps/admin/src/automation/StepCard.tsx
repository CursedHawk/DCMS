import { useMemo } from 'react';
import { ArrowDown, ArrowUp, Trash2 } from 'lucide-react';
import { Button, Label } from '@dcms/ui';
import { usePluginHost, usePluginT } from '@dcms/plugin-ui';
import type { FlowActionInfo } from '../api';
import { SelectField, TextField } from '../model/dialogs';
import { ExpressionEditor } from './editor/ExpressionEditor';
import type { ExpressionContext } from './editor/expressionLanguage';
import { inputSchemaFor, parseInput, scriptLanguage, withAction, type StepDraft } from './steps';

/**
 * One step of a flow: its id, action, condition and input. A script step's code gets a
 * TypeScript editor typed from the app (`typings`, for the script being worked on); every other
 * action's input is JSON checked against that action's schema.
 */
export function StepCard({ draft, index, count, slug, documentBase, actions, context, typings, onChange, onMove, onRemove, onFocusScript }: {
  draft: StepDraft;
  index: number;
  count: number;
  slug: string;
  documentBase: string;
  actions: FlowActionInfo[];
  /** What the step's condition can see: the steps before it. */
  context: ExpressionContext | null;
  /** The flow-context declarations, when this is the script being worked on. */
  typings: string | null;
  onChange: (draft: StepDraft) => void;
  onMove: (by: -1 | 1) => void;
  onRemove: () => void;
  onFocusScript: () => void;
}) {
  const { t } = usePluginT();
  const { CodeEditor } = usePluginHost().components;
  const script = draft.code !== null;
  const action = actions.find((a) => a.key === draft.action);
  const options = actions.map((a) => ({ value: a.key, label: a.key }));
  // An action no provider offers any more still shows, so the step can be seen and changed.
  if (draft.action && !action) options.push({ value: draft.action, label: draft.action });
  const language = scriptLanguage(draft);
  const base = `${documentBase}/steps/${draft.uid}`;
  const inputInvalid = parseInput(draft.inputText) === null;
  // Stable identities: the editor re-registers a schema or a declaration file whenever these change.
  const schema = useMemo(() => inputSchemaFor(action, script), [action, script]);
  const libs = useMemo(() => (typings ? [{ path: 'file:///dcms/dynamic-apps/flow-context.d.ts', content: typings }] : undefined), [typings]);

  return (
    <section className="space-y-3 rounded-md border p-3" aria-label={t('automation.stepN', { n: index + 1 })}>
      <div className="flex items-center gap-1">
        <h4 className="mr-auto text-sm font-semibold">{t('automation.stepN', { n: index + 1 })}</h4>
        <Button type="button" variant="ghost" size="icon" aria-label={t('automation.moveUp')} disabled={index === 0} onClick={() => onMove(-1)}>
          <ArrowUp className="size-4" />
        </Button>
        <Button type="button" variant="ghost" size="icon" aria-label={t('automation.moveDown')} disabled={index === count - 1} onClick={() => onMove(1)}>
          <ArrowDown className="size-4" />
        </Button>
        <Button type="button" variant="ghost" size="icon" aria-label={t('automation.removeStep')} disabled={count === 1} onClick={onRemove}>
          <Trash2 className="size-4" />
        </Button>
      </div>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <TextField id={`${draft.uid}-id`} label={t('automation.stepId')} value={draft.id} mono
          onChange={(id) => onChange({ ...draft, id })} />
        <SelectField id={`${draft.uid}-action`} label={t('automation.action')} value={draft.action} options={options}
          onChange={(key) => onChange(withAction(draft, key))} />
      </div>
      {action ? <p className="text-xs text-muted-foreground">{action.description}</p> : null}
      <ExpressionEditor uri={`${base}/condition.dexpr`} slug={slug} label={t('automation.stepCondition')}
        value={draft.condition} onChange={(condition) => onChange({ ...draft, condition })} context={context}
        hint={t('automation.stepConditionHint')} />
      {script ? (
        <div className="space-y-1" onFocus={onFocusScript}>
          <Label>{t('automation.scriptCode')}</Label>
          <CodeEditor uri={`${base}/main.${language}`} language={language === 'js' ? 'javascript' : 'typescript'}
            value={draft.code ?? ''} onChange={(code) => onChange({ ...draft, code })} height={220} ariaLabel={t('automation.scriptCode')}
            extraLibs={libs} />
          <p className="text-xs text-muted-foreground">{t('automation.scriptHint')}</p>
        </div>
      ) : null}
      <div className="space-y-1">
        <Label>{t(script ? 'automation.scriptArgs' : 'automation.stepInput')}</Label>
        <CodeEditor uri={`${base}/input.json`} language="json" value={draft.inputText} height={script ? 96 : 140}
          onChange={(inputText) => onChange({ ...draft, inputText })} jsonSchema={schema}
          ariaLabel={t(script ? 'automation.scriptArgs' : 'automation.stepInput')} />
        <p className={`text-xs ${inputInvalid ? 'text-destructive' : 'text-muted-foreground'}`}>
          {inputInvalid ? t('automation.invalidJson') : t(script ? 'automation.scriptArgsHint' : 'automation.stepInputHint')}
        </p>
      </div>
    </section>
  );
}
