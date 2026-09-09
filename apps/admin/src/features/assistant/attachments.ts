/**
 * Files the operator has handed the assistant in this conversation.
 *
 * <p>A model cannot produce bytes, so "upload this" only means anything if the bytes are
 * already here. The composer's paperclip puts them in this pool; the model is told their names
 * and sizes in the message text and calls `upload_media` with a name; the tool posts the real
 * multipart request the media library uses.</p>
 *
 * <p>In memory only, and per session. These are the operator's own files on their own machine —
 * putting them anywhere else before they have said where they belong is a decision that is not
 * ours to make, and a reload is a perfectly clear way to say "never mind".</p>
 */
export interface Attachment {
  /** The file name, which is also how the model refers to it. */
  name: string;
  size: number;
  type: string;
  file: File;
  /** Set once uploaded, so a second call cannot upload the same bytes twice. */
  assetId?: string;
}

export function describeAttachments(attachments: readonly Attachment[]): string {
  if (attachments.length === 0) return '';
  const lines = attachments.map(
    (a) => `- ${a.name} (${a.type || 'unknown type'}, ${Math.round(a.size / 1024)} KB)`,
  );
  return `\n\nFiles attached to this message, uploadable with upload_media:\n${lines.join('\n')}`;
}

/** Two files of the same name in one pool would make `upload_media("x.jpg")` ambiguous. */
export function addAttachments(
  current: readonly Attachment[],
  files: readonly File[],
): Attachment[] {
  const next = [...current];
  for (const file of files) {
    const at = next.findIndex((a) => a.name === file.name);
    const entry: Attachment = { name: file.name, size: file.size, type: file.type, file };
    if (at >= 0) next[at] = entry;
    else next.push(entry);
  }
  return next;
}
