import { Bot, Check, Eye, HandMetal, Zap } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  cn,
} from '@dcms/ui';
import { AI_MODES, type AiMode } from './modes';

const ICONS: Record<AiMode, typeof Eye> = {
  read: Eye,
  careful: HandMetal,
  agent: Bot,
  auto: Zap,
};

/**
 * How much the assistant may do without asking.
 *
 * <p>A menu rather than a row of segments: the four modes are not equally reachable — Agent is
 * where nearly everyone stays — and each one needs a sentence to be honest about what it does.
 * The sentence is the control's whole job. "Write access: on" told an operator nothing about
 * whether the thing was about to publish.</p>
 */
export function ModeSwitch({
  mode,
  onChange,
  disabled,
}: {
  mode: AiMode;
  onChange: (mode: AiMode) => void;
  /** True when no role-reachable tool writes: the other three modes would behave identically. */
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  const Icon = ICONS[mode];

  if (disabled) {
    return (
      <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
        <Eye className="h-3.5 w-3.5" aria-hidden />
        {t('assistant.mode.read')}
      </span>
    );
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" className="h-7 gap-1.5 px-2">
          <Icon
            className={cn('h-3.5 w-3.5', mode === 'auto' && 'text-[hsl(var(--warning))]')}
            aria-hidden
          />
          <span className="text-xs font-medium">{t(`assistant.mode.${mode}`)}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-80">
        {AI_MODES.map((option) => {
          const OptionIcon = ICONS[option];
          return (
            <DropdownMenuItem
              key={option}
              onSelect={() => onChange(option)}
              className="items-start gap-2.5 py-2"
            >
              <OptionIcon className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
              <span className="min-w-0 flex-1">
                <span className="block text-sm font-medium">{t(`assistant.mode.${option}`)}</span>
                <span className="block text-xs leading-snug text-muted-foreground">
                  {t(`assistant.mode.${option}Hint`)}
                </span>
              </span>
              {option === mode ? <Check className="mt-0.5 h-4 w-4 shrink-0" aria-hidden /> : null}
            </DropdownMenuItem>
          );
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
