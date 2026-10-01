import { definePluginAdmin } from '@dcms/plugin-ui';
import { AccentWidget } from './AccentWidget';
import { ModerationScreen } from './ModerationScreen';
import { OverviewScreen } from './OverviewScreen';
import { cs, en } from './locales';
import './guestbook.css';

/**
 * The Guestbook sample's admin UI module. Its screen ids match AdminScreens in the manifest;
 * its widget key matches `"format": "guestbook-accent"` in the config schema.
 */
export default definePluginAdmin({
  screens: {
    overview: OverviewScreen,
    moderation: ModerationScreen,
  },
  configWidgets: {
    'guestbook-accent': AccentWidget,
  },
  locales: { en, cs },
});
