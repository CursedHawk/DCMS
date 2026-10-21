import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';

/** The File Downloads plugin's admin UI: its settings, the form its config schema describes. */
export default definePluginAdmin({
  screens: { settings: SchemaSettings },
});
