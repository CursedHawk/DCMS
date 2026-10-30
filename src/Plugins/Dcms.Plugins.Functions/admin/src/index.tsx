import { definePluginAdmin, SchemaSettings } from '@dcms/plugin-ui';
import { CodeScreen } from './CodeScreen';
import { DeploymentsScreen } from './DeploymentsScreen';
import { KeysScreen } from './KeysScreen';
import { RunsScreen } from './RunsScreen';
import { SecretsScreen } from './SecretsScreen';
import en from './locales.en.json';
import cs from './locales.cs.json';

/** The Functions admin UI (ADR 0029): code, deployments, runs, API keys, secrets and settings. */
export default definePluginAdmin({
  screens: {
    settings: SchemaSettings,
    code: CodeScreen,
    deployments: DeploymentsScreen,
    runs: RunsScreen,
    keys: KeysScreen,
    secrets: SecretsScreen,
  },
  locales: { en, cs },
});
