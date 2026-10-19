import type { ComponentType, ReactNode } from 'react';
import { z } from 'zod';
import { propDefinitionSchema } from './props';
import type { ComponentDefinition, ComponentRenderProps } from './registry';

/**
 * Developer components (P7, ADR 0020): a React component a developer writes in TSX, placed and
 * configured on the canvas like any other.
 *
 * Two files: the contract `dcms/code/<name>.json` — what the builder knows (label, props) — and
 * the source `src/components/<name>.tsx`, whose default export receives the props as plain React
 * props. The contract is data and is read everywhere; the source is code and runs only where
 * tenant code may run: the published site and opaque-origin sandboxed frames. The canvas is
 * same-origin with the admin, so the source never runs in it: the builder draws each developer
 * component as an island — its own sandboxed frame — through `island`, and a placeholder until
 * the island is ready.
 */

export const CODE_PREFIX = 'code.';

export const codeType = (name: string) => `${CODE_PREFIX}${name}`;
export const codeContractPath = (name: string) => `dcms/code/${name}.json`;
export const codeSourcePath = (name: string) => `src/components/${name}.tsx`;

/** The component a contract path describes, or null for any other path. */
export function codeContractOf(path: string): string | null {
  return /^dcms\/code\/([a-z0-9][a-z0-9-]{0,47})\.json$/.exec(path)?.[1] ?? null;
}

/** The component a source path implements (`src/components/x.tsx`, or a bundler key ending so). */
export function codeSourceOf(path: string): string | null {
  return /(?:^|\/)components\/([a-z0-9][a-z0-9-]{0,47})\.tsx$/.exec(path)?.[1] ?? null;
}

export const codeContractSchema = z.strictObject({
  schemaVersion: z.literal(1),
  name: z.string().regex(/^[a-z0-9][a-z0-9-]{0,47}$/, 'must be kebab-case'),
  label: z.string().min(1).max(60),
  description: z.string().max(300).optional(),
  category: z.string().max(40).optional(),
  props: z.array(propDefinitionSchema).default([]),
});

export type CodeContract = z.infer<typeof codeContractSchema>;

/**
 * How the builder draws a developer component without running it here: a frame of its own that
 * renders `contract` with `props`, showing `fallback` until it can. Supplied by the admin; the
 * runtime only hands it what to draw.
 */
export type CodeIsland = ComponentType<{ contract: CodeContract; props: Record<string, unknown>; fallback: ReactNode }>;

/** Bundler key → the module's default export (`import.meta.glob(…, { import: 'default' })`). */
export type CodeModules = Readonly<Record<string, unknown>>;

/** Read every contract out of the site's parsed JSON documents. */
export function readCodeContracts(byPath: ReadonlyMap<string, unknown>): { contracts: CodeContract[]; problems: { path: string; message: string }[] } {
  const contracts: CodeContract[] = [];
  const problems: { path: string; message: string }[] = [];
  for (const [path, json] of byPath) {
    const name = codeContractOf(path);
    if (!name) continue;
    const parsed = codeContractSchema.safeParse(json);
    if (!parsed.success) {
      const issue = parsed.error.issues[0];
      problems.push({ path, message: `${issue?.path.join('.') || 'contract'}: ${issue?.message}` });
    } else if (parsed.data.name !== name) {
      problems.push({ path, message: `says it is ${parsed.data.name}, but the file is ${name}.` });
    } else contracts.push(parsed.data);
  }
  return { contracts, problems };
}

/**
 * The registry entries for developer components. With `modules` (the published site, the
 * sandboxed preview) an entry renders the developer's component; without (the canvas, the
 * in-admin preview) no developer code is loaded here: it renders through `island` if given, a
 * placeholder otherwise.
 */
export function codeDefinitions(contracts: readonly CodeContract[], modules?: CodeModules, island?: CodeIsland): ComponentDefinition[] {
  const byName = new Map<string, ComponentType<Record<string, unknown>>>();
  for (const [key, value] of Object.entries(modules ?? {})) {
    const name = codeSourceOf(key);
    if (name && typeof value === 'function') byName.set(name, value as ComponentType<Record<string, unknown>>);
  }
  return contracts.map((contract) => {
    const Impl = byName.get(contract.name);
    const Placeholder = placeholder(contract, modules !== undefined);
    const Island = modules === undefined ? island : undefined;
    return {
      type: codeType(contract.name),
      version: 1,
      label: contract.label,
      description: contract.description,
      category: contract.category || 'Developer',
      props: contract.props,
      component: Impl
        ? ({ props }: ComponentRenderProps) => <Impl {...props} />
        : Island
          ? ({ props }: ComponentRenderProps) => <Island contract={contract} props={props} fallback={<Placeholder />} />
          : Placeholder,
    };
  });
}

function placeholder(contract: CodeContract, missing: boolean) {
  return function CodePlaceholder() {
    return (
      <div className="dcms-code-placeholder" role="note">
        <strong>{contract.label}</strong>
        <span>{missing ? ` — ${codeSourcePath(contract.name)} has no default export.` : ' — developer component; it runs in the sandboxed preview and on the site.'}</span>
      </div>
    );
  };
}
