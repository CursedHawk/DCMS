// dcms: develop DCMS functions from a clone on your own machine (ADR 0031).
// What the server says is authoritative: `check` and `run` send the working tree to the same
// build the web IDE uses, and `deploy` merges main into release on the server, as the console does.
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { parseArgs } from 'node:util';
import {
  authOrigin,
  CLIENT_ID,
  csharpFiles,
  editorFiles,
  formatProblem,
  git,
  hasCSharp,
  ignoreDcms,
  keptTokens,
  parseInput,
  readCredentials,
  readLink,
  removeCredentials,
  repoRoot,
  SCOPES,
  workingFiles,
  writeCredentials,
  writeFiles,
  writeLink,
} from './lib.mjs';

const HELP = `dcms — develop DCMS functions on your own machine

  dcms login [--server https://admin.example.com]   sign in through your browser
  dcms logout                                       forget this machine's sign-in
  dcms link [--remote origin]                       link this clone to its app
  dcms sdk                                          write .dcms/ (SDK) and editor config
  dcms check                                        build the working tree on the server
  dcms run <function> [--input JSON|@file] [--live] run it: the working tree, or what is live
  dcms runs [function]                              the newest runs
  dcms logs <runId>                                 one run: input, result, logs
  dcms secrets [set NAME [VALUE] | rm NAME]         list names, set one (stdin if no VALUE), remove one
  dcms deploy                                       merge main into release, wait for the build, deploy it
`;

export async function main(argv) {
  const [command, ...rest] = argv;
  const handler = COMMANDS[command];
  if (!handler) {
    process.stdout.write(HELP);
    return command && !['help', '--help', '-h'].includes(command) ? 1 : 0;
  }
  return (await handler(rest)) ?? 0;
}

const COMMANDS = {
  async login(args) {
    const { values } = parseArgs({ args, options: { server: { type: 'string' }, auth: { type: 'string' } } });
    const server = new URL(values.server ?? readCredentials()?.server ?? 'https://admin.highgeek.eu').origin;
    const auth = values.auth ? new URL(values.auth).origin : authOrigin(server);
    const discovery = await json(await fetch(`${auth}/.well-known/openid-configuration`));
    const device = await json(await fetch(discovery.device_authorization_endpoint, form({ client_id: CLIENT_ID, scope: SCOPES })));

    console.log(`Open ${device.verification_uri_complete ?? device.verification_uri} and approve the code ${device.user_code}.`);
    let interval = (device.interval ?? 5) * 1000;
    const until = Date.now() + (device.expires_in ?? 600) * 1000;
    while (Date.now() < until) {
      await sleep(interval);
      const res = await fetch(discovery.token_endpoint, form({ grant_type: 'urn:ietf:params:oauth:grant-type:device_code', device_code: device.device_code, client_id: CLIENT_ID }));
      const body = await res.json();
      if (res.ok) {
        writeCredentials({ server, tokenEndpoint: discovery.token_endpoint, ...keptTokens(body) });
        console.log(`Signed in to ${server}.`);
        return 0;
      }
      if (body.error === 'slow_down') interval += 5000;
      else if (body.error !== 'authorization_pending') throw new Error(body.error === 'access_denied' ? 'The code was denied.' : body.error_description || body.error);
    }
    throw new Error('The code expired. Run `dcms login` again.');
  },

  async logout() {
    removeCredentials();
    console.log('Signed out on this machine. To end the sign-in everywhere, remove it under Account → Command lines.');
  },

  async link(args) {
    const { values } = parseArgs({ args, options: { remote: { type: 'string', default: 'origin' } } });
    const root = repoRoot();
    const remote = git(root, 'remote', 'get-url', values.remote);
    const found = await api(`/api/admin/me/repos?remote=${encodeURIComponent(remote)}`);
    writeLink(root, found, { name: values.remote, url: remote });
    console.log(`Linked to ${found.app} in ${found.tenant} (${found.environment}). Next: dcms sdk`);
  },

  async sdk() {
    const root = repoRoot();
    const link = readLink(root);
    const sdk = await fn(link, '/sdk');
    const files = { '.dcms/functions.ts': sdk.typings, '.dcms/Sdk.g.cs': sdk.csharp, ...editorFiles() };
    // C#: the server's Dcms.Functions.Sdk in a package folder of the clone's own, and a project
    // that references it. Written once; yours to change after.
    if (sdk.package && hasCSharp(root)) {
      const nupkg = await fn(link, '/sdk.nupkg', undefined, 'GET', true);
      Object.assign(files, csharpFiles(sdk.package, nupkg, (p) => existsSync(join(root, p))));
    }
    const changed = writeFiles(root, files);
    ignoreDcms(root);
    console.log(changed.length ? `Wrote ${changed.join(', ')}.` : 'Up to date.');
  },

  async check() {
    const root = repoRoot();
    const result = await fn(readLink(root), '/check', { files: workingFiles(root) });
    for (const p of result.problems ?? []) console.log(formatProblem(p));
    if (!result.ok) return 1;
    console.log(`OK: ${(result.functions ?? []).join(', ') || 'no functions'}.`);
    if (result.permissions?.length) console.log(`Deploying needs: ${result.permissions.join(', ')}.`);
  },

  async run(args) {
    const { values, positionals } = parseArgs({ args, allowPositionals: true, options: { input: { type: 'string' }, live: { type: 'boolean' } } });
    const [name] = positionals;
    if (!name) throw new Error('Which function? dcms run <function>');
    const root = repoRoot();
    const input = parseInput(values.input);
    const outcome = values.live
      ? await fn(readLink(root), '/run', { function: name, input })
      : await fn(readLink(root), '/test', { files: workingFiles(root), function: name, input });
    if (outcome.logs) process.stderr.write(outcome.logs.endsWith('\n') ? outcome.logs : `${outcome.logs}\n`);
    console.log(JSON.stringify(outcome.result ?? null, null, 2));
    console.error(`${outcome.status} in ${outcome.durationMs} ms, ${outcome.calls} host call(s).`);
    if (outcome.error) {
      console.error(outcome.error);
      return 1;
    }
  },

  async runs(args) {
    const [name] = args;
    const page = await fn(readLink(repoRoot()), `/runs${name ? `?function=${encodeURIComponent(name)}` : ''}`);
    for (const r of page.items) {
      console.log(`${r.id}  ${r.createdAt}  ${r.function.padEnd(24)} ${r.trigger.padEnd(8)} ${r.status.padEnd(9)} ${r.durationMs} ms${r.error ? `  ${r.error}` : ''}`);
    }
  },

  async logs(args) {
    const [id] = args;
    if (!id) throw new Error('Which run? dcms logs <runId> (dcms runs lists them)');
    const run = await fn(readLink(repoRoot()), `/runs/${encodeURIComponent(id)}`);
    console.log(JSON.stringify({ input: run.input, result: run.result, error: run.run.error }, null, 2));
    if (run.logs) process.stdout.write(run.logs.endsWith('\n') ? run.logs : `${run.logs}\n`);
  },

  async secrets(args) {
    const [action, name, value] = args;
    const link = readLink(repoRoot());
    if (!action) {
      for (const n of await fn(link, '/secrets')) console.log(n);
    } else if (action === 'set' && name) {
      const secret = value ?? readFileSync(0, 'utf8').replace(/\r?\n$/, '');
      await fn(link, `/secrets/${encodeURIComponent(name)}`, { value: secret }, 'PUT');
      console.log(`Set ${name}.`);
    } else if (action === 'rm' && name) {
      await fn(link, `/secrets/${encodeURIComponent(name)}`, undefined, 'DELETE');
      console.log(`Removed ${name}.`);
    } else {
      throw new Error('dcms secrets [set NAME [VALUE] | rm NAME]');
    }
  },

  async deploy() {
    const link = readLink(repoRoot());
    console.log(`Deploying ${link.app} in ${link.tenant} (${link.environment}).`);
    const merge = await api(`/api/admin/repos/${encodeURIComponent(link.repoId)}/git/merge`, link, { head: 'main' });
    if (merge.conflict) {
      for (const f of merge.files ?? []) console.error(`conflict: ${f.path}`);
      throw new Error('main does not merge into release cleanly. Resolve it in the IDE, then deploy again.');
    }
    const sha = merge.sha;
    console.log(merge.upToDate ? `release is already at main (${sha?.slice(0, 7)}).` : `Merged main into release (${sha?.slice(0, 7)}). Building…`);
    const until = Date.now() + 10 * 60_000;
    while (Date.now() < until) {
      const build = (await fn(link, '/deployments')).find((d) => d.commitSha === sha);
      if (build?.status === 'Active') {
        console.log(`#${build.number} is live.`);
        return 0;
      }
      if (build?.status === 'Failed') throw new Error(`#${build.number} did not build: ${build.error ?? 'see the Deployments screen'}`);
      if (build && build.status !== 'Building') {
        const live = await fn(link, `/deployments/${build.id}/activate`, {});
        console.log(`#${live.number} is live: ${live.functions.join(', ')}.`);
        return 0;
      }
      await sleep(3000);
    }
    throw new Error('The build did not finish in 10 minutes. Check the Deployments screen.');
  },
};

/** A call to the linked app's functions API. */
function fn(link, path, body, method, bytes) {
  return api(`/api/admin/plugins/${encodeURIComponent(link.app)}/_functions${path}`, link, body, method, bytes);
}

/** An admin API call as the signed-in person: their bearer, the link's tenant and environment. */
async function api(path, link, body, method, bytes) {
  const creds = await freshCredentials();
  const headers = { Authorization: `Bearer ${creds.accessToken}`, Accept: 'application/json' };
  if (link?.tenant) headers['X-Dcms-Tenant'] = link.tenant;
  if (link?.environment) headers['X-Dcms-Environment'] = link.environment;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(`${creds.server}${path}`, {
    method: method ?? (body !== undefined ? 'POST' : 'GET'),
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (res.status === 401) throw new Error('Not signed in, or the sign-in was removed. Run `dcms login`.');
  if (res.status === 403) throw new Error(`You may not do that here (${path}).`);
  if (bytes && res.ok) return new Uint8Array(await res.arrayBuffer());
  return res.status === 204 ? undefined : json(res);
}

async function freshCredentials() {
  const creds = readCredentials();
  if (!creds) throw new Error('Not signed in. Run `dcms login`.');
  if (creds.expiresAt - 60_000 > Date.now()) return creds;
  const res = await fetch(creds.tokenEndpoint, form({ grant_type: 'refresh_token', refresh_token: creds.refreshToken, client_id: CLIENT_ID }));
  if (!res.ok) throw new Error('The sign-in expired or was removed. Run `dcms login`.');
  const next = { ...creds, ...keptTokens(await res.json()) };
  writeCredentials(next);
  return next;
}

function form(fields) {
  return { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(fields) };
}

async function json(res) {
  const text = await res.text();
  let body;
  try {
    body = text ? JSON.parse(text) : undefined;
  } catch {
    body = undefined;
  }
  if (!res.ok) throw new Error(body?.error_description ?? body?.error ?? body?.title ?? `${res.status} ${res.statusText}`);
  return body;
}
