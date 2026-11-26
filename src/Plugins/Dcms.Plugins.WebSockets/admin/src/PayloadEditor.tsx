import { useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import {
  Button,
  Checkbox,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  TagsInput,
} from '@dcms/ui';
import { usePluginHost, usePluginT } from '@dcms/plugin-ui';
import { FORMATS, KINDS, MAX_DEPTH, kindOf, toTypeScript, withKind, type Literal, type PayloadSchema } from './schema';

/**
 * A channel's payload type: fields to click together, or the JSON Schema itself (checked by the
 * editor against the subset the server accepts), with the TypeScript the clients will see.
 */
export function PayloadEditor({
  uri,
  value,
  onChange,
  metaSchema,
}: {
  uri: string;
  value: PayloadSchema | undefined;
  onChange: (schema: PayloadSchema | undefined) => void;
  metaSchema?: Record<string, unknown>;
}) {
  const { t } = usePluginT();
  const { components } = usePluginHost();
  const [text, setText] = useState(() => JSON.stringify(value ?? {}, null, 2));
  const [invalid, setInvalid] = useState(false);
  const typed = value !== undefined;

  const setSchema = (schema: PayloadSchema | undefined) => {
    onChange(schema);
    setText(JSON.stringify(schema ?? {}, null, 2));
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <Switch
          id={`${uri}-typed`}
          checked={typed}
          onCheckedChange={(on) => setSchema(on ? { type: 'object', properties: {} } : undefined)}
        />
        <Label htmlFor={`${uri}-typed`}>{t('websockets.typedPayload')}</Label>
      </div>
      {!typed ? (
        <p className="text-xs text-muted-foreground">{t('websockets.anyJson')}</p>
      ) : (
        <Tabs defaultValue="fields">
          <TabsList>
            <TabsTrigger value="fields">{t('websockets.fields')}</TabsTrigger>
            <TabsTrigger value="json">JSON Schema</TabsTrigger>
            <TabsTrigger value="ts">TypeScript</TabsTrigger>
          </TabsList>
          <TabsContent value="fields">
            <FieldEditor schema={value} onChange={setSchema} depth={0} />
          </TabsContent>
          <TabsContent value="json" className="space-y-1">
            <components.CodeEditor
              uri={`${uri}.schema.json`}
              language="json"
              value={text}
              height={260}
              jsonSchema={metaSchema}
              ariaLabel="JSON Schema"
              onChange={(next) => {
                setText(next);
                try {
                  onChange(JSON.parse(next) as PayloadSchema);
                  setInvalid(false);
                } catch {
                  setInvalid(true);
                }
              }}
            />
            {invalid && <p className="text-xs text-destructive">{t('websockets.invalidJson')}</p>}
          </TabsContent>
          <TabsContent value="ts">
            <pre className="overflow-x-auto rounded-md border bg-muted/40 p-3 text-xs">{toTypeScript(value)}</pre>
          </TabsContent>
        </Tabs>
      )}
    </div>
  );
}

/** One schema node: its kind, and what that kind needs (fields, an item, values, a format). */
function FieldEditor({ schema, onChange, depth }: { schema: PayloadSchema; onChange: (s: PayloadSchema) => void; depth: number }) {
  const { t } = usePluginT();
  const kind = kindOf(schema);

  if (kind === 'object') {
    const properties = Object.entries(schema.properties ?? {});
    const required = new Set(schema.required ?? []);
    const write = (entries: [string, PayloadSchema][], req: Set<string>) =>
      onChange({
        ...schema,
        properties: Object.fromEntries(entries),
        required: [...req].filter((r) => entries.some(([n]) => n === r)),
      });
    return (
      <div className="space-y-2">
        {properties.map(([name, field], index) => (
          <div key={index} className="space-y-2 rounded-md border p-2">
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_9rem_auto_auto]">
              <Input
                aria-label={t('websockets.fieldName')}
                value={name}
                onChange={(e) => {
                  const next = properties.slice();
                  next[index] = [e.target.value, field];
                  const req = new Set([...required].map((r) => (r === name ? e.target.value : r)));
                  write(next, req);
                }}
              />
              <KindSelect
                value={kindOf(field)}
                depth={depth}
                onChange={(k) => {
                  const next = properties.slice();
                  next[index] = [name, withKind(field, k)];
                  write(next, required);
                }}
              />
              <label className="flex items-center gap-1.5 text-sm">
                <Checkbox
                  checked={required.has(name)}
                  onCheckedChange={(on) => {
                    const req = new Set(required);
                    if (on) req.add(name);
                    else req.delete(name);
                    write(properties, req);
                  }}
                />
                {t('websockets.required')}
              </label>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                aria-label={t('websockets.removeField')}
                onClick={() => write(properties.filter((_, i) => i !== index), required)}
              >
                <Trash2 className="size-4" />
              </Button>
            </div>
            <Input
              aria-label={t('websockets.fieldDescription')}
              placeholder={t('websockets.fieldDescription')}
              value={field.description ?? ''}
              onChange={(e) => {
                const next = properties.slice();
                next[index] = [name, { ...field, description: e.target.value || undefined }];
                write(next, required);
              }}
            />
            {kindOf(field) !== 'object' || depth < MAX_DEPTH ? (
              <div className="border-l pl-3">
                <FieldDetail
                  schema={field}
                  depth={depth + 1}
                  onChange={(s) => {
                    const next = properties.slice();
                    next[index] = [name, s];
                    write(next, required);
                  }}
                />
              </div>
            ) : null}
          </div>
        ))}
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => write([...properties, [uniqueName(properties.map(([n]) => n)), { type: 'string' }]], required)}
        >
          <Plus className="size-4" />
          {t('websockets.addField')}
        </Button>
      </div>
    );
  }
  return <FieldDetail schema={schema} onChange={onChange} depth={depth} />;
}

/** What a non-object kind needs beyond its type; an object's fields for an object. */
function FieldDetail({ schema, onChange, depth }: { schema: PayloadSchema; onChange: (s: PayloadSchema) => void; depth: number }) {
  const { t } = usePluginT();
  switch (kindOf(schema)) {
    case 'object':
      return <FieldEditor schema={schema} onChange={onChange} depth={depth} />;
    case 'array': {
      const items = schema.items ?? { type: 'string' };
      return (
        <div className="space-y-2">
          <div className="flex items-center gap-2 text-sm">
            <span className="text-muted-foreground">{t('websockets.listOf')}</span>
            <KindSelect value={kindOf(items)} depth={depth} onChange={(k) => onChange({ ...schema, items: withKind(items, k) })} />
          </div>
          {kindOf(items) === 'object' || kindOf(items) === 'choice' ? (
            <FieldDetail schema={items} depth={depth + 1} onChange={(s) => onChange({ ...schema, items: s })} />
          ) : null}
        </div>
      );
    }
    case 'choice':
      return (
        <TagsInput
          value={(schema.enum ?? []).map(String)}
          placeholder={t('websockets.choices')}
          onChange={(values) => onChange({ ...schema, enum: values.map(parseLiteral) })}
        />
      );
    case 'string':
      return (
        <Select
          value={schema.format ?? 'text'}
          onValueChange={(f) => onChange({ ...schema, format: f === 'text' ? undefined : (f as PayloadSchema['format']) })}
        >
          <SelectTrigger className="w-40" aria-label={t('websockets.format')}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="text">{t('websockets.formats.text')}</SelectItem>
            {FORMATS.map((f) => (
              <SelectItem key={f} value={f}>
                {t(`websockets.formats.${f}`)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      );
    default:
      return null;
  }
}

function KindSelect({ value, onChange, depth }: { value: string; onChange: (kind: (typeof KINDS)[number]) => void; depth: number }) {
  const { t } = usePluginT();
  return (
    <Select value={value} onValueChange={(k) => onChange(k as (typeof KINDS)[number])}>
      <SelectTrigger aria-label={t('websockets.kind')}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {KINDS.filter((k) => depth < MAX_DEPTH || (k !== 'object' && k !== 'array')).map((k) => (
          <SelectItem key={k} value={k}>
            {t(`websockets.kinds.${k}`)}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

function uniqueName(taken: string[]): string {
  for (let i = 1; ; i++) {
    const name = i === 1 ? 'field' : `field${i}`;
    if (!taken.includes(name)) return name;
  }
}

/** A choice typed as what it looks like: 3 is a number, true a boolean, anything else text. */
function parseLiteral(value: string): Literal {
  if (value === 'true' || value === 'false') return value === 'true';
  return value.trim() !== '' && !Number.isNaN(Number(value)) ? Number(value) : value;
}
