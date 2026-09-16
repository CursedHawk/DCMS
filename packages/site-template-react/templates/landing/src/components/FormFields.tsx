import type { FormFieldInfo } from '../api';

/** One input per field, of the type the tenant configured for it. */
export function FormFields({ fields }: { fields: readonly FormFieldInfo[] }) {
  return (
    <>
      {fields.map((field) => {
        const id = `field-${field.name}`;
        if (field.type === 'checkbox') {
          return (
            <div key={field.name} className="field field--checkbox">
              <input id={id} name={field.name} type="checkbox" required={field.required} />
              <label htmlFor={id}>{field.label}</label>
            </div>
          );
        }
        return (
          <div key={field.name} className="field">
            <label htmlFor={id}>
              {field.label}
              {!field.required && <span className="meta"> (optional)</span>}
            </label>
            {field.type === 'textarea' ? (
              <textarea id={id} name={field.name} required={field.required} maxLength={field.maxLength} />
            ) : (
              <input
                id={id}
                name={field.name}
                type={field.type}
                required={field.required}
                maxLength={field.maxLength}
                autoComplete={field.type === 'email' ? 'email' : undefined}
              />
            )}
          </div>
        );
      })}
    </>
  );
}
