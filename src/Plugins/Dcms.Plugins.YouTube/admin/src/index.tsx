import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';
import { YouTubeChannelWidget } from './YouTubeChannelWidget';
import en from './locales.en.json';
import cs from './locales.cs.json';

/**
 * The YouTube plugin's admin UI: its settings, where the config's `"format": "youtube-channel"`
 * field shows which channel a pasted link resolves to, with a Sync-now button beside it.
 */
export default definePluginAdmin({
  screens: { settings: SchemaSettings },
  configWidgets: { 'youtube-channel': YouTubeChannelWidget },
  locales: { en, cs },
});
