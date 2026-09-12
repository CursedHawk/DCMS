import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import globals from 'globals';

/*
 * Deliberately narrow.
 *
 * `tsc` already runs with `strict`, `noUnusedLocals` and `noUnusedParameters` on every package,
 * so the whole "unused variable / implicit any" family is covered by the build and repeating it
 * here would only produce two reports of the same problem. What is left is the class of bug a
 * type checker cannot see: hooks called conditionally, a dependency array that lies, a promise
 * dropped on the floor.
 *
 * Type-aware linting is off. It needs a program per package and turns a two-second lint into a
 * minute on a four-core box that also has to build .NET.
 */
export default tseslint.config(
  {
    ignores: [
      '**/dist/**',
      '**/node_modules/**',
      '**/*.tsbuildinfo',
      'apps/*/dist/**',
      // Generated at request time by the .NET emitter, and shipped to tenants as-is.
      'packages/site-builder-toolchain/**',
      // k6 scripts. A different runtime with its own globals and its own module resolution;
      // linting them against browser or node rules reports 130 things that are all fine.
      'loadtest/**',
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{js,ts,tsx}'],
    languageOptions: { globals: { ...globals.browser, ...globals.es2022 } },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // tsc's noUnusedLocals already reports these, with better messages.
      '@typescript-eslint/no-unused-vars': ['error', { caughtErrors: 'none', args: 'none', varsIgnorePattern: '^_' }],
      // The escape hatch is used deliberately in a handful of places, each with a comment
      // saying why. Flagging them all would train everyone to ignore the linter.
      '@typescript-eslint/no-explicit-any': 'off',
      '@typescript-eslint/no-empty-object-type': 'off',
      // TypeScript resolves every identifier already, and does it knowing the lib and DOM
      // types. eslint's copy only knows the globals list it was handed.
      'no-undef': 'off',
      // `catch (e) {}` where the error genuinely does not matter — a failed localStorage read,
      // a non-JSON error body — is the idiom throughout, always with a comment saying so.
      // The base rule cannot see TypeScript — it reports every enum member in gjs-parse as
      // unused. The typescript-eslint one replaces it.
      'no-unused-vars': 'off',
    },
  },
  {
    // The runtime shipped inside every published site. Plain browser JS by design — it runs
    // before any bundle and cannot assume a module system.
    files: ['src/Services/Dcms.SiteBuilder/Runtime/*.js'],
    languageOptions: { globals: { ...globals.browser } },
  },
  {
    /*
     * End-to-end tests. Node, not a browser, and not React.
     *
     * The react-hooks rules have to come off here specifically: Playwright names a fixture's
     * "hand the value to the test" callback `use`, and the rules-of-hooks check sees any call
     * to something named `use` as React's `use` hook being called outside a component. Every
     * fixture in the suite is then an error, for code that has nothing to do with React.
     */
    files: ['e2e/**/*.ts'],
    languageOptions: { globals: { ...globals.node } },
    rules: {
      'react-hooks/rules-of-hooks': 'off',
      'react-hooks/exhaustive-deps': 'off',
    },
  },
  {
    /*
     * Accessibility. Installed because two files already carried
     * `eslint-disable-next-line jsx-a11y/...` comments for a plugin that had never been added —
     * somebody meant this to be enforced and it silently was not.
     */
    files: ['**/*.tsx'],
    plugins: { 'jsx-a11y': jsxA11y },
    rules: {
      ...jsxA11y.flatConfigs.recommended.rules,
      /*
       * `<label><span>Older than</span><Input /></label>` is correct — nesting associates them
       * with no id needed — but the rule only recognises intrinsic elements, so every wrapped
       * `@dcms/ui` control reads as a label with nothing in it. Name them.
       */
      'jsx-a11y/label-has-associated-control': [
        'error',
        {
          controlComponents: ['Input', 'Textarea', 'Select', 'Switch', 'Checkbox', 'TagsInput'],
          // Label text is routinely a `{t(...)}` two spans down, for the checkbox-plus-hint
          // pattern the builder panels use. The default depth of 2 stops short of it.
          depth: 4,
        },
      ],
      /*
       * Downgraded, with a reason.
       *
       * Every autoFocus in this codebase is on a field that appears BECAUSE the user just
       * clicked something — "new branch", "rename folder", "add page". Moving focus into a
       * control the user summoned is the correct behaviour and what WCAG asks for; the rule
       * exists for autoFocus on page load, which is a different thing it cannot distinguish.
       * Left as a warning so a genuine page-load autoFocus still shows up in review.
       */
      'jsx-a11y/no-autofocus': 'warn',
    },
  },
  {
    /*
     * No polling in the SPAs.
     *
     * <p>Both apps are kept current by SignalR — a hub pushes the name of a class of data that
     * changed and the matching queries refetch. The gaps a push cannot cover (a reconnect, a
     * tab becoming visible, a browser coming back online) are covered once, in each app's
     * shell, by `useHubRevalidation`. A `refetchInterval` added to a query re-opens the problem
     * all of that removed: load that scales with the number of tabs somebody left open, and a
     * second answer to "is this fresh?" that disagrees with the first.</p>
     *
     * <p>Data that genuinely is a periodic sample — read from Prometheus or Loki, which
     * announce nothing — keeps its period, but keeps it on the server: see
     * `PlatformSampleBroadcaster`, which broadcasts a tick so the sample is taken once per
     * replica rather than once per open browser.</p>
     */
    files: ['apps/*/src/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector: "Property[key.name='refetchInterval'], Property[key.value='refetchInterval']",
          message:
            'No polling in the SPAs. The hub pushes; useHubRevalidation covers reconnect, tab focus and coming back online. A genuinely periodic sample belongs on the server — see PlatformSampleBroadcaster.',
        },
      ],
    },
  },
  {
    files: ['apps/*/src/**/*.tsx'],
    plugins: { 'react-refresh': reactRefresh },
    rules: {
      // Warn, not error: several feature files legitimately export a component next to its
      // hooks, and splitting them would be churn for a dev-server nicety.
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
    },
  },
  {
    // Test files run in vitest, which supplies globals the source never sees.
    files: ['**/*.test.{ts,tsx}'],
    rules: { '@typescript-eslint/no-non-null-assertion': 'off' },
  },
  {
    files: ['**/*.worker.ts', '**/workers/**/*.ts'],
    languageOptions: { globals: { self: 'readonly', postMessage: 'readonly' } },
  },
  {
    /*
     * Repo tooling. Node, and `.mjs` — which the main block above does not match at all, so
     * these files get `js.configs.recommended` with no globals and every `console` and
     * `process` reads as undefined.
     */
    files: ['scripts/**/*.mjs'],
    languageOptions: { globals: { ...globals.node } },
  },
);
