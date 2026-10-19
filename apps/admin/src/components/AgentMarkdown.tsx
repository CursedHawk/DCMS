import Markdown from 'markdown-to-jsx';
import { cn } from '@dcms/ui';

// No typography plugin here, so the elements Markdown produces are styled from the wrapper.
const styles = cn(
  'space-y-2 break-words',
  '[&_:is(h1,h2,h3,h4,h5,h6)]:mt-3 [&_:is(h1,h2,h3,h4,h5,h6)]:font-semibold [&_h1]:text-base [&_h2]:text-base',
  '[&_ul]:list-disc [&_ol]:list-decimal [&_:is(ul,ol)]:space-y-0.5 [&_:is(ul,ol)]:pl-5',
  '[&_a]:text-primary [&_a]:underline [&_a]:underline-offset-2',
  '[&_code]:rounded [&_code]:bg-muted [&_code]:px-1 [&_code]:font-mono [&_code]:text-[0.85em]',
  '[&_pre]:overflow-x-auto [&_pre]:rounded-md [&_pre]:bg-muted [&_pre]:p-2.5 [&_pre]:text-xs [&_pre_code]:bg-transparent [&_pre_code]:p-0',
  '[&_blockquote]:border-l-2 [&_blockquote]:pl-2.5 [&_blockquote]:text-muted-foreground',
  '[&_table]:block [&_table]:overflow-x-auto [&_table]:text-xs [&_:is(th,td)]:border [&_:is(th,td)]:px-2 [&_:is(th,td)]:py-1 [&_th]:text-left [&_th]:font-medium',
  '[&_hr]:border-border',
);

/**
 * An AI agent's answer, read as the Markdown it is written in. Raw HTML in it stays text — what
 * a model writes is never markup — and links open in a new tab, so the conversation stays put.
 */
export function AgentMarkdown({ text, className }: { text: string; className?: string }) {
  return (
    <Markdown
      className={cn(styles, className)}
      options={{
        forceBlock: true,
        forceWrapper: true,
        disableParsingRawHTML: true,
        overrides: { a: { props: { target: '_blank', rel: 'noopener noreferrer' } } },
      }}
    >
      {text}
    </Markdown>
  );
}
