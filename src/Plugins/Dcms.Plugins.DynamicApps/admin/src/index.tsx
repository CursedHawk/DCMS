import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';
import { ConfigurationScreen } from './ConfigurationScreen';
import { RecordsScreen } from './RecordsScreen';
import en from './locales.en.json';
import cs from './locales.cs.json';

/** The Dynamic Apps admin UI: an app's configuration and its records, as tabs of the app's page. */
export default definePluginAdmin({
  screens: { settings: SchemaSettings, configuration: ConfigurationScreen, records: RecordsScreen },
  locales: { en, cs },
});
