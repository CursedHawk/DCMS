import { Check, Copy } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, type ButtonProps } from './ui/button';

export function CopyButton({
  value,
  label,
  ...props
}: { value: string; label?: string } & Omit<ButtonProps, 'onClick' | 'children'>) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);

  return (
    <Button
      variant="outline"
      size="sm"
      onClick={async () => {
        await navigator.clipboard.writeText(value);
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      }}
      {...props}
    >
      {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
      {label ?? (copied ? t('actions.copied', { defaultValue: 'Copied' }) : t('actions.copy', { defaultValue: 'Copy' }))}
    </Button>
  );
}
