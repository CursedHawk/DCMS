import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';
import { DashboardScreen } from './DashboardScreen';
import en from './locales.en.json';
import cs from './locales.cs.json';

/** The Analytics plugin's admin UI: the dashboard. */
export default definePluginAdmin({
  screens: { settings: SchemaSettings, dashboard: DashboardScreen },
  locales: { en, cs },
});
