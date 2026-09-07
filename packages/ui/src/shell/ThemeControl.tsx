import { Monitor, Moon, Sun } from 'lucide-react';
import { Button } from '../ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '../ui/dropdown-menu';
import { Tooltip, TooltipContent, TooltipTrigger } from '../ui/tooltip';
import { useTheme } from '../theme';

export interface ThemeLabels {
  toggle: string;
  light: string;
  dark: string;
  system: string;
}

const DEFAULTS: ThemeLabels = {
  toggle: 'Theme',
  light: 'Light',
  dark: 'Dark',
  system: 'System',
};

/**
 * Light / dark / follow-the-OS.
 *
 * <p>Two presentations of one control. `menu` offers all three and is right where someone
 * configures their workspace once; `toggle` flips between light and dark in a click and is
 * right in a console people open, read and close.</p>
 *
 * <p>The tooltip and the dropdown share a single trigger, both as `asChild` Radix Slots onto
 * the same Button, so the pointer handlers from each compose. Wrapping the Button in a plain
 * hint element here instead swallows the dropdown's handlers and the menu never opens — which
 * is exactly how the theme switch broke once already.</p>
 */
export function ThemeControl({
  variant = 'menu',
  labels: partial,
}: {
  variant?: 'menu' | 'toggle';
  labels?: Partial<ThemeLabels>;
}) {
  const { theme, setTheme, resolved } = useTheme();
  const labels = { ...DEFAULTS, ...partial };
  const Icon = resolved === 'dark' ? Moon : Sun;

  if (variant === 'toggle') {
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            variant="ghost"
            size="icon"
            aria-label={resolved === 'dark' ? labels.light : labels.dark}
            onClick={() => setTheme(resolved === 'dark' ? 'light' : 'dark')}
          >
            <Icon className="h-4 w-4" aria-hidden />
          </Button>
        </TooltipTrigger>
        <TooltipContent>{resolved === 'dark' ? labels.light : labels.dark}</TooltipContent>
      </Tooltip>
    );
  }

  const options = [
    { key: 'light', icon: Sun, label: labels.light },
    { key: 'dark', icon: Moon, label: labels.dark },
    { key: 'system', icon: Monitor, label: labels.system },
  ] as const;

  return (
    <DropdownMenu>
      <Tooltip>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={labels.toggle}>
              <Icon className="h-4 w-4" aria-hidden />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent>{labels.toggle}</TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end">
        {options.map((o) => (
          <DropdownMenuItem
            key={o.key}
            onClick={() => setTheme(o.key)}
            className={theme === o.key ? 'text-primary' : ''}
          >
            <o.icon className="h-4 w-4" aria-hidden /> {o.label}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
