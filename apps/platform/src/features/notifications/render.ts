import { parseParams, type PlatformNotification } from './api';

/**
 * The sentence for a notification, built here from the facts the server stored.
 *
 * <p>Nothing renders prose into the database. A sentence written at raise time is frozen against
 * a certificate that has since been renamed, and cannot be improved without rewriting history —
 * so the row carries `kind` plus parameters, and the wording lives in the console where it can
 * be changed by editing this file.</p>
 *
 * <p>Unlike the tenant console this is not routed through i18n keys, because the platform
 * console ships one language and hardcodes every other string it shows. The shape is the same
 * one i18n would need, so that stays possible.</p>
 */
export interface RenderedNotification {
  title: string;
  body: string;
}

const listOf = (value: unknown): string =>
  Array.isArray(value) ? value.filter((v) => typeof v === 'string').join(', ') : '';

const nameOf = (params: Record<string, unknown>): string =>
  typeof params.name === 'string' && params.name.length > 0 ? params.name : 'A certificate';

export function render(notification: PlatformNotification): RenderedNotification {
  const p = parseParams(notification.paramsJson);
  const name = nameOf(p);
  const names = listOf(p.identifiers);
  const error = typeof p.error === 'string' ? p.error : '';
  const days = typeof p.daysRemaining === 'number' ? p.daysRemaining : null;

  switch (notification.kind) {
    case 'certificate.issued':
      return {
        title: `${name} was issued`,
        body: names ? `Now covering ${names}.` : 'A new certificate is being served.',
      };

    // The CA answered and said no. This one costs an attempt against the weekly ceiling, and
    // the fix is almost always a DNS record -- so the CA's own sentence is the body, verbatim.
    case 'certificate.failed':
      return {
        title: `${name} could not be issued`,
        body: error || 'The certificate authority refused the order.',
      };

    // Nothing was sent to the CA. Deliberately worded so it cannot be mistaken for the case
    // above: it costs nothing, and the fix is in our own configuration rather than in DNS.
    case 'certificate.blocked':
      return {
        title: `${name} was not attempted`,
        body: error || 'Nothing was sent to the certificate authority.',
      };

    case 'certificate.expiring':
      return {
        title: `${name} expires in ${days ?? 0} day${days === 1 ? '' : 's'}`,
        body: 'It is inside the renewal window and has not been replaced yet.',
      };

    case 'certificate.expired':
      return {
        title: `${name} has expired`,
        body: names
          ? `Every hostname under ${names} is refusing TLS.`
          : 'The hostnames it covered are refusing TLS.',
      };

    // A kind this build has never heard of -- an older console against a newer server. Showing
    // the discriminator is worth more than hiding the row: it is still a real event, and the
    // link still goes somewhere useful.
    default:
      return { title: notification.kind, body: '' };
  }
}
