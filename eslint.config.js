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
);
