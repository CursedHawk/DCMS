import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Form components.
 *
 * A form posts to the Forms plugin's delivery endpoint, so submissions land in
 * the tenant's Forms admin (and trigger its notification emails) without the
 * author wiring anything up. The instance and form slug are traits rather than a
 * hard-coded action so the same markup works across environments.
 *
 * Fields are ordinary inputs: no controlled-component layer, no client script.
 */
export const formSpecs: DcmsComponentSpec[] = [
  {
    type: 'Form',
    label: 'Form',
    category: 'form',
    tag: 'form',
    icon: 'form',
    acceptsChildren: true,
    order: 0,
    requiredPluginId: 'forms',
    docs: 'Collects submissions into the Forms plugin.',
    traits: [
      {
        name: 'data-instance',
        label: 'Forms instance',
        kind: 'contentRef',
        required: true,
        accepts: { pluginId: 'forms' },
        description: 'Which Forms plugin instance receives the submissions.',
      },
      { name: 'data-form', label: 'Form slug', kind: 'text', required: true },
      { name: 'data-success', label: 'Thank-you message', kind: 'text', default: 'Thank you.' },
      { name: 'data-redirect', label: 'Redirect to', kind: 'url' },
    ],
    snippet: `<form class="dcms-form" method="post" data-instance="" data-form="" data-success="Thank you.">
  <label class="dcms-field"><span>Name</span><input name="name" type="text" required /></label>
  <label class="dcms-field"><span>Email</span><input name="email" type="email" required /></label>
  <label class="dcms-field"><span>Message</span><textarea name="message" rows="5"></textarea></label>
  <button class="dcms-button" type="submit">Send</button>
</form>`,
  },
  {
    type: 'FormField',
    label: 'Text field',
    category: 'form',
    tag: 'label',
    icon: 'input',
    identityClass: 'dcms-field',
    acceptsChildren: true,
    order: 1,
    docs: 'A labelled single-line input.',
    traits: [],
    snippet: `<label class="dcms-field"><span>Label</span><input name="field" type="text" /></label>`,
  },
  {
    type: 'FormTextarea',
    label: 'Long text',
    category: 'form',
    tag: 'label',
    icon: 'input',
    identityClass: 'dcms-field-textarea',
    acceptsChildren: true,
    order: 2,
    docs: 'A labelled multi-line input.',
    traits: [],
    snippet: `<label class="dcms-field dcms-field-textarea"><span>Message</span><textarea name="message" rows="5"></textarea></label>`,
  },
  {
    type: 'FormSelect',
    label: 'Dropdown',
    category: 'form',
    tag: 'label',
    icon: 'select',
    identityClass: 'dcms-field-select',
    acceptsChildren: true,
    order: 3,
    docs: 'A labelled dropdown.',
    traits: [],
    snippet: `<label class="dcms-field dcms-field-select"><span>Choose</span><select name="choice"><option value="a">First</option><option value="b">Second</option></select></label>`,
  },
  {
    type: 'FormCheckbox',
    label: 'Checkbox',
    category: 'form',
    tag: 'label',
    icon: 'checkbox',
    identityClass: 'dcms-field-checkbox',
    acceptsChildren: true,
    order: 4,
    docs: 'A single opt-in checkbox — consent, terms, newsletter.',
    traits: [],
    snippet: `<label class="dcms-field dcms-field-checkbox"><input name="consent" type="checkbox" value="yes" /><span>I agree to the terms</span></label>`,
  },
  {
    type: 'FormRadioGroup',
    label: 'Radio group',
    category: 'form',
    tag: 'fieldset',
    icon: 'checkbox',
    acceptsChildren: true,
    order: 5,
    docs: 'One choice from a few.',
    traits: [],
    snippet: `<fieldset class="dcms-form-radio-group">
  <legend>Pick one</legend>
  <label><input type="radio" name="choice" value="a" /> First</label>
  <label><input type="radio" name="choice" value="b" /> Second</label>
</fieldset>`,
  },
  {
    type: 'FormFile',
    label: 'File upload',
    category: 'form',
    tag: 'label',
    icon: 'input',
    identityClass: 'dcms-field-file',
    acceptsChildren: true,
    order: 6,
    docs: 'An attachment field.',
    traits: [],
    snippet: `<label class="dcms-field dcms-field-file"><span>Attachment</span><input name="file" type="file" /></label>`,
  },
  {
    type: 'FormSubmit',
    label: 'Submit button',
    category: 'form',
    tag: 'button',
    icon: 'button',
    classes: ['dcms-button'],
    acceptsChildren: true,
    order: 7,
    docs: 'Sends the form.',
    traits: [],
    snippet: `<button class="dcms-form-submit dcms-button" type="submit">Send</button>`,
  },
  {
    type: 'NewsletterSignup',
    label: 'Newsletter signup',
    category: 'form',
    tag: 'section',
    icon: 'newsletter',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 8,
    requiredPluginId: 'forms',
    docs: 'An email capture band backed by the Forms plugin.',
    traits: [
      {
        name: 'data-instance',
        label: 'Forms instance',
        kind: 'contentRef',
        required: true,
        accepts: { pluginId: 'forms' },
      },
      { name: 'data-form', label: 'Form slug', kind: 'text', default: 'newsletter' },
    ],
    snippet: `<section class="dcms-newsletter-signup dcms-section" data-instance="" data-form="newsletter">
  <div class="dcms-container">
    <h2>Stay in the loop</h2>
    <form class="dcms-form" method="post">
      <label class="dcms-field"><span>Email</span><input name="email" type="email" required /></label>
      <button class="dcms-button" type="submit">Subscribe</button>
    </form>
  </div>
</section>`,
  },
];
