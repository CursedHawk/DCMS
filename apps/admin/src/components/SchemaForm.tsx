import Form from '@rjsf/core';
import type { RJSFSchema } from '@rjsf/utils';
import validator from '@rjsf/validator-ajv8';

/**
 * JSON-Schema-driven form (react-jsonschema-form). Controlled via formData/onChange;
 * the default submit button is suppressed — callers provide their own save action.
 * Styled through the `.dcms-rjsf` scope in index.css.
 */
export function SchemaForm({
  schema,
  formData,
  onChange,
}: {
  schema: RJSFSchema;
  formData: unknown;
  onChange: (data: unknown) => void;
}) {
  return (
    <div className="dcms-rjsf">
      <Form
        schema={schema}
        formData={formData}
        validator={validator}
        onChange={(e) => onChange(e.formData)}
        liveValidate={false}
        showErrorList={false}
      >
        <></>
      </Form>
    </div>
  );
}
