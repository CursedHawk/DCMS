import {
  GLOBAL_CSS,
  SITE_JSON,
  pageHtmlPath,
  safeParseSiteManifest,
  serializeSiteManifest,
  slugFromPageHtmlPath,
  slugFromRegionHtmlPath,
  type NavItem,
} from '@dcms/gjs-schema';
import { DESIGN_KITS, applyKit, findKit, matchKit } from '@dcms/gjs-blocks';
import type { ToolSpec } from '../../agent/contracts';
import { useVfs } from '../../site-source/vfs';
import type { CheckToolContext } from '../../ide/agent/checkTools';

/**
 * The Mode A agent's own tools, and the deterministic check behind them.
 *
 * <p>A Mode A site has no compiler, so the Mode B gate — esbuild bundles it, tsc type-checks it
 * — has nothing to say about it. What it does have is a manifest that the builder must be able
 * to open: `site.json` lists the pages, `pages/<slug>.html` holds each one, and a mismatch
 * between the two is a page that either does not exist or never ships. That is checkable for
 * free, from a file map the browser already holds, and it is exactly the class of mistake an
 * agent writing several files in a row makes.</p>
 *
 * <p>So this is the same bargain `check_types` strikes in Mode B: the certain, free answer in
 * place of asking the model whether its own output was consistent.</p>
 */

/** What is wrong with a Mode A file map, split by whether it stops the site working. */
export interface StaticSiteReport {
  /** The site cannot be opened or a page cannot ship. */
  errors: string[];
  /** It works, but breaks a rule the builder and the design kits depend on. */
  warnings: string[];
}

const STYLE_ATTR = /<[^>]+\sstyle\s*=\s*["']/i;
const SCRIPT_TAG = /<script[\s>]/i;
const DOCUMENT_TAG = /<\/?(?:html|head|body)[\s>]/i;
const IMG_WITHOUT_ALT = /<img\b(?![^>]*\balt\s*=)[^>]*>/i;

/** Every nav path, including nested ones. */
function navPaths(items: readonly NavItem[]): string[] {
  return items.flatMap((item) => [item.path, ...navPaths(item.children ?? [])]);
}

/** Whether a page route serves an address, treating `:param` as a wildcard segment. */
function routeServes(route: string, address: string): boolean {
  const left = route.split('/');
  const right = address.split('/');
  if (left.length !== right.length) return false;
  return left.every((part, i) => part.startsWith(':') || part === right[i]);
}

export function checkStaticSite(files: Readonly<Record<string, string>>): StaticSiteReport {
  const errors: string[] = [];
  const warnings: string[] = [];

  const raw = files[SITE_JSON];
  if (raw === undefined) {
    return { errors: [`${SITE_JSON} is missing. Without it there is no site.`], warnings };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (e) {
    return { errors: [`${SITE_JSON} is not valid JSON: ${(e as Error).message}`], warnings };
  }

  const result = safeParseSiteManifest(parsed);
  if (!result.success) {
    const issues = result.error.issues.slice(0, 5).map((issue) => {
      const where = issue.path.length ? `${issue.path.join('.')}: ` : '';
      return `${SITE_JSON} — ${where}${issue.message}`;
    });
    // A manifest that does not parse makes every other check meaningless: there are no pages to
    // reconcile against. Reported alone, so the model fixes one thing rather than a cascade.
    return { errors: issues, warnings };
  }

  const manifest = result.data;
  const htmlSlugs = new Set(
    Object.keys(files)
      .map(slugFromPageHtmlPath)
      .filter((slug): slug is string => slug !== null),
  );

  for (const page of manifest.pages) {
    const path = pageHtmlPath(page.slug);
    if (!htmlSlugs.has(page.slug)) {
      errors.push(`“${page.title}” is listed in ${SITE_JSON} but ${path} does not exist.`);
    } else if (!files[path]?.trim()) {
      errors.push(`${path} is empty, so “${page.title}” would publish as a blank page.`);
    }
  }

  const listed = new Set(manifest.pages.map((p) => p.slug));
  for (const slug of htmlSlugs) {
    if (!listed.has(slug)) {
      errors.push(
        `${pageHtmlPath(slug)} has no entry in ${SITE_JSON}.pages, so it never ships. Add the entry or delete the file.`,
      );
    }
  }

  if (!manifest.pages.some((p) => p.path === '/')) {
    errors.push(`No page is published at “/”. The site has no home page.`);
  }

  const routes = manifest.pages.map((p) => p.path);
  for (const path of navPaths(manifest.nav)) {
    if (!path.startsWith('/')) continue;
    if (!routes.some((route) => routeServes(route, path))) {
      warnings.push(`${SITE_JSON}.nav links to “${path}”, which no page serves.`);
    }
  }

  const regionSlugs = new Set(manifest.regions.map((r) => r.slug));
  for (const path of Object.keys(files)) {
    const slug = slugFromRegionHtmlPath(path);
    if (slug && !regionSlugs.has(slug)) {
      warnings.push(`${path} is not a region in ${SITE_JSON}.regions, so no page draws it.`);
    }
  }

  const markup = Object.entries(files).filter(
    ([path]) => slugFromPageHtmlPath(path) !== null || slugFromRegionHtmlPath(path) !== null,
  );
  for (const [path, content] of markup) {
    if (DOCUMENT_TAG.test(content)) {
      errors.push(`${path} contains <html>, <head> or <body>. A page file is a body fragment.`);
    }
    if (SCRIPT_TAG.test(content)) {
      errors.push(`${path} contains a <script>, which the publisher strips.`);
    }
    if (STYLE_ATTR.test(content)) {
      warnings.push(
        `${path} uses a style="" attribute. Give the element a class and style it in a stylesheet, or no design kit can reach it.`,
      );
    }
    if (IMG_WITHOUT_ALT.test(content)) {
      warnings.push(`${path} has an <img> with no alt attribute.`);
    }
  }

  if (files[GLOBAL_CSS] === undefined) {
    warnings.push(`${GLOBAL_CSS} does not exist; shared rules have nowhere to live.`);
  }

  return { errors, warnings };
}

/** The report as the model reads it. */
export function formatStaticReport(report: StaticSiteReport): string {
  if (report.errors.length === 0 && report.warnings.length === 0) {
    return 'The site is consistent: every page has a manifest entry and a file, and the markup follows the format.';
  }
  return [
    ...report.errors.map((e) => `error: ${e}`),
    ...report.warnings.map((w) => `warning: ${w}`),
  ].join('\n');
}

/** The kit catalogue as one line per kit, for the tool description. */
const KIT_LIST = DESIGN_KITS.map((kit) => `${kit.id} (${kit.description})`).join('; ');

export const STATIC_TOOLS: ToolSpec<CheckToolContext>[] = [
  {
    /*
     * The supported way to set a look, and the reason it is a tool rather than a line in the
     * prompt telling the model to write `site.json.theme` by hand.
     *
     * The block stylesheet reads close to sixty custom properties. A hand-written theme defines
     * the half-dozen the model thought of, and the other fifty declarations silently fall back
     * to nothing — no type scale, no radii, no shadows — which renders as a site that looks
     * broken for no visible reason. A kit is a complete token set by construction, so applying
     * one cannot produce that state.
     */
    name: 'apply_design_kit',
    description: `Set this site's whole look — colours, type, spacing rhythm, corners, shadows — from a named design kit. A kit is a complete set of theme tokens, which is why this is the right way to restyle a site: the block stylesheet reads about sixty custom properties and a theme written by hand defines a handful, leaving most of the design undeclared. Applying a kit rewrites site.json.theme (and so the generated styles/theme.css) and touches no page and no hand-written CSS, so it is reversible. Kits: ${KIT_LIST}.`,
    input_schema: {
      type: 'object',
      properties: {
        kit: { type: 'string', enum: DESIGN_KITS.map((k) => k.id) },
      },
      required: ['kit'],
      additionalProperties: false,
    },
    risk: 'safe',
    describe: (input) => `apply design kit ${String(input.kit ?? '')}`,
    summarize: (input) =>
      `Restyle the whole site with the “${findKit(String(input.kit ?? ''))?.name ?? String(input.kit ?? '')}” design kit`,
    run: async (input, ctx) => {
      const kit = findKit(String(input.kit ?? ''));
      if (!kit) {
        return {
          content: `No kit named that. Available: ${DESIGN_KITS.map((k) => k.id).join(', ')}.`,
          isError: true,
        };
      }

      const raw = useVfs.getState().files[SITE_JSON];
      if (raw === undefined) return { content: `${SITE_JSON} does not exist.`, isError: true };

      let manifest;
      try {
        const parsed = safeParseSiteManifest(JSON.parse(raw));
        if (!parsed.success) throw new Error(parsed.error.issues[0]?.message ?? 'invalid');
        manifest = parsed.data;
      } catch (e) {
        return {
          content: `${SITE_JSON} cannot be read as a manifest (${(e as Error).message}). Fix it first.`,
          isError: true,
        };
      }

      const was = matchKit(manifest.theme);
      const next = serializeSiteManifest({ ...manifest, theme: applyKit(manifest.theme, kit) });
      if (next === raw) return { content: `The site is already on the “${kit.name}” kit.` };

      // Through the transaction like every other write, so the change-review pane lists
      // site.json and the author can revert this the same way they revert an edited page.
      const outcome = ctx.tx.patch(SITE_JSON, { oldText: raw, newText: next });
      if (outcome.isError) return outcome;
      return {
        ...outcome,
        content: `Applied the “${kit.name}” kit${was ? ` (was “${was.name}”)` : ''}. ${kit.description}`,
      };
    },
  },
  {
    name: 'check_site',
    description:
      'Check this site for the mistakes that stop it opening or publishing: a page in site.json with no pages/<slug>.html (or the reverse), no page at "/", nav pointing nowhere, a page file carrying <html> or <script>, inline styles, images with no alt. Free and certain — call it after writing files rather than reasoning about whether your own output was consistent.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'check site',
    // Read from the live store rather than the run's pinned snapshot: the point of the check is
    // the state the files are in *now*, after this turn's writes.
    run: async () => {
      const report = checkStaticSite(useVfs.getState().files);
      return { content: formatStaticReport(report), isError: report.errors.length > 0 };
    },
  },
];

/**
 * The run's deterministic gate, for the session to call after a turn that wrote files.
 *
 * <p>Never null, unlike Mode B's compiler checks: this one needs nothing to be attached, because
 * a file map is always readable. A Mode A run is therefore always verified, which is the one
 * thing this mode has easier.</p>
 */
export async function validateStaticSite(): Promise<{ ok: boolean; report: string }> {
  const report = checkStaticSite(useVfs.getState().files);
  return { ok: report.errors.length === 0, report: formatStaticReport(report) };
}
