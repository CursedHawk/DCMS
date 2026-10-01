import { definePluginAdmin } from '@dcms/plugin-ui';
import { MetaConnectionWidget } from '@dcms/plugin-meta-admin';

/**
 * The Facebook plugin's admin UI: no screens of its own, one widget — its config's
 * `"format": "meta-connection"` field picks an account connected through Meta's consent.
 */
export default definePluginAdmin({
  configWidgets: { 'meta-connection': MetaConnectionWidget },
});
