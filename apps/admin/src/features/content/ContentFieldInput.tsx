import { Input, Textarea } from '../../components/ui/input';
import { Label } from '../../components/ui/label';
import { Switch } from '../../components/ui/switch';
import type { MediaCategory } from '../media/api';
import { MediaMultiPicker } from '../media/MediaMultiPicker';
import { MediaPicker } from '../media/MediaPicker';
import type { ContentFieldDef } from '../plugins/api';
import { PerformerPicker } from './PerformerPicker';

/** Coerce a stored value (array, JSON string, or empty) into a list of ids. */
function toIdList(value: unknown): string[] {
  if (Array.isArray(value)) return value.filter((v): v is string => typeof v === 'string');
  if (typeof value === 'string' && value.trim().startsWith('[')) {
    try {
      const parsed = JSON.parse(value);
      return Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === 'string') : [];
    } catch {
      return [];
    }
  }
  return [];
}

/** Renders the right control for a plugin content field type, bound to data[name]. */
export function ContentFieldInput({
  field,
  value,
  onChange,
  instanceConfig,
}: {
  field: ContentFieldDef;
  value: unknown;
  onChange: (v: unknown) => void;
  /**
   * Config of the instance the item belongs to — where a field referencing
   * another plugin names the instance it points at.
   */
  instanceConfig: Record<string, unknown>;
}) {
  const label = (
    <Label className="flex items-center gap-1">
      {field.name}
      {field.required ? <span className="text-destructive">*</span> : null}
    </Label>
  );

  const control = (() => {
    switch (field.type) {
      case 'RichText':
      case 'Markdown':
        return (
          <Textarea
            rows={6}
            value={(value as string) ?? ''}
            onChange={(e) => onChange(e.target.value)}
          />
        );
      case 'Json':
        // A JSON field that references media (e.g. a gallery's images) is edited
        // as an ordered list of assets picked from the library, not raw JSON.
        if (field.reference?.mediaCategory) {
          return (
            <MediaMultiPicker
              value={toIdList(value)}
              onChange={(ids) => onChange(ids)}
              category={field.reference.mediaCategory as MediaCategory}
            />
          );
        }
        // A JSON field referencing roster members is the gig line-up. The roster
        // is a separate instance, named by this one's config; without it the
        // picker still takes performers by name.
        if (field.reference?.targetPluginId === 'roster') {
          return (
            <PerformerPicker
              rosterSlug={
                typeof instanceConfig.rosterSlug === 'string' ? instanceConfig.rosterSlug : null
              }
              contentType={field.reference.contentType ?? 'member'}
              value={value}
              onChange={(list) => onChange(list)}
            />
          );
        }
        return (
          <Textarea
            rows={4}
            className="font-mono text-xs"
            value={typeof value === 'string' ? value : JSON.stringify(value ?? {}, null, 2)}
            onChange={(e) => onChange(e.target.value)}
          />
        );
      case 'Number':
        return (
          <Input
            type="number"
            value={(value as number | undefined) ?? ''}
            onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))}
          />
        );
      case 'Boolean':
        return <Switch checked={!!value} onCheckedChange={(v) => onChange(v)} />;
      case 'DateTime':
        return (
          <Input
            type="datetime-local"
            value={(value as string) ?? ''}
            onChange={(e) => onChange(e.target.value)}
          />
        );
      case 'MediaRef':
        return (
          <MediaPicker
            value={value as string | undefined}
            onChange={(id) => onChange(id)}
            category={(field.reference?.mediaCategory as MediaCategory) ?? undefined}
          />
        );
      case 'Tags':
        return (
          <Input
            value={Array.isArray(value) ? (value as string[]).join(', ') : ((value as string) ?? '')}
            placeholder="tag1, tag2"
            onChange={(e) =>
              onChange(
                e.target.value
                  .split(',')
                  .map((s) => s.trim())
                  .filter(Boolean),
              )
            }
          />
        );
      case 'ContentRef':
        return (
          <Input
            value={(value as string) ?? ''}
            placeholder={field.reference?.contentType ?? 'reference id / slug'}
            onChange={(e) => onChange(e.target.value)}
          />
        );
      default:
        return <Input value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value)} />;
    }
  })();

  return (
    <div className="space-y-1.5">
      {label}
      {control}
      {field.description ? (
        <p className="text-xs text-muted-foreground">{field.description}</p>
      ) : null}
    </div>
  );
}
