import type { TFunction } from 'i18next';
import { toast } from 'sonner';
import { ApiError } from '@dcms/admin-client';

/**
 * The single place a failed request becomes something the user sees.
 *
 * Every mutation in the app used to do the same thing inline — `toast.error` with a ternary on
 * `ApiError` — which showed the message and threw away the trace id that came back with it.
 * That id is the only handle anyone has on the actual failure: it resolves to the span tree in
 * Tempo, the log lines in Loki and the audit rows in Postgres, all of which are keyed on it. A
 * report that says "saving didn't work this morning" costs an hour of guessing; a report that
 * quotes an id costs a minute of looking.
 *
 * The id goes in the toast's action button rather than its body on purpose. It is noise to
 * everyone whose action simply succeeded on the retry, and it is exactly one click away for
 * the person who is about to open a ticket.
 *
 * @param prefix Prepended to the message, for the callers where one toast is not enough to
 * say which of several things failed — an upload of twelve files, say.
 */
export function toastApiError(error: unknown, t: TFunction, prefix?: string): void {
  const api = error instanceof ApiError ? error : undefined;
  const message = prefix
    ? `${prefix}: ${api?.message ?? t('errors.generic', { defaultValue: 'Something went wrong.' })}`
    : api?.message ?? t('errors.generic', { defaultValue: 'Something went wrong.' });

  // Trace first: it is the id that resolves across all three stores. The request id is a
  // fallback for the case where tracing was not sampled or the response never reached a
  // service that starts spans (a proxy error, say).
  const id = api?.traceId ?? api?.requestId;
  if (!id) {
    toast.error(message);
    return;
  }

  toast.error(message, {
    description: t('errors.traceId', { id, defaultValue: 'Trace {{id}}' }),
    action: {
      label: t('errors.copyId', { defaultValue: 'Copy id' }),
      onClick: () => {
        // navigator.clipboard is undefined on an insecure origin, which is how this app is
        // reached in local development, and writeText can still be refused by permissions
        // policy where it does exist. A silently dead button is worse than no button, so both
        // failures fall back to showing the id in a toast the user can select by hand.
        const clipboard = navigator.clipboard;
        if (!clipboard) {
          toast.message(id);
          return;
        }
        void clipboard.writeText(id).then(
          () => toast.success(t('errors.copied', { defaultValue: 'Copied' })),
          () => toast.message(id),
        );
      },
    },
  });
}
