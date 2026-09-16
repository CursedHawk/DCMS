/**
 * Long-form text in the format its content type declares.
 *
 * Rich text is HTML written by the tenant's own editors in DCMS, and is rendered as such — the same
 * trust a CMS page always places in its authors. Never pass text a *visitor* supplied through here.
 * Markdown and plain text are shown as paragraphs; add a Markdown renderer to package.json if the
 * site needs headings and lists from Markdown fields.
 */
export function RichText({ value, format }: { value: string; format?: 'richtext' | 'markdown' | 'text' }) {
  if (format === 'richtext') {
    return <div className="prose" dangerouslySetInnerHTML={{ __html: value }} />;
  }
  return (
    <div className="prose">
      {value.split(/\n{2,}/).map((paragraph, i) => (
        <p key={i}>{paragraph}</p>
      ))}
    </div>
  );
}
