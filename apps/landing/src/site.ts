export const SITE_URL = 'https://highgeek.eu';
/** The admin console. Signing in there is also where an account is created. */
export const CONSOLE_URL = 'https://admin.highgeek.eu';
export const CONTACT_EMAIL = 'admin@highgeek.eu';
/**
 * The controller named on the legal pages. GDPR Art. 13 wants the operator's full identity, so
 * if DCMS is run by a company or a registered sole trader, put its registered name, address and
 * ID number (IČO) here — both pages render whatever these say.
 */
export const OPERATOR = 'HighGeek';
export const OPERATOR_DETAILS = 'Czech Republic';
/** Shown on both legal pages and stamped into their structured data. */
export const LEGAL_UPDATED = '2026-10-05';

export interface Route {
  path: string;
  /** Where scripts/prerender.mjs writes it; nginx serves `/x` from `x.html`. */
  file: string;
  title: string;
  description: string;
  /** In the sitemap and canonical; false = noindex. */
  indexed: boolean;
}

export const routes: Route[] = [
  {
    path: '/',
    file: 'index.html',
    title: 'DCMS — website builder and headless CMS on your own domain',
    description:
      'Build websites visually, in React or with an AI agent, manage content with plugins, and publish to your own domain with automatic HTTPS. Every site is versioned in git and hosted in the EU.',
    indexed: true,
  },
  {
    path: '/privacy-policy',
    file: 'privacy-policy.html',
    title: 'Privacy Policy — DCMS',
    description:
      'What personal data DCMS processes, why, for how long, who it is shared with, and how to exercise your rights under the GDPR.',
    indexed: true,
  },
  {
    path: '/terms-of-service',
    file: 'terms-of-service.html',
    title: 'Terms of Service — DCMS',
    description:
      'The terms that govern your use of DCMS: accounts, workspaces, acceptable use, your content, AI features, availability and liability.',
    indexed: true,
  },
  {
    path: '/404',
    file: '404.html',
    title: 'Page not found — DCMS',
    description: 'This page does not exist.',
    indexed: false,
  },
];
