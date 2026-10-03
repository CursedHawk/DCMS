import { PreviewPane } from '../ide/PreviewPane';
import { usePreview } from '../ide/preview/usePreview';

/**
 * The site with its developer components running (P7, ADR 0020): the whole draft bundled in the
 * browser and run in the Mode B preview's opaque-origin sandbox, whose API calls the admin
 * replays only inside this site's preview subtree. Developer code never runs anywhere else in
 * the admin — the canvas and the in-admin preview show it as a placeholder.
 */
export function SandboxPreview({ siteId }: { siteId: string }) {
  const preview = usePreview(true, siteId);
  return <PreviewPane preview={preview} siteId={siteId} />;
}
