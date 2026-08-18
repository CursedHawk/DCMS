import { Download, FileBox } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/button';
import { base64ToBytes, dataUriFor, isPreviewableImage } from './binary';
import { downloadFile } from './fileio';
import { useVfs } from './vfs';

// Shown in place of Monaco when the active file is a binary asset (image, font,
// …). Images render inline; anything else shows a placeholder. Both offer a
// download so the raw bytes are recoverable from the base64 map entry.

export function BinaryFileView() {
  const { t } = useTranslation();
  const activePath = useVfs((s) => s.activePath);
  const content = useVfs((s) => (s.activePath ? s.files[s.activePath] : undefined));

  const sizeKb = useMemo(
    () => (content != null ? Math.max(1, Math.round(base64ToBytes(content).length / 1024)) : 0),
    [content],
  );

  if (!activePath || content == null) return null;
  const name = activePath.slice(activePath.lastIndexOf('/') + 1);

  return (
    <div className="flex h-full w-full flex-col items-center justify-center gap-4 bg-background p-6">
      {isPreviewableImage(activePath) ? (
        <img
          src={dataUriFor(activePath, content)}
          alt={name}
          className="max-h-[70%] max-w-full rounded border bg-[repeating-conic-gradient(#0000000d_0deg_90deg,transparent_90deg_180deg)] bg-[length:16px_16px] object-contain shadow-sm"
        />
      ) : (
        <FileBox className="h-16 w-16 text-muted-foreground" />
      )}
      <div className="text-center">
        <div className="text-sm font-medium">{name}</div>
        <div className="text-xs text-muted-foreground">
          {t('ide.binaryFile')} · {sizeKb} KB
        </div>
      </div>
      <Button variant="outline" size="sm" onClick={() => downloadFile(activePath, content)}>
        <Download className="h-4 w-4" /> {t('ide.download')}
      </Button>
    </div>
  );
}
