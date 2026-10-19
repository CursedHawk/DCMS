import { cn } from '../cn';

type Tone = 'default' | 'secondary' | 'success' | 'warning' | 'destructive' | 'outline';

const tones: Record<Tone, string> = {
  default: 'bg-primary/10 text-primary border-transparent',
  secondary: 'bg-secondary text-secondary-foreground border-transparent',
  success: 'bg-success/12 text-success-foreground border-transparent',
  warning: 'bg-warning/15 text-warning-foreground border-transparent',
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
