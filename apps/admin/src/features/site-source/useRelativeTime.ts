import { useTranslation } from 'react-i18next';
import { relativeTime } from '@dcms/core';

/**
 * "2 hours ago" in the reader's own language. Shared by Source Control history and Deployments.
 * The formatting itself is `@dcms/core`; what this hook adds is the active language.
 */
export function useRelativeTime() {
  const { i18n } = useTranslation();
  return (iso?: string | null): string => relativeTime(iso, i18n.language);
}
