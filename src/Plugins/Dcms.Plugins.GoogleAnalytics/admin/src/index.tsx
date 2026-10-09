import { definePluginAdmin } from '@dcms/plugin-ui';
import { SitesScreen } from './SitesScreen';
import en from './locales.en.json';
import cs from './locales.cs.json';

/** The Google Analytics plugin's admin UI: the Measurement ID of each site. */
export default definePluginAdmin({
  screens: { sites: SitesScreen },
  locales: { en, cs },
});
