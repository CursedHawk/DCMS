import { cn } from '../cn';

type Tone = 'default' | 'secondary' | 'success' | 'warning' | 'destructive' | 'outline';

const tones: Record<Tone, string> = {
  default: 'bg-primary/10 text-primary border-transparent',
  secondary: 'bg-secondary text-secondary-foreground border-transparent',
  success: 'bg-[hsl(var(--success)/0.12)] text-[hsl(var(--success))] border-transparent',
  warning: 'bg-[hsl(var(--warning)/0.15)] text-[hsl(var(--warning))] border-transparent',
  destructive: 'bg-destructive/10 text-destructive border-transparent',
  outline: 'text-foreground border-border',
};

export function Badge({
  className,
  tone = 'default',
  ...props
}: React.HTMLAttributes<HTMLSpanElement> & { tone?: Tone }) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium',
        tones[tone],
        className,
      )}
      {...props}
    />
  );
}
