import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';
import { ChannelsWidget } from './ChannelsWidget';
import en from './locales.en.json';
import cs from './locales.cs.json';

/**
 * The WebSockets plugin's admin UI: the settings form, whose channel list (`"format":
 * "websocket-channels"`) is the channel and payload-type builder.
 */
export default definePluginAdmin({
  screens: { settings: SchemaSettings },
  configWidgets: { 'websocket-channels': ChannelsWidget },
  locales: { en, cs },
});
