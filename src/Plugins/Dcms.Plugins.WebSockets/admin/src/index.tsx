import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';
import en from './locales.en.json';
import cs from './locales.cs.json';

/** The WebSockets plugin's admin UI: its settings (the channels) and its words. */
export default definePluginAdmin({
  screens: { settings: SchemaSettings },
  locales: { en, cs },
});
