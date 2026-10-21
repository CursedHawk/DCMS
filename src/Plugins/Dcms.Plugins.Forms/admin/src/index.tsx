import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';
import { InboxScreen } from './InboxScreen';
import en from './locales.en.json';
import cs from './locales.cs.json';

/** The Forms plugin's admin UI: its inbox, its settings and its words. */
export default definePluginAdmin({
  screens: { settings: SchemaSettings, inbox: InboxScreen },
  locales: { en, cs },
});
