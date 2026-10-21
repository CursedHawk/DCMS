import { definePluginAdmin } from '@dcms/plugin-ui';
import { YouTubeChannelWidget } from './YouTubeChannelWidget';
import { YouTubeSettings } from './YouTubeSettings';
import en from './locales.en.json';
import cs from './locales.cs.json';

/**
 * The YouTube plugin's admin UI: its settings, where the config's `"format": "youtube-channel"`
 * field shows which channel a pasted link resolves to, under a Sync-now button.
 */
export default definePluginAdmin({
  screens: { settings: YouTubeSettings },
  configWidgets: { 'youtube-channel': YouTubeChannelWidget },
  locales: { en, cs },
});
