import type { RegistryWidgetsType, WidgetProps } from '@rjsf/utils';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Textarea } from '@dcms/ui';
import { MediaPicker } from '../media/MediaPicker';
import { MetaConnectionWidget } from './MetaConnectionWidget';
import { ContractBindingWidget } from './ContractBindingWidget';

/**
 * RJSF widget for schema fields declared with `"format": "media"` (e.g. the
 * Branding plugin's logo/favicon). Renders the shared media picker so an admin can
 * upload a new image — processed through the standard media pipeline and stored in
 * tenant media — or pick an existing asset; the field stores the media asset id.
 */
function MediaFieldWidget({ value, onChange }: WidgetProps) {
  return (
    <MediaPicker
      value={(value as string) || undefined}
      onChange={(id) => onChange(id ?? undefined)}
      category="Image"
    />
  );
}

/**
 * `"format": "json"` -- a string holding a JSON document (a plugin's stored document in the
 * data view). Kept as text while typing, so a half-written document is not lost; says so when
 * it does not parse, and the server refuses it on save.
 */
function JsonWidget({ id, value, onChange, disabled, readonly }: WidgetProps) {
  const { t } = useTranslation();
  const text = (value as string) ?? '';
  const [invalid, setInvalid] = useState(false);
  return (
    <div className="space-y-1">
      <Textarea
        id={id}
        value={text}
        disabled={disabled || readonly}
        spellCheck={false}
        rows={Math.min(24, Math.max(6, text.split('\n').length + 1))}
        className="font-mono text-xs"
        onChange={(e) => {
          onChange(e.target.value);
          try {
            JSON.parse(e.target.value);
            setInvalid(false);
          } catch {
            setInvalid(true);
          }
        }}
      />
      {invalid ? <p className="text-xs text-destructive">{t('pluginPage.data.invalidJson')}</p> : null}
    </div>
  );
}

/** The custom widgets every plugin-described form (config, data rows, action input) can use. */
export const configWidgets: RegistryWidgetsType = {
  media: MediaFieldWidget,
  // `"format": "meta-connection"` -- the Instagram/Facebook plugins' connectionId.
  // A connection is the product of an OAuth round trip, so it cannot be typed in.
  'meta-connection': MetaConnectionWidget,
  // `"x-dcms-contract-binding"` -- which instance of another plugin this one uses.
  'contract-binding': ContractBindingWidget,
  json: JsonWidget,
};
