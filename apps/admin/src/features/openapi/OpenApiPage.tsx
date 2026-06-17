import { ApiReferenceReact } from '@scalar/api-reference-react';
import { useQuery } from '@tanstack/react-query';
import { Check, Copy, Download, FileJson } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Page, PageHeader } from '../../components/Page';
import { Button } from '../../components/ui/button';
import { EmptyState } from '../../components/ui/empty-state';
import { CenteredSpinner } from '../../components/ui/spinner';
import { api } from '../../lib/api';
import { useTheme } from '../../lib/theme';

const AI_PREAMBLE =
  'This is the OpenAPI document for a tenant content API. Each tag is a configured ' +
  'plugin instance; its description states intent. Use it to write a client.\n\n';

export function OpenApiPage() {
  const { t } = useTranslation();
  const { resolved } = useTheme();
  const [copied, setCopied] = useState(false);

  const spec = useQuery({
    queryKey: ['openapi-spec'],
    queryFn: () => api.get<Record<string, unknown>>('/admin/openapi.json'),
  });

  const hasPaths =
    spec.data && typeof spec.data.paths === 'object' && Object.keys(spec.data.paths as object).length > 0;

  async function copyForAi() {
    await navigator.clipboard.writeText(AI_PREAMBLE + JSON.stringify(spec.data, null, 2));
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }

  function download() {
    const blob = new Blob([JSON.stringify(spec.data, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'openapi.json';
    a.click();
    URL.revokeObjectURL(url);
  }

  return (
    <Page className="max-w-none">
      <PageHeader
        title={t('openapi.title')}
        actions={
          hasPaths ? (
            <div className="flex gap-2">
              <Button variant="outline" onClick={copyForAi}>
                {copied ? <Check className="h-4 w-4" /> : <Copy className="h-4 w-4" />}
                {t('openapi.copyForAi')}
              </Button>
              <Button variant="outline" onClick={download}>
                <Download className="h-4 w-4" /> {t('openapi.downloadJson')}
              </Button>
            </div>
          ) : undefined
        }
      />

      {spec.isLoading ? (
        <CenteredSpinner />
      ) : hasPaths ? (
        <div className="overflow-hidden rounded-lg border">
          <ApiReferenceReact
            configuration={{
              content: spec.data,
              darkMode: resolved === 'dark',
              hideClientButton: true,
            }}
          />
        </div>
      ) : (
        <EmptyState icon={FileJson} title={t('openapi.title')} description={t('openapi.noInstances')} />
      )}
    </Page>
  );
}
