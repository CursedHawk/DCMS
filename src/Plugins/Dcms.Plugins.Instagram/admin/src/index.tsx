import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';
import { MetaConnectionWidget } from '@dcms/plugin-meta-admin';

/**
 * The Instagram plugin's admin UI: its settings, with one widget of its own — its config's
 * `"format": "meta-connection"` field picks an account connected through Meta's consent.
 */
export default definePluginAdmin({
  screens: { settings: SchemaSettings },
  configWidgets: { 'meta-connection': MetaConnectionWidget },
});
