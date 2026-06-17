import { FileText, Plus } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Page, PageHeader } from '../../components/Page';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { EmptyState } from '../../components/ui/empty-state';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../components/ui/select';
import { CenteredSpinner } from '../../components/ui/spinner';
import { TBody, TD, TH, THead, TR, Table } from '../../components/ui/table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { type ContentTypeDef, usePluginCatalog, usePluginInstances } from '../plugins/api';
import { ContentEditor } from './ContentEditor';
import { useContentItems } from './api';

export function ContentPage() {
  const { t } = useTranslation();
  const instances = usePluginInstances();
  const catalog = usePluginCatalog();
  const [instanceId, setInstanceId] = useState<string>('');

  // Default to the first instance that actually defines content types.
  const manifestFor = (pluginId: string) => catalog.data?.find((m) => m.id === pluginId);
  const authorable = (instances.data ?? []).filter(
    (i) => (manifestFor(i.pluginId)?.contentTypes.length ?? 0) > 0,
  );

  useEffect(() => {
    if (!instanceId && authorable.length) setInstanceId(authorable[0].id);
  }, [authorable, instanceId]);

  const instance = authorable.find((i) => i.id === instanceId);
  const types = instance ? (manifestFor(instance.pluginId)?.contentTypes ?? []) : [];

  if (instances.isLoading || catalog.isLoading) {
    return (
      <Page>
        <PageHeader title={t('content.title')} />
        <CenteredSpinner />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title={t('content.title')}
        actions={
          authorable.length > 0 ? (
            <Select value={instanceId} onValueChange={setInstanceId}>
              <SelectTrigger className="w-56">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {authorable.map((i) => (
                  <SelectItem key={i.id} value={i.id}>
                    {i.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          ) : undefined
        }
      />

      {!instance ? (
        <EmptyState icon={FileText} title={t('content.title')} description={t('content.selectInstance')} />
      ) : (
        <Tabs defaultValue={types[0]?.name} key={instance.id}>
          <TabsList>
            {types.map((ct) => (
              <TabsTrigger key={ct.name} value={ct.name}>
                {ct.name}
              </TabsTrigger>
            ))}
          </TabsList>
          {types.map((ct) => (
            <TabsContent key={ct.name} value={ct.name}>
              <ContentTypePanel instanceId={instance.id} contentType={ct} />
            </TabsContent>
          ))}
        </Tabs>
      )}
    </Page>
  );
}

function ContentTypePanel({
  instanceId,
  contentType,
}: {
  instanceId: string;
  contentType: ContentTypeDef;
}) {
  const { t } = useTranslation();
  const items = useContentItems(instanceId);
  const [editorItem, setEditorItem] = useState<string | null | undefined>(undefined);

  const filtered = (items.data ?? []).filter((i) => i.contentType === contentType.name);

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button size="sm" onClick={() => setEditorItem(null)}>
          <Plus className="h-4 w-4" /> {t('content.newItem')}
        </Button>
      </div>

      {items.isLoading ? (
        <CenteredSpinner />
      ) : filtered.length > 0 ? (
        <Table>
          <THead>
            <TR>
              <TH>{t('common.slug')}</TH>
              <TH>{t('common.status')}</TH>
              <TH>{t('common.updated')}</TH>
            </TR>
          </THead>
          <TBody>
            {filtered.map((item) => (
              <TR key={item.id} className="cursor-pointer" onClick={() => setEditorItem(item.id)}>
                <TD className="font-medium">{item.slug}</TD>
                <TD>
                  <Badge tone={item.status === 'Published' ? 'success' : 'secondary'}>
                    {item.status}
                  </Badge>
                </TD>
                <TD className="text-muted-foreground">{new Date(item.updatedAt).toLocaleString()}</TD>
              </TR>
            ))}
          </TBody>
        </Table>
      ) : (
        <EmptyState icon={FileText} title={contentType.name} />
      )}

      {editorItem !== undefined ? (
        <ContentEditor
          instanceId={instanceId}
          contentType={contentType}
          itemId={editorItem}
          onClose={() => setEditorItem(undefined)}
          onSaved={() => setEditorItem(undefined)}
        />
      ) : null}
    </div>
  );
}
