import { Fragment, type ReactNode } from 'react';

/**
 * The little Markdown the help articles use — paragraphs, `##` headings, `-` and `1.` lists,
 * **bold**, *italic* and `code` — drawn as React elements, so nothing in an article is ever HTML.
 */
export function Markdown({ text }: { text: string }) {
  return <>{text.split(/\n{2,}/).map((block, i) => <Block key={i} block={block.trim()} />)}</>;
}

function Block({ block }: { block: string }) {
  const lines = block.split('\n');
  if (block.startsWith('## ')) return <h3 className="mb-1 mt-4 text-sm font-semibold">{inline(block.slice(3))}</h3>;
  if (lines.every((l) => /^- /.test(l))) return <ul className="mb-3 list-disc space-y-1 pl-5">{lines.map((l, i) => <li key={i}>{inline(l.slice(2))}</li>)}</ul>;
  if (lines.every((l) => /^\d+\. /.test(l))) return <ol className="mb-3 list-decimal space-y-1 pl-5">{lines.map((l, i) => <li key={i}>{inline(l.replace(/^\d+\. /, ''))}</li>)}</ol>;
  return <p className="mb-3">{inline(lines.join(' '))}</p>;
}

function inline(text: string): ReactNode {
  return text.split(/(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`)/).map((part, i) => {
    if (part.startsWith('**') && part.endsWith('**') && part.length > 4) return <strong key={i}>{part.slice(2, -2)}</strong>;
    if (part.startsWith('`') && part.endsWith('`') && part.length > 2) return <code key={i} className="rounded bg-muted px-1 text-xs">{part.slice(1, -1)}</code>;
    if (part.startsWith('*') && part.endsWith('*') && part.length > 2) return <em key={i}>{part.slice(1, -1)}</em>;
    return <Fragment key={i}>{part}</Fragment>;
  });
}
