import { useEffect, useState, type CSSProperties, type ReactNode } from 'react';
import { consentNeeded, onAnalyticsChange, setConsent } from './analytics';

/**
 * The cookie notice for a Mode B site.
 *
 * Renders nothing at all unless there is genuinely something to consent to: the
 * tenant records analytics, the mode is `banner`, and this visitor has not
 * already answered. A site whose owner has switched analytics off shows no
 * banner, which is the whole point — a consent prompt for storage that never
 * happens is theatre, and it trains people to click "accept" without reading.
 *
 * Styling is inline and deliberately plain so it works before the site has any
 * CSS. Every class name is also emitted (`dcms-consent*`), so a site can restyle
 * it from its own stylesheet without touching this file — which matters because
 * "Refresh API" overwrites it.
 */

export interface CookieConsentProps {
  /** Replaces the default explanation. Keep it honest and specific. */
  message?: ReactNode;
  acceptLabel?: string;
  declineLabel?: string;
  /** Link to the site's own privacy/cookie policy. */
  policyUrl?: string;
  policyLabel?: string;
}

export function CookieConsent({
  message,
  acceptLabel = 'Accept',
  declineLabel = 'Decline',
  policyUrl,
  policyLabel = 'Privacy policy',
}: CookieConsentProps) {
  // Subscribed rather than read once: the banner has to appear when the status
  // check comes back, which is after the first render.
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    const sync = () => setVisible(consentNeeded());
    sync();
    return onAnalyticsChange(sync);
  }, []);

  if (!visible) return null;

  return (
    <div className="dcms-consent" role="dialog" aria-live="polite" aria-label="Cookie notice" style={bar}>
      <p className="dcms-consent-text" style={text}>
        {message ??
          'We use anonymous analytics to understand how this site is used. No data is stored until you accept.'}
        {policyUrl ? (
          <>
            {' '}
            <a href={policyUrl} style={link}>
              {policyLabel}
            </a>
          </>
        ) : null}
      </p>
      <div className="dcms-consent-actions" style={actions}>
        {/* Decline first in the DOM so it is not the default focus target — an
            accept button that catches a stray Enter is not consent. */}
        <button type="button" className="dcms-consent-decline" style={button} onClick={() => setConsent('denied')}>
          {declineLabel}
        </button>
        <button
          type="button"
          className="dcms-consent-accept"
          style={{ ...button, ...accept }}
          onClick={() => setConsent('granted')}
        >
          {acceptLabel}
        </button>
      </div>
    </div>
  );
}

const bar: CSSProperties = {
  position: 'fixed',
  left: '1rem',
  right: '1rem',
  bottom: '1rem',
  zIndex: 2147483000,
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  gap: '0.75rem 1rem',
  maxWidth: '44rem',
  margin: '0 auto',
  padding: '0.875rem 1rem',
  borderRadius: '0.5rem',
  background: 'var(--dcms-consent-bg, #111)',
  color: 'var(--dcms-consent-fg, #fff)',
  boxShadow: '0 8px 30px rgba(0,0,0,.25)',
  font: '400 .875rem/1.5 system-ui, sans-serif',
};

const text: CSSProperties = { margin: 0, flex: '1 1 16rem' };
const link: CSSProperties = { color: 'inherit', textUnderlineOffset: 2 };
const actions: CSSProperties = { display: 'flex', gap: '0.5rem', marginLeft: 'auto' };

const button: CSSProperties = {
  cursor: 'pointer',
  borderRadius: '0.375rem',
  padding: '0.4rem 0.9rem',
  font: 'inherit',
  fontWeight: 600,
  border: '1px solid currentColor',
  background: 'transparent',
  color: 'inherit',
};

const accept: CSSProperties = {
  background: 'var(--dcms-consent-fg, #fff)',
  color: 'var(--dcms-consent-bg, #111)',
  borderColor: 'transparent',
};
