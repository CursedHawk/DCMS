import { useEffect, useRef, useState } from 'react';
import type * as Monaco from 'monaco-editor';
import { Label } from '@dcms/ui';
import { usePluginHost } from '@dcms/plugin-ui';
import { useCheckExpression, type ExpressionProblem } from '../../api';
import { EXPRESSION_LANGUAGE, registerExpressionLanguage, setExpressionContext, type ExpressionContext } from './expressionLanguage';

const register = (monaco: unknown) => registerExpressionLanguage(monaco as typeof Monaco);

/**
 * One flow expression — a flow's or a step's condition — with completion from the draft being
 * edited and the server's own parser underlining what a save would refuse.
 */
export function ExpressionEditor({ uri, slug, label, value, onChange, context, hint }: {
  /** Stable for the expression being edited, e.g. file:///dcms/dynamic-apps/{slug}/flows/{id}/condition. */
  uri: string;
  slug: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  /** Null until the language has loaded: the editor works, it just offers nothing yet. */
  context: ExpressionContext | null;
  hint?: string;
}) {
  const { CodeEditor } = usePluginHost().components;
  const check = useCheckExpression(slug);
  const checkRef = useRef(check);
  checkRef.current = check;
  const [problems, setProblems] = useState<ExpressionProblem[]>([]);

  useEffect(() => {
    setExpressionContext(uri, context);
  }, [uri, context]);
  useEffect(() => () => setExpressionContext(uri, null), [uri]);

  useEffect(() => {
    if (!value.trim()) {
      setProblems([]);
      return undefined;
    }
    let live = true;
    // Asked on a pause in typing, not per keystroke.
    const timer = setTimeout(() => {
      checkRef.current(value).then((p) => live && setProblems(p), () => {});
    }, 300);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [value]);

  return (
    <div className="space-y-1">
      <Label>{label}</Label>
      <CodeEditor uri={uri} language={EXPRESSION_LANGUAGE} value={value} onChange={onChange} compact height={56}
        ariaLabel={label} markers={problems} onMonaco={register} />
      {problems.length > 0 ? (
        <p className="text-xs text-destructive" data-testid="expression-problem">{problems[0]!.message}</p>
      ) : hint ? (
        <p className="text-xs text-muted-foreground">{hint}</p>
      ) : null}
    </div>
  );
}
