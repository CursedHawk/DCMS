import type { ComponentNode } from '@dcms/editor-core';
import { findRegistration } from '@dcms/site-components';
import { ImageIcon } from 'lucide-react';
import { AuthedImage } from '../../components/AuthedImage';

/**
 * In-editor visual renderer. Mirrors the published output closely enough to edit
 * against; data-bound/plugin components render a labelled placeholder (their real
 * data is hydrated at runtime on the published site).
 */
export function NodeRenderer({ node }: { node: ComponentNode }) {
  const p = node.props as Record<string, string>;
  const bound = (node.bindings?.length ?? 0) > 0;
  const reg = findRegistration(node.type);

  if (bound || reg?.requiredPluginId) {
    return (
      <div className="flex h-full w-full flex-col items-center justify-center gap-1 rounded-md border border-dashed bg-muted/40 p-3 text-center">
        <span className="text-xs font-semibold text-foreground">{reg?.displayName ?? node.type}</span>
        <span className="text-[11px] text-muted-foreground">
          {bound ? `↪ ${node.bindings?.[0].source.instanceSlug}` : 'bind a data source'}
        </span>
      </div>
    );
  }

  switch (node.type) {
    case 'Hero':
      return (
        <div className="flex h-full w-full flex-col items-center justify-center gap-2 rounded-md bg-gradient-to-br from-primary/15 to-accent/40 p-6 text-center">
          <h1 className="text-3xl font-bold">{p.title}</h1>
          {p.subtitle ? <p className="text-muted-foreground">{p.subtitle}</p> : null}
        </div>
      );
    case 'Heading':
      return <Heading level={p.variant} text={p.text} />;
    case 'Text':
      return <p className="h-full w-full overflow-hidden text-sm leading-relaxed">{p.text}</p>;
    case 'Button':
      return (
        <div className="flex h-full w-full items-center">
          <span className="inline-flex items-center rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground">
            {p.label}
          </span>
        </div>
      );
    case 'Image': {
      if (!p.src) {
        return (
          <div className="flex h-full w-full items-center justify-center rounded-md bg-muted text-muted-foreground">
            <ImageIcon className="h-6 w-6" />
          </div>
        );
      }
      return isAssetId(p.src) ? (
        <AuthedImage id={p.src} className="h-full w-full rounded-md object-cover" />
      ) : (
        <img src={p.src} alt={p.alt ?? ''} className="h-full w-full rounded-md object-cover" />
      );
    }
    case 'Section':
    case 'Stack':
    case 'Grid':
      return (
        <div className="h-full w-full rounded-md border border-dashed border-border/70 bg-card/40 p-2 text-[11px] text-muted-foreground">
          {reg?.displayName ?? node.type}
        </div>
      );
    default:
      return (
        <div className="flex h-full w-full items-center justify-center rounded-md border bg-card text-xs text-muted-foreground">
          {reg?.displayName ?? node.type}
        </div>
      );
  }
}

function Heading({ level, text }: { level?: string; text?: string }) {
  const cls =
    level === 'h1'
      ? 'text-3xl font-bold'
      : level === 'h3'
        ? 'text-xl font-semibold'
        : level === 'h4'
          ? 'text-lg font-semibold'
          : 'text-2xl font-bold';
  return <div className={`${cls} truncate`}>{text}</div>;
}

/** Heuristic: a GUID-ish string is a media asset id, otherwise treat as a URL. */
function isAssetId(v: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-/i.test(v);
}
