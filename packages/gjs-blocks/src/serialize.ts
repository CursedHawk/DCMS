/**
 * Serialization helpers shared by the builder's storage bridge and its tests.
 *
 * GrapesJS wraps a page in a `<body>` component, so `editor.getHtml()` always
 * returns `<body>…</body>`. The repo stores page *bodies* — the C# assembler
 * supplies the document shell — so the wrapper has to come off before the markup
 * is written. Feeding an unstripped result back into `setComponents()` nests a
 * second `<body>` inside the first, and every save-and-reload cycle adds another
 * one; this lives here rather than in the app so the round-trip test exercises
 * the same function production does.
 */

const WRAPPER = /^\s*<body(?:\s[^>]*)?>([\s\S]*)<\/body>\s*$/i;

/** The page body from a GrapesJS `getHtml()` result. */
export function pageBodyOf(html: string): string {
  const match = WRAPPER.exec(html);
  return match ? match[1] : html;
}

/** True when the markup is a GrapesJS wrapper rather than a bare page body. */
export function isWrapped(html: string): boolean {
  return WRAPPER.test(html);
}
