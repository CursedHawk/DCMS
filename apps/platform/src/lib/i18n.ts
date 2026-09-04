import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from '../locales/en/common.json';
import cs from '../locales/cs/common.json';

/**
 * The console's own i18n instance.
 *
 * <p>Only the keys the SHARED components reach for — @dcms/admin-ui's error toast and copy
 * button call useTranslation() against whatever instance the host app initialised, so an app
 * that skipped this would render raw key names inside a borrowed component. The console's own
 * copy is written inline in English: it has one audience, the operators of this platform, and
 * a translation layer over six screens would be indirection with nothing on the other side.</p>
 */
void i18n.use(initReactI18next).init({
  resources: { en: { common: en }, cs: { common: cs } },
  lng: localStorage.getItem('dcms.lang') ?? 'en',
  fallbackLng: 'en',
  defaultNS: 'common',
  interpolation: { escapeValue: false },
});

export default i18n;
