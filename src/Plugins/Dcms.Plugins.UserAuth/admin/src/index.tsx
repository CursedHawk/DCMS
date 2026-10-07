import { definePluginAdmin } from '@dcms/plugin-ui';
import { AccessScreen } from './AccessScreen';
import { SignInScreen } from './SignInScreen';
import { UsersScreen } from './UsersScreen';
import en from './locales.en.json';
import cs from './locales.cs.json';

/** The User Authentication admin UI: site users and groups, who may reach what, and how people sign in. */
export default definePluginAdmin({
  screens: { users: UsersScreen, access: AccessScreen, 'sign-in': SignInScreen },
  locales: { en, cs },
});
