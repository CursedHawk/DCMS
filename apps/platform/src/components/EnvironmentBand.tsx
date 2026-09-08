import { cn } from '@dcms/ui';
import { runtimeConfig } from '../runtime-config';

/**
 * Which platform this console is pointed at.
 *
 * <p>Not decoration, and not a badge tucked into a corner. dev and production run the same
 * image, serve the same UI and sign in against the same-looking identity service, and this is
 * the console that can suspend a tenant, revoke the last SuperAdmin's role and delete a
 * telemetry store. The failure this prevents is an operator doing the right thing to the wrong
 * environment — which no confirmation dialog catches, because they will confirm it.</p>
 *
 * <p>Production is deliberately the loud one, in a red-violet that is not the console's error
 * red: "you are on production" must not read as "something is broken".</p>
 */
export function EnvironmentBand() {
  const name = (runtimeConfig.environmentName || 'development').toLowerCase();
  const isProduction = name === 'production' || name === 'prod';

  return (
    <div
      className={cn(
        'flex items-center gap-2 border-b px-4 py-1.5 text-xs',
        /*
         * The non-production band is `text-foreground`, not `text-muted-foreground`.
         *
         * Muted on the secondary ground measures 4.32:1 — under the 4.5:1 minimum, which axe
         * caught on every page of this console. A band whose whole purpose is to be readable at
         * a glance, and which is not quite readable, is worse than no band: it is a safeguard
         * that looks present. Quiet is carried by the size and the ground, not by dimming the
         * only words on it.
         */
        isProduction
          ? 'border-transparent bg-[hsl(340_53%_36%)] text-white'
          : 'border-border bg-secondary text-foreground',
      )}
    >
      <span
        aria-hidden
        className={cn(
          'inline-block h-1.5 w-1.5 rounded-full',
          isProduction ? 'bg-white' : 'bg-[hsl(var(--success))]',
        )}
      />
      <span className={cn(isProduction && 'font-medium')}>
        {isProduction
          ? 'You are on production. Changes here affect live tenants.'
          : `Environment: ${name}`}
      </span>
    </div>
  );
}
