// Environment profile loading, shared by the Node driver and (via `run.sh`, which
// exports the resolved values as k6 `-e` flags) by the k6 scenarios themselves.
//
// One profile per environment, selected by name. Nothing in a profile is a secret:
// credentials come from the process environment, so a profile can be committed and
// read by anyone without handing them a login. A profile that named a password would
// be the one file in this tree nobody could share.

import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
export const LOADTEST_ROOT = join(here, '..');
export const REPO_ROOT = join(LOADTEST_ROOT, '..');

/**
 * Values a profile may pull from the environment, written as ${VAR} in the JSON.
 * Expanded after parse so a profile stays valid JSON rather than a template dialect.
 * An unset variable is an error, not an empty string: silently authenticating as ""
 * produces a 401 that looks like a broken scenario.
 */
function expand(value, env) {
  if (typeof value === 'string') {
    return value.replace(/\$\{([A-Z0-9_]+)(?::-([^}]*))?\}/g, (_, name, fallback) => {
      const found = env[name];
      if (found !== undefined && found !== '') return found;
      if (fallback !== undefined) return fallback;
      throw new Error(
        `Profile references \${${name}} but it is not set in the environment. ` +
        `Export it, or add a :- default in the profile.`);
    });
  }
  if (Array.isArray(value)) return value.map((v) => expand(v, env));
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, expand(v, env)]));
  }
  return value;
}

/**
 * `--vus 20`, `-e vus=20` and `load.vus` in the profile all mean the same thing.
 * Overrides are dotted paths so anything in the profile can be overridden from the
 * command line without a bespoke flag per field.
 */
function applyOverrides(profile, overrides) {
  for (const [path, raw] of Object.entries(overrides)) {
    const parts = path.split('.');
    let node = profile;
    for (const part of parts.slice(0, -1)) {
      if (typeof node[part] !== 'object' || node[part] === null) node[part] = {};
      node = node[part];
    }
    const key = parts.at(-1);
    const existing = node[key];
    // Keep the profile's type: a threshold read back as the string "300" would make
    // every numeric comparison in a scenario silently false.
    node[key] = typeof existing === 'number' ? Number(raw)
      : typeof existing === 'boolean' ? raw === 'true'
      : raw;
  }
  return profile;
}

export async function loadProfile(name, { overrides = {}, env = process.env } = {}) {
  const path = join(LOADTEST_ROOT, 'env', `${name}.json`);
  let text;
  try {
    text = await readFile(path, 'utf8');
  } catch (cause) {
    throw new Error(`No environment profile '${name}' at ${path}`, { cause });
  }
  // Strip // comments so profiles can explain themselves; JSON cannot.
  const stripped = text.replace(/^\s*\/\/.*$/gm, '');
  const profile = applyOverrides(expand(JSON.parse(stripped), env), overrides);
  profile.name ??= name;
  return profile;
}

/**
 * The base URLs a scenario should use, given the profile's mode.
 *
 * `edge` goes through Caddy over public HTTPS — what a real client sees, TLS and
 * rate limiting included. `internal` addresses the services directly on the compose
 * network, which is the only way to tell "the edge is slow" from "the service is
 * slow". Running the same scenario in both and differencing the p95 is the point of
 * having two modes at all.
 */
export function targets(profile) {
  const mode = profile.mode ?? 'edge';
  if (mode !== 'edge' && mode !== 'internal') {
    throw new Error(`profile.mode must be 'edge' or 'internal', got '${mode}'`);
  }
  const t = profile[mode];
  if (!t) throw new Error(`Profile '${profile.name}' has mode '${mode}' but no '${mode}' block.`);
  return { mode, ...t };
}

/** Parse `key=value` repeated flags into the overrides map loadProfile expects. */
export function parseArgs(argv) {
  const out = { env: 'local', overrides: {}, rest: [] };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--env') out.env = argv[++i];
    else if (arg === '-e' || arg === '--set') {
      const [k, ...v] = argv[++i].split('=');
      out.overrides[k] = v.join('=');
    } else out.rest.push(arg);
  }
  return out;
}
