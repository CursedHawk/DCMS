import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Visitor accounts on a builder site (the VisitorAuth plugin).
 *
 * The published page's runtime (`hydrate.js`) does the work: it posts these forms to the
 * VisitorAuth instance, keeps the visitor's tokens in the browser, and shows or hides gated
 * sections once it knows who is signed in. The attribute names below are that contract, and
 * `runtimeParity.test.ts` checks the runtime reads each of them.
 *
 * In the canvas every gated section is shown, so an author can edit both states.
 */

/** On a `.dcms-form`: `login` or `register` makes it a VisitorAuth form rather than a Forms submission. */
export const VISITOR_FORM_ATTR = 'data-dcms-visitor';
/** On a section: `signed-in` or `signed-out` — shown only in that state. */
export const VISITOR_GATE_ATTR = 'data-dcms-visitor-gate';
/** Filled with the signed-in visitor's display name (or email). */
export const VISITOR_NAME_ATTR = 'data-dcms-visitor-name';
/** A button that signs the visitor out. */
export const VISITOR_LOGOUT_ATTR = 'data-dcms-visitor-logout';

const instanceTrait = {
  name: 'data-instance',
  label: 'Accounts instance',
  kind: 'contentRef',
  required: true,
  accepts: { pluginId: 'visitor-auth' },
  description: 'Which Visitor Authentication instance the accounts belong to.',
} as const;

export const visitorSpecs: DcmsComponentSpec[] = [
  {
    type: 'VisitorSignIn',
    label: 'Sign-in form',
    category: 'form',
    tag: 'form',
    icon: 'form',
    identityClass: 'dcms-visitor-sign-in',
    acceptsChildren: true,
    order: 20,
    requiredPluginId: 'visitor-auth',
    docs: 'Signs a site visitor in to their account.',
    traits: [
      instanceTrait,
      { name: 'data-success', label: 'Message after signing in', kind: 'text', default: 'Welcome back.' },
      { name: 'data-redirect', label: 'Go to after signing in', kind: 'url' },
    ],
    snippet: `<form class="dcms-form dcms-visitor-sign-in" ${VISITOR_FORM_ATTR}="login" data-instance="" data-success="Welcome back.">
  <label class="dcms-field"><span>Email</span><input name="email" type="email" autocomplete="email" required /></label>
  <label class="dcms-field"><span>Password</span><input name="password" type="password" autocomplete="current-password" required /></label>
  <button class="dcms-button" type="submit">Sign in</button>
</form>`,
  },
  {
    type: 'VisitorSignUp',
    label: 'Registration form',
    category: 'form',
    tag: 'form',
    icon: 'form',
    identityClass: 'dcms-visitor-sign-up',
    acceptsChildren: true,
    order: 21,
    requiredPluginId: 'visitor-auth',
    docs: 'Creates a site visitor account and signs the visitor in.',
    traits: [
      instanceTrait,
      { name: 'data-success', label: 'Message after registering', kind: 'text', default: 'Your account is ready.' },
      { name: 'data-redirect', label: 'Go to after registering', kind: 'url' },
    ],
    snippet: `<form class="dcms-form dcms-visitor-sign-up" ${VISITOR_FORM_ATTR}="register" data-instance="" data-success="Your account is ready.">
  <label class="dcms-field"><span>Name</span><input name="displayName" type="text" autocomplete="name" /></label>
  <label class="dcms-field"><span>Email</span><input name="email" type="email" autocomplete="email" required /></label>
  <label class="dcms-field"><span>Password</span><input name="password" type="password" autocomplete="new-password" required /></label>
  <button class="dcms-button" type="submit">Create account</button>
</form>`,
  },
  {
    type: 'VisitorGate',
    label: 'Members-only section',
    category: 'form',
    tag: 'div',
    icon: 'card',
    identityClass: 'dcms-visitor-gate',
    acceptsChildren: true,
    order: 22,
    requiredPluginId: 'visitor-auth',
    docs: 'Shown only to signed-in visitors — or only to signed-out ones.',
    traits: [
      instanceTrait,
      {
        name: VISITOR_GATE_ATTR,
        label: 'Show to',
        kind: 'select',
        default: 'signed-in',
        options: [
          { value: 'signed-in', label: 'Signed-in visitors' },
          { value: 'signed-out', label: 'Signed-out visitors' },
        ],
      },
    ],
    snippet: `<div class="dcms-visitor-gate" ${VISITOR_GATE_ATTR}="signed-in" data-instance="">
  <p>Welcome back, <span ${VISITOR_NAME_ATTR}>friend</span>.</p>
  <button class="dcms-button" type="button" ${VISITOR_LOGOUT_ATTR}>Sign out</button>
</div>`,
  },
];
