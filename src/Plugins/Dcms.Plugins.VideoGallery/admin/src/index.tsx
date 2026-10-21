import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';

/** The Video Gallery plugin's admin UI: its settings, the form its config schema describes. */
export default definePluginAdmin({
  screens: { settings: SchemaSettings },
});
