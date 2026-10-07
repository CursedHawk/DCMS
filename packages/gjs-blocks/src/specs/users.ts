import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Enterprise users on a builder site (the User Authentication plugin, ADR 0022).
 *
 * Signing in happens at the edge, not on the page: the links below go to the edge's own
 * `/.edge/site/signin` and `/.edge/site/signout`, which run the tenant's sign-in and keep the
 * session in a cookie the page never sees. The runtime (`hydrate.js`) asks `/.edge/site/me` who
 * is signed in, then shows or hides the sections below and fills in the name. Those sections are
 * cosmetic: what must stay private belongs under a path the site's access rules protect.
 *
 * In the canvas every section is shown, so an author can edit both states.
 */

/** On a link: sends the visitor to sign in, and back to this page afterwards. */
export const USER_SIGNIN_ATTR = 'data-dcms-user-signin';
/** On a link: signs the user out of the site (and of the tenant's sign-in). */
export const USER_SIGNOUT_ATTR = 'data-dcms-user-signout';
/** On a section: `signed-in` or `signed-out` — shown only in that state. */
export const USER_GATE_ATTR = 'data-dcms-user-gate';
/** Filled with the signed-in user's name (or email). */
export const USER_NAME_ATTR = 'data-dcms-user-name';

export const SIGNIN_PATH = '/.edge/site/signin';
export const SIGNOUT_PATH = '/.edge/site/signout';

export const userSpecs: DcmsComponentSpec[] = [
  {
    type: 'UserSignIn',
    label: 'Sign-in button',
    category: 'form',
    tag: 'a',
    icon: 'button',
    identityClass: 'dcms-user-sign-in',
    acceptsChildren: false,
    order: 23,
    requiredPluginId: 'user-auth',
    docs: 'Sends the visitor to sign in with your organisation, then back to this page.',
    traits: [],
    snippet: `<a class="dcms-button dcms-user-sign-in" href="${SIGNIN_PATH}" ${USER_SIGNIN_ATTR}>Sign in</a>`,
  },
  {
    type: 'UserSignOut',
    label: 'Sign-out button',
    category: 'form',
    tag: 'a',
    icon: 'button',
    identityClass: 'dcms-user-sign-out',
    acceptsChildren: false,
    order: 24,
    requiredPluginId: 'user-auth',
    docs: 'Signs the user out of the site.',
    traits: [],
    snippet: `<a class="dcms-button dcms-user-sign-out" href="${SIGNOUT_PATH}" ${USER_SIGNOUT_ATTR}>Sign out</a>`,
  },
  {
    type: 'UserGate',
    label: 'Signed-in section',
    category: 'form',
    tag: 'div',
    icon: 'card',
    identityClass: 'dcms-user-gate',
    acceptsChildren: true,
    order: 25,
    requiredPluginId: 'user-auth',
    docs: 'Shown only to signed-in users — or only to signed-out ones. To keep content private, protect its path in Site access instead.',
    traits: [
      {
        name: USER_GATE_ATTR,
        label: 'Show to',
        kind: 'select',
        default: 'signed-in',
        options: [
          { value: 'signed-in', label: 'Signed-in users' },
          { value: 'signed-out', label: 'Signed-out visitors' },
        ],
      },
    ],
    snippet: `<div class="dcms-user-gate" ${USER_GATE_ATTR}="signed-in">
  <p>Signed in as <span ${USER_NAME_ATTR}>you</span>.</p>
  <a class="dcms-button" href="${SIGNOUT_PATH}" ${USER_SIGNOUT_ATTR}>Sign out</a>
</div>`,
  },
];
