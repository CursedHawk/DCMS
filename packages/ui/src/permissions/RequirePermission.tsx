import { Lock } from 'lucide-react';
import { Button } from '../ui/button';
import { usePermissions, useCan, useIsSuperAdmin } from './context';
import { CenteredSpinner } from '../ui/spinner';

export interface NotPermittedLabels {
  title: string;
  description: string;
  back: string;
}

const DEFAULTS: NotPermittedLabels = {
  title: 'You do not have access to this page',
  description:
    'Your account is signed in, but it does not hold the permission this page needs. An administrator of this workspace can grant it.',
  back: 'Go back',
};

/**
 * What a caller sees where a page they cannot open would be.
 *
 * Says which permission is missing. That is not a security leak — the permission catalogue is
 * already readable by anyone who can open the roles screen, and naming it is the difference
 * between a person who can ask their admin for the right thing and a person who files a
 * ticket saying "it's broken".
 */
export function NotPermitted({
  permission,
  labels: partial,
  onBack,
}: {
  permission?: string;
  labels?: Partial<NotPermittedLabels>;
  onBack?: () => void;
}) {
  const labels = { ...DEFAULTS, ...partial };
  return (
    <div className="mx-auto flex w-full max-w-md flex-col items-center gap-3 px-6 py-24 text-center">
      <div className="flex h-12 w-12 items-center justify-center rounded-full bg-muted text-muted-foreground">
        <Lock className="h-5 w-5" aria-hidden />
      </div>
      <h1 className="text-lg font-semibold">{labels.title}</h1>
      <p className="text-sm text-muted-foreground">{labels.description}</p>
      {permission ? (
        <p className="text-sm text-muted-foreground">
          Required: <code className="rounded bg-muted px-1.5 py-0.5 text-xs">{permission}</code>
        </p>
      ) : null}
      {onBack ? (
        <Button variant="outline" className="mt-2" onClick={onBack}>
          {labels.back}
        </Button>
      ) : null}
    </div>
  );
}

/**
 * Page-level guard.
 *
 * Routes are reachable by typing a URL, so filtering the sidebar is not access control for the
 * UI — before this, a link the nav had hidden still rendered its page, which then made calls
 * the API refused and left a screen of failed queries and empty tables. Now the page is not
 * rendered at all and the reason is stated.
 *
 * While permissions are still loading this shows a spinner rather than the refusal: flashing
 * "you do not have access" at someone who does is worse than a moment of nothing.
 */
export function RequirePermission({
  perm,
  superAdmin,
  labels,
  onBack,
  children,
}: {
  perm?: string;
  superAdmin?: boolean;
  labels?: Partial<NotPermittedLabels>;
  onBack?: () => void;
  children: React.ReactNode;
}) {
  const me = usePermissions();
  const allowed = useCan(perm);
  const isSuperAdmin = useIsSuperAdmin();

  if (!me) return <CenteredSpinner />;
  if (superAdmin && !isSuperAdmin) return <NotPermitted labels={labels} onBack={onBack} />;
  if (!allowed) return <NotPermitted permission={perm} labels={labels} onBack={onBack} />;
  return <>{children}</>;
}
