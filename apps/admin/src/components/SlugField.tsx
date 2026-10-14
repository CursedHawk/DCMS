import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Input, Label } from '@dcms/ui';

/**
 * A slug input that can say "this one is taken" in place: the form stays filled, the field is
 * focused, and the message says why and what to do, instead of a toast that is gone in seconds.
 * `taken` is the refused value; the message clears itself as soon as the slug is edited.
 */
export function SlugField({
  id,
  label,
  value,
  onChange,
  taken,
  disabled,
  placeholder,
}: {
  id: string;
  label?: string;
  value: string;
  onChange: (v: string) => void;
  taken?: string | null;
  disabled?: boolean;
  placeholder?: string;
}) {
  const { t } = useTranslation();
  const input = useRef<HTMLInputElement>(null);
  const refused = !!taken && taken === value.trim();

  useEffect(() => {
    if (refused) input.current?.focus();
  }, [refused]);

  return (
    <div className="space-y-1.5">
      <Label htmlFor={id}>{label ?? t('common.slug')}</Label>
      <Input
        ref={input}
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        placeholder={placeholder}
        aria-invalid={refused || undefined}
        aria-describedby={refused ? `${id}-taken` : undefined}
      />
      {refused ? (
        <p id={`${id}-taken`} role="alert" className="text-xs text-destructive [overflow-wrap:anywhere]">
          {t('common.slugTaken', { slug: value.trim() })}
        </p>
      ) : null}
    </div>
  );
}
