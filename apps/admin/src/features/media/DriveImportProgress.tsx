import { useQueryClient } from '@tanstack/react-query';
import { AlertCircle, CheckCircle2 } from 'lucide-react';
import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Progress, jobFraction } from '@dcms/ui';
import { useJob } from '../jobs/api';
import { jobText } from '../jobs/text';

/**
 * A Drive import the server is running (ADR 0024): one row per picked file, live from the job
 * ledger. The page can be left at any point; the import does not depend on it, and the jobs ring
 * keeps following it. While the uploader is open, each file that lands is handed to
 * `onUploaded`, so a picker can select it as soon as it exists.
 */
export function DriveImportProgress({
  jobId,
  onUploaded,
  onSettled,
}: {
  jobId: string;
  onUploaded?: (id: string, fileName: string) => void;
  /** The import finished; the caller can drop this panel. */
  onSettled: (anyFailed: boolean) => void;
}) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const { data } = useJob(jobId);
  const announced = useRef(new Set<string>());

  const files = (data?.children ?? []).filter((c) => c.kind === 'media.drive-file');
  const finished = data?.job && ['Succeeded', 'Failed', 'Canceled'].includes(data.job.status);

  useEffect(() => {
    for (const f of files) {
      const assetId = (f.result as { assetId?: string } | null)?.assetId;
      if (f.status === 'Succeeded' && assetId && !announced.current.has(f.id)) {
        announced.current.add(f.id);
        onUploaded?.(assetId, String(f.params.name ?? ''));
        void qc.invalidateQueries({ queryKey: ['media'] });
      }
    }
  });

  useEffect(() => {
    if (!finished) return;
    void Promise.all([
      qc.invalidateQueries({ queryKey: ['media'] }),
      qc.invalidateQueries({ queryKey: ['media-usage'] }),
    ]);
    onSettled(files.some((f) => f.status === 'Failed'));
    // Settling is a one-way edge; the file list is read once it is reached.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [finished]);

  if (files.length === 0) return null;

  return (
    <ul className="divide-y rounded-lg border" data-testid="drive-import">
      {files.map((f) => {
        const text = jobText(t, f);
        return (
          <li key={f.id} className="space-y-1.5 p-2.5">
            <div className="flex items-center gap-2 text-xs">
              {f.status === 'Succeeded' ? (
                <CheckCircle2 className="h-3.5 w-3.5 shrink-0 text-success-foreground" />
              ) : f.status === 'Failed' ? (
                <AlertCircle className="h-3.5 w-3.5 shrink-0 text-destructive" />
              ) : null}
              <span className="min-w-0 flex-1 truncate font-medium" title={text.title}>
                {text.title}
              </span>
              <span className="shrink-0 text-muted-foreground">{t(`jobs.status.${f.status}`)}</span>
            </div>
            {f.status === 'Failed' ? (
              <p className="text-xs text-destructive">{text.detail}</p>
            ) : (
              <Progress value={jobFraction(f) ?? 0} label={text.title} />
            )}
          </li>
        );
      })}
    </ul>
  );
}
