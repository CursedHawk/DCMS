import { Cookie, X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../components/ui/button';

const DISMISSED_KEY = 'dcms.storage-notice';

/**
 * Transparency notice about what the admin app keeps in the browser.
 *
 * Deliberately a *notice*, not a consent gate. Everything the admin stores is
 * strictly necessary for a tool you have signed in to use — the session tokens
 * themselves, the selected workspace, the theme, sidebar state, and which editor
 * tabs were open. Under ePrivacy that class of storage does not require consent,
 * and offering a "decline" that would break sign-in would be dishonest.
 *
 * Hosted tenant sites are the opposite case: their only storage is analytics, so
 * there the runtime asks first and records nothing until told yes (see
 * SiteBuilder/Runtime/hydrate.js).
 */
export function StorageNotice() {
  const { t } = useTranslation();
  const [dismissed, setDismissed] = useState(() => {
    try {
      return localStorage.getItem(DISMISSED_KEY) === '1';
    } catch {
      // Storage unavailable: showing the notice once per load is better than
      // crashing the shell over it.
      return false;
    }
  });

  if (dismissed) return null;

  const dismiss = () => {
    setDismissed(true);
    try {
      localStorage.setItem(DISMISSED_KEY, '1');
    } catch {
      /* the in-memory state still hides it for this session */
    }
  };

  return (
    <div
      role="status"
      className="dcms-pop fixed inset-x-4 bottom-4 z-50 mx-auto flex max-w-2xl flex-wrap items-center gap-3 rounded-lg border bg-card p-3 text-sm shadow-lg"
    >
      <Cookie className="h-4 w-4 shrink-0 text-muted-foreground" />
      <p className="min-w-0 flex-1 text-muted-foreground">{t('storage.notice')}</p>
      <Button size="sm" onClick={dismiss}>
        {t('storage.gotIt')}
      </Button>
      <button
        type="button"
        onClick={dismiss}
        aria-label={t('actions.close')}
        className="text-muted-foreground hover:text-foreground"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}
