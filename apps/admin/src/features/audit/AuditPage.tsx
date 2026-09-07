import { useTranslation } from 'react-i18next';
import { Page, PageHeader } from '@dcms/ui';
import { AuditLogViewer } from './AuditLogViewer';

/**
 * The current tenant's audit log.
 *
 * Everything here lives in {@link AuditLogViewer}, which the platform Tenants page reuses to
 * show one tenant's log without switching the whole app to it.
 */
export function AuditPage() {
  const { t } = useTranslation();

  return (
    <Page>
      <PageHeader title={t('audit.title')} description={t('audit.description')} />
      <AuditLogViewer />
    </Page>
  );
}
