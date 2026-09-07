/**
 * `@dcms/ui` — the design system shared by every DCMS operator SPA.
 *
 * Extracted from apps/admin when the platform SPA was added: two hand-maintained
 * copies of the button, the theme provider and the error toast is how the second
 * app drifts from the first. The stylesheet half lives in `./theme.css` and is
 * imported from each app's own index.css, because Tailwind's content detection
 * has to root at the app rather than here.
 */
export * from './ui';
export * from './cn';
export * from './theme';
export * from './errors';
export * from './Page';
export * from './ConfirmDeleteDialog';
export * from './CopyButton';
export * from './Toaster';
export * from './shell';
