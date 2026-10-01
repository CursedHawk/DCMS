import { definePluginAdmin } from '@dcms/plugin-ui';
import { ConsoleScreen } from './ConsoleScreen';
import en from './locales.en.json';
import cs from './locales.cs.json';

/** The AI Chatbot plugin's admin UI: the live chat console. */
export default definePluginAdmin({
  screens: { console: ConsoleScreen },
  locales: { en, cs },
});
