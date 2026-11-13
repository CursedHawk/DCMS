import { MessageCircleQuestion } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, cn } from '@dcms/ui';
import type { AgentAnswers, AgentQuestion } from './ask';

/** The "write my own" choice, kept apart from any option the model could name. */
const OTHER = '\u0000other';

/**
 * The agent's `ask_user` questions, answered by picking options or writing your own.
 *
 * <p>One card for the whole set: answering three related questions together is one decision,
 * and the run waits for all of them anyway. Used by site agents (IDE panel, dock's Agents tab)
 * and the console assistant alike.</p>
 */
export function QuestionCard({
  questions,
  onAnswer,
}: {
  questions: readonly AgentQuestion[];
  /** Called once: with the answers, or `null` when the person skips. */
  onAnswer: (answers: AgentAnswers) => void;
}) {
  const { t } = useTranslation();
  const [chosen, setChosen] = useState<string[][]>(() => questions.map(() => []));
  const [other, setOther] = useState<string[]>(() => questions.map(() => ''));
  const cardRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    // Announced when it appears, like the approval card: the run is waiting on it.
    cardRef.current?.focus();
  }, []);

  const pick = (index: number, value: string, multi: boolean) =>
    setChosen((all) =>
      all.map((current, i) => {
        if (i !== index) return current;
        if (!multi) return [value];
        return current.includes(value) ? current.filter((v) => v !== value) : [...current, value];
      }),
    );

  const answered = (index: number) =>
    chosen[index].some((v) => v !== OTHER) || (chosen[index].includes(OTHER) && other[index].trim() !== '');
  const complete = questions.every((_, index) => answered(index));

  const submit = () =>
    onAnswer(
      questions.map((_, index) => ({
        selected: chosen[index].filter((v) => v !== OTHER),
        ...(chosen[index].includes(OTHER) && other[index].trim() ? { other: other[index].trim() } : {}),
      })),
    );

  return (
    <div
      ref={cardRef}
      role="dialog"
      aria-label={t('assistant.ask.title', { count: questions.length })}
      tabIndex={-1}
      className="m-3 space-y-3 rounded-md border border-primary/50 bg-card p-3 text-sm outline-none"
    >
      <p className="flex items-center gap-1.5 font-medium">
        <MessageCircleQuestion className="h-4 w-4 text-primary" aria-hidden />
        {t('assistant.ask.title', { count: questions.length })}
      </p>

      {questions.map((q, index) => {
        const multi = q.multiSelect === true;
        const name = `ask-${index}`;
        return (
          <fieldset key={index} className="space-y-1.5">
            <legend className="mb-1 flex flex-wrap items-center gap-1.5">
              {q.header ? <Badge tone="secondary">{q.header}</Badge> : null}
              <span className="font-medium">{q.question}</span>
              {multi ? <span className="text-2xs text-muted-foreground">{t('assistant.ask.multi')}</span> : null}
            </legend>
            {q.options.map((option) => (
              <label
                key={option.label}
                className={cn(
                  'flex cursor-pointer items-start gap-2 rounded-md border px-2.5 py-1.5 hover:bg-accent/50',
                  chosen[index].includes(option.label) && 'border-primary bg-primary/5',
                )}
              >
                <input
                  type={multi ? 'checkbox' : 'radio'}
                  name={name}
                  className="mt-0.5 accent-primary"
                  checked={chosen[index].includes(option.label)}
                  onChange={() => pick(index, option.label, multi)}
                />
                <span className="min-w-0">
                  <span className="block">{option.label}</span>
                  {option.description ? (
                    <span className="block text-xs text-muted-foreground">{option.description}</span>
                  ) : null}
                </span>
              </label>
            ))}
            <label
              className={cn(
                'flex items-center gap-2 rounded-md border px-2.5 py-1.5',
                chosen[index].includes(OTHER) && 'border-primary bg-primary/5',
              )}
            >
              <input
                type={multi ? 'checkbox' : 'radio'}
                name={name}
                aria-label={t('assistant.ask.other')}
                className="accent-primary"
                checked={chosen[index].includes(OTHER)}
                onChange={() => pick(index, OTHER, multi)}
              />
              <input
                type="text"
                value={other[index]}
                placeholder={t('assistant.ask.otherPlaceholder')}
                aria-label={`${q.question} — ${t('assistant.ask.other')}`}
                className="min-w-0 flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
                onChange={(event) => {
                  const text = event.target.value;
                  setOther((all) => all.map((v, i) => (i === index ? text : v)));
                  // Typing is choosing it.
                  if (!chosen[index].includes(OTHER)) pick(index, OTHER, multi);
                }}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' && complete) submit();
                }}
              />
            </label>
          </fieldset>
        );
      })}

      <div className="flex justify-end gap-2">
        <Button size="sm" variant="ghost" onClick={() => onAnswer(null)}>
          {t('assistant.ask.skip')}
        </Button>
        <Button size="sm" disabled={!complete} onClick={submit}>
          {t('assistant.ask.send')}
        </Button>
      </div>
    </div>
  );
}
