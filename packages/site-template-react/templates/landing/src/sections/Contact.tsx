import { useState, type FormEvent } from 'react';
import { ApiError, forms, type FormInfo } from '../api';
import { FormFields } from '../components/FormFields';
import { api } from '../lib/api';
import { site } from '../site';

/**
 * The tenant's first visitor form, rendered from its configured fields and submitted to DCMS,
 * where submissions appear under Forms. With no form configured, an email link instead.
 */
export function Contact() {
  const form = forms[0];

  return (
    <section id="contact" className="band" aria-labelledby="contact-title">
      <div className="container narrow">
        <h2 id="contact-title">{form?.title ?? site.cta}</h2>
        {form ? (
          <ContactForm form={form} />
        ) : (
          <p className="lead">
            Write to us at <a href={`mailto:${site.email}`}>{site.email}</a>.
          </p>
        )}
      </div>
    </section>
  );
}

type State = { kind: 'idle' } | { kind: 'sending' } | { kind: 'sent'; message: string } | { kind: 'failed'; message: string };

function ContactForm({ form }: { form: FormInfo }) {
  const [state, setState] = useState<State>({ kind: 'idle' });

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const element = event.currentTarget;
    const data = new FormData(element);
    const values = Object.fromEntries(
      form.fields.map((f) => [f.name, f.type === 'checkbox' ? data.has(f.name) : f.type === 'number' ? Number(data.get(f.name)) : String(data.get(f.name) ?? '')]),
    );

    setState({ kind: 'sending' });
    try {
      const result = await api.submitForm(form.instance, form.name, values);
      element.reset();
      setState({ kind: 'sent', message: result.message ?? 'Thank you — we will be in touch.' });
    } catch (error) {
      // The server names the field that failed validation; that sentence is the useful part.
      const detail = error instanceof ApiError && typeof (error.body as { error?: unknown })?.error === 'string'
        ? (error.body as { error: string }).error
        : 'Please try again in a moment.';
      setState({ kind: 'failed', message: `Your message was not sent. ${detail}` });
    }
  }

  if (state.kind === 'sent') {
    return (
      <p className="lead" role="status">
        {state.message}
      </p>
    );
  }

  return (
    <form className="form" onSubmit={submit}>
      <FormFields fields={form.fields} />
      {state.kind === 'failed' && (
        <p className="status status--error" role="alert">
          {state.message}
        </p>
      )}
      <button className="button" type="submit" disabled={state.kind === 'sending'}>
        {state.kind === 'sending' ? 'Sending…' : 'Send'}
      </button>
    </form>
  );
}
