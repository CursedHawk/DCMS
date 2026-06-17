import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from '../locales/en/common.json';
import cs from '../locales/cs/common.json';

export const LANGUAGES = [
  { code: 'en', label: 'English' },
  { code: 'cs', label: 'Čeština' },
] as const;

const STORAGE_KEY = 'dcms.lang';

void i18n.use(initReactI18next).init({
  resources: {
    en: { common: en },
    cs: { common: cs },
  },
  lng: localStorage.getItem(STORAGE_KEY) ?? 'en',
  fallbackLng: 'en',
  defaultNS: 'common',
  interpolation: { escapeValue: false },
});

export function setLanguage(code: string): void {
  localStorage.setItem(STORAGE_KEY, code);
  void i18n.changeLanguage(code);
}

export default i18n;
