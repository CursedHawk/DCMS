import { css as beautifyCss, html as beautifyHtml } from 'js-beautify';

/**
 * Formatting for what the builder writes back to the repo.
 *
 * This is not cosmetic. The canvas serializes to a single line, and a one-line
 * `pages/home.html` would make every git diff useless — which is the whole point
 * of storing HTML instead of a JSON blob. Formatting is also the most expensive
 * step of a save on a large page, which is why it lives here, in the worker.
 */

const HTML_OPTIONS: Parameters<typeof beautifyHtml>[1] = {
  indent_size: 2,
  wrap_line_length: 0,
  preserve_newlines: false,
  end_with_newline: true,
  // Attribute-per-line only once a tag gets wide, so simple tags stay on one line.
  wrap_attributes: 'auto',
  // Keep text flow intact — re-indenting inside these changes what renders.
  unformatted: ['pre', 'textarea', 'code'],
  indent_inner_html: false,
  extra_liners: [],
};

const CSS_OPTIONS: Parameters<typeof beautifyCss>[1] = {
  indent_size: 2,
  end_with_newline: true,
  newline_between_rules: true,
  preserve_newlines: false,
};

export function formatHtml(input: string): string {
  if (!input.trim()) return '';
  try {
    return beautifyHtml(input, HTML_OPTIONS);
  } catch {
    // Never fail a save over formatting; the unformatted content is still correct.
    return endWithNewline(input);
  }
}

export function formatCss(input: string): string {
  if (!input.trim()) return '';
  try {
    return beautifyCss(input, CSS_OPTIONS);
  } catch {
    return endWithNewline(input);
  }
}

function endWithNewline(value: string): string {
  return value.endsWith('\n') ? value : `${value}\n`;
}
