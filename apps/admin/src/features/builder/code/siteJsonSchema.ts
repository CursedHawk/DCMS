import { SITE_JSON, siteManifestSchema } from '@dcms/gjs-schema';
import type * as Monaco from 'monaco-editor';
import { z } from 'zod';

/**
 * JSON Schema validation and completion for `site.json`, inside Monaco's own
 * json worker.
 *
 * The schema is derived from the zod schema the builder itself parses with, so
 * the editor cannot accept a manifest the builder would then reject — the two
 * are the same definition, not two descriptions of one.
 */

const SITE_JSON_URI = `file:///${SITE_JSON}`;

export function registerSiteJsonSchema(monaco: typeof Monaco): void {
  const schema = z.toJSONSchema(siteManifestSchema, { io: 'input', target: 'draft-7' });

  monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
    validate: true,
    enableSchemaRequest: false,
    schemas: [
      {
        uri: 'https://dcms.local/schemas/site.json',
        fileMatch: [SITE_JSON_URI, SITE_JSON],
        schema: schema as object,
      },
    ],
  });
}
