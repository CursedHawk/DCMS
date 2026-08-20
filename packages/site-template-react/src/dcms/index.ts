/**
 * The DCMS-owned layer of a Mode B site, beside `src/api/`.
 *
 * Everything here is generated: the editor's **Refresh API** button re-pulls it
 * from DCMS along with `openapi.json` and the typed client, so a local edit is
 * overwritten. Configure it from your own code instead — `installAnalytics()`
 * takes options, and `<CookieConsent>` takes props.
 */

export {
  installAnalytics,
  sendPageview,
  trackEvent,
  consentState,
  consentNeeded,
  setConsent,
  onAnalyticsChange,
  type AnalyticsOptions,
  type ConsentState,
} from './analytics';

export { CookieConsent, type CookieConsentProps } from './CookieConsent';
