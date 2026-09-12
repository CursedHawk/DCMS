import type { ToolSpec } from '../../agent/contracts';
import { gitApi } from '../../site-source/git';
import { latestBuild, previewAvailable, requestBuild } from '../preview/buildBroker';
import { previewAttached, previewMessages, queryPreview } from '../preview/previewBridge';
import { checkTypes } from '../diagnostics/checkTypes';
import { isCheckable } from '../diagnostics/typeCheck';
import type { BuildProblem } from '../preview/problems';
import { IDE_TOOLS, type IdeToolContext } from './workspaceTools';

/**
 * Tools that answer questions the server or the compiler already knows.
 *
 * <p>These are the brief's "zero-token facts": what changed, what branch, does it build. Every
 * one of them used to be answerable only by asking the model to reason about files it had read,
 * which is both expensive and unreliable — the compiler's opinion on whether something compiles
 * is free and correct, and the model's is neither.</p>
 */

export interface CheckToolContext extends IdeToolContext {
  siteId: string;
  branch: string;
}

type CheckTool = ToolSpec<CheckToolContext>;

/** How many problems are worth listing before the rest are a count. */
const MAX_PROBLEMS = 25;

export function formatProblems(problems: readonly BuildProblem[]): string {
  const errors = problems.filter((p) => p.severity === 'error');
  const warnings = problems.filter((p) => p.severity === 'warning');
  if (problems.length === 0) return 'Build succeeded with no problems.';

  const lines = [...errors, ...warnings].slice(0, MAX_PROBLEMS).map((p) => {
    const where = p.file ? `${p.file}:${p.line}:${p.column}` : '(dependency)';
    return `${p.severity}: ${where} — ${p.text}`;
  });

  const header = `${errors.length} error${errors.length === 1 ? '' : 's'}, ${warnings.length} warning${warnings.length === 1 ? '' : 's'}.`;
  const more =
    problems.length > MAX_PROBLEMS ? `\n… and ${problems.length - MAX_PROBLEMS} more.` : '';
  return `${header}\n${lines.join('\n')}${more}`;
}

export const CHECK_TOOLS: CheckTool[] = [
  {
    name: 'check_types',
    description:
      'Type-check the project with TypeScript and report errors with their file, line and column. DIFFERENT FROM check_build: the bundler strips types without reading them, so wrong arguments, misspelled props and a null passed where one is not allowed all compile cleanly and only break at runtime. This catches them. Works whether or not the preview is open. Pass `paths` to check only the files you changed; omit it to check everything you have touched this run.',
    input_schema: {
      type: 'object',
      properties: {
        paths: {
          type: 'array',
          items: { type: 'string' },
          description: 'Files to check. Defaults to the files this run has changed.',
        },
      },
      additionalProperties: false,
    },
    describe: (input) => {
      const paths = Array.isArray(input.paths) ? (input.paths as string[]) : [];
      return paths.length === 1 ? `check types in ${paths[0]}` : 'check types';
    },
    // A type error's message can be long and chained; twenty of them at the default ceiling
    // would be clipped mid-diagnostic, which is worse than being told there are more.
    maxResultChars: 10_000,
    run: async (input, ctx) => {
      const asked = Array.isArray(input.paths) ? (input.paths as string[]).map(String) : null;
      const paths = (asked ?? ctx.tx.changes().map((c) => c.path)).filter(isCheckable);

      if (paths.length === 0) {
        return {
          content: asked
            ? 'None of those paths are TypeScript or JavaScript, so there is nothing to type-check.'
            : 'No TypeScript or JavaScript files have been changed in this run yet.',
        };
      }

      const problems = await checkTypes(paths);
      if (problems === null) {
        // Never "clean". A check that did not run must not read as one that passed.
        return {
          content:
            'The type checker could not be reached, so nothing can be concluded about types. Do not report this as passing.',
          isError: true,
        };
      }

      const checked = `Checked ${paths.length} file${paths.length === 1 ? '' : 's'}.`;
      if (problems.length === 0) return { content: `${checked} No type errors.` };
      return { content: `${checked}\n${formatProblems(problems)}` };
    },
  },

  {
    name: 'check_build',
    description:
      'Compile the site and report errors and warnings with their file, line and column. Run this after a meaningful change instead of asking whether the code is correct — the compiler answers for free and is never wrong about it. Returns quickly from cache if nothing has changed since the last build.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'check build',
    run: async (_input, ctx) => {
      if (!previewAvailable()) {
        // Deliberately an error, not an empty success. A validation tool that reports "no
        // problems" when it did not run is worse than none at all, because the model trusts it.
        return {
          content:
            'Cannot check: the preview pane is closed, so there is no compiler attached. Ask the user to open the preview, or proceed without build verification and say so.',
          isError: true,
        };
      }

      const snapshot = await requestBuild(ctx.workspace.revision.revision);
      if (!snapshot || snapshot.revision === -1) {
        return {
          content: 'The build did not complete in time. Nothing can be concluded about it.',
          isError: true,
        };
      }

      const body = formatProblems(snapshot.problems);
      const stale =
        snapshot.revision !== ctx.workspace.revision.revision
          ? '\n\nNote: this build is from an earlier revision than your current workspace.'
          : '';
      return { content: `${body}${stale}`, isError: !snapshot.ok };
    },
  },

  {
    name: 'last_build',
    description:
      'The most recent build result without triggering a new one. Use when you only want to know whether the project was already broken before you touched it.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'read last build',
    run: async () => {
      const snapshot = latestBuild();
      if (!snapshot) return { content: 'No build has completed yet in this session.' };
      return { content: formatProblems(snapshot.problems), isError: !snapshot.ok };
    },
  },

  {
    name: 'dependencies',
    description:
      'What this site may import: the packages in package.json, and which of them the live preview can bundle. A package outside the preview palette still builds and deploys — it just will not appear in the preview until the site is published.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'list dependencies',
    run: async (_input, ctx) => {
      const deps = Object.entries(ctx.index.dependencies);
      if (deps.length === 0) return { content: 'No package.json, or it declares no dependencies.' };
      return {
        content: deps
          .sort(([a], [b]) => a.localeCompare(b))
          .map(([name, version]) => `${name} ${version}`)
          .join('\n'),
      };
    },
  },

  {
    name: 'git_status',
    description:
      'What has changed in the working draft compared to the branch it is based on: added, modified and deleted paths. Cheaper and more reliable than inferring it from what you remember editing.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'git status',
    run: async (_input, ctx) => {
      const changes = await gitApi.changes(ctx.siteId, ctx.branch);
      if (changes.length === 0) return { content: `No changes on ${ctx.branch}.` };
      return {
        content: [
          `${changes.length} changed file(s) on ${ctx.branch}:`,
          ...changes.map((c) => `${c.status.toLowerCase().padEnd(9)} ${c.path}`),
        ].join('\n'),
      };
    },
  },

  {
    name: 'git_diff',
    description:
      'The diff for one changed file: the branch version against the working draft. Use to see exactly what a change looks like before committing or describing it.',
    input_schema: {
      type: 'object',
      properties: { path: { type: 'string' } },
      required: ['path'],
      additionalProperties: false,
    },
    describe: (input) => `git diff ${String(input.path ?? '')}`,
    // A file diff is two full copies of a file; the default ceiling would clip most of them
    // mid-line. Given generously here and narrowed by asking for one path at a time.
    maxResultChars: 12_000,
    run: async (input, ctx) => {
      const path = String(input.path ?? '');
      const changes = await gitApi.changes(ctx.siteId, ctx.branch);
      const change = changes.find((c) => c.path === path);
      if (!change) return { content: `${path} has no pending changes.`, isError: true };
      if (change.truncated) {
        return {
          content: `${path} is too large to diff. Read the file directly instead.`,
          isError: true,
        };
      }
      return {
        content: [
          `${path} (${change.status})`,
          '--- branch',
          change.headContent ?? '(absent)',
          '--- draft',
          change.draftContent ?? '(deleted)',
        ].join('\n'),
      };
    },
  },

  {
    name: 'git_history',
    description:
      'Recent commits on this branch: sha, message and author. Use to understand how the site got to its current state, or to find when something was introduced.',
    input_schema: {
      type: 'object',
      properties: { limit: { type: 'number', description: 'Default 15.' } },
      additionalProperties: false,
    },
    describe: () => 'git history',
    run: async (input, ctx) => {
      const limit = typeof input.limit === 'number' ? Math.min(input.limit, 50) : 15;
      const commits = await gitApi.history(ctx.siteId, ctx.branch, limit);
      if (commits.length === 0) return { content: `No commits on ${ctx.branch}.` };
      return {
        content: commits
          .map(
            (c) => `${c.shortSha}  ${c.message?.split('\n')[0] ?? ''} — ${c.author ?? 'unknown'}`,
          )
          .join('\n'),
      };
    },
  },

  {
    name: 'git_branches',
    description: 'The branches this site has, and which one the working draft is on.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'git branches',
    run: async (_input, ctx) => {
      const branches = await gitApi.branches(ctx.siteId);
      return {
        content: branches.map((b) => `${b.name === ctx.branch ? '* ' : '  '}${b.name}`).join('\n'),
      };
    },
  },
];

/**
 * Everything the IDE agent can do, in the order it should reach for them.
 *
 * <p>Order matters more than it looks: some providers weight earlier tools slightly, and more
 * importantly this is the order a human reads when debugging a run. Inspection first, then
 * edits, then verification — which is also the sequence the system prompt asks for.</p>
 */
const PREVIEW_TOOLS: CheckTool[] = [
  {
    name: 'preview_console',
    description:
      'Errors, warnings, uncaught exceptions and failed network requests from the RUNNING preview. A build that compiles can still throw on mount or fetch a 404 — this is how you find that out. Check it after a change that could affect behaviour, not just compilation.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'read preview console',
    /*
     * The rendered page is the one thing the agent reads that somebody with no access to this
     * workspace can influence: it shows published content, live API responses, and whatever a
     * visitor submitted that the site chose to display. See `markUntrusted` in the runtime.
     */
    untrustedSource: 'the running preview page',
    run: async () => {
      if (!previewAttached()) {
        return {
          content: 'The preview is not running, so there is no console to read.',
          isError: true,
        };
      }
      const messages = previewMessages();
      if (messages.length === 0) {
        return { content: 'The preview reported no errors, warnings or failed requests.' };
      }
      return {
        content: messages.map((m) => `${m.kind}: ${m.text}`).join('\n'),
        isError: messages.some((m) => m.kind !== 'warn'),
      };
    },
  },

  {
    name: 'preview_dom',
    description:
      'The rendered HTML of the running preview, or of one CSS selector within it. Use to check what actually rendered — an empty root, a element nested wrongly, a class that never applied. Pass a selector: the whole body of a real page is a lot of tokens.',
    input_schema: {
      type: 'object',
      properties: {
        selector: { type: 'string', description: 'CSS selector. Omit for the whole body.' },
      },
      additionalProperties: false,
    },
    describe: (input) => `inspect ${String(input.selector ?? 'body')}`,
    maxResultChars: 6000,
    /*
     * The rendered page is the one thing the agent reads that somebody with no access to this
     * workspace can influence: it shows published content, live API responses, and whatever a
     * visitor submitted that the site chose to display. See `markUntrusted` in the runtime.
     */
    untrustedSource: 'the running preview page',
    run: async (input) => {
      const selector = typeof input.selector === 'string' ? input.selector : undefined;
      const html = await queryPreview('dom', selector);
      if (html === null) {
        // A page stuck in a render loop never services the message. Saying so beats waiting.
        return {
          content: 'The preview did not answer — it may not be running, or may be stuck.',
          isError: true,
        };
      }
      return { content: html };
    },
  },

  {
    name: 'preview_text',
    description:
      'The visible text of the running preview, or of one selector. Much cheaper than preview_dom when you only need to confirm what copy is on the page.',
    input_schema: {
      type: 'object',
      properties: { selector: { type: 'string' } },
      additionalProperties: false,
    },
    describe: (input) => `read text ${String(input.selector ?? 'body')}`,
    maxResultChars: 4000,
    /*
     * The rendered page is the one thing the agent reads that somebody with no access to this
     * workspace can influence: it shows published content, live API responses, and whatever a
     * visitor submitted that the site chose to display. See `markUntrusted` in the runtime.
     */
    untrustedSource: 'the running preview page',
    run: async (input) => {
      const selector = typeof input.selector === 'string' ? input.selector : undefined;
      const text = await queryPreview('text', selector);
      if (text === null) {
        return { content: 'The preview did not answer.', isError: true };
      }
      return { content: text.trim() || '(nothing visible)' };
    },
  },
];

export const ALL_TOOLS: CheckTool[] = [
  ...(IDE_TOOLS as CheckTool[]),
  ...CHECK_TOOLS,
  ...PREVIEW_TOOLS,
];
