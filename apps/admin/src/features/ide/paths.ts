// Mode B path helpers. Everything generic to a DCMS source editor lives in
// features/site-source/paths.ts; this file holds only what is specific to a
// React project's shape.

/** Pick the client-side preview entry point from the file map. */
export function previewEntry(files: Record<string, string>): string | null {
  for (const candidate of ['src/main.tsx', 'src/main.ts', 'src/index.tsx', 'src/index.ts', 'main.tsx']) {
    if (files[candidate] != null) return candidate;
  }
  // Fall back to the first .tsx/.ts under src.
  return Object.keys(files).find((p) => /^src\/.*\.(tsx?|jsx?)$/.test(p)) ?? null;
}
