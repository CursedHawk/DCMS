// Pure helpers and the files the CLI keeps: no network here, so they test without a server.
import { execFileSync } from 'node:child_process';
import { chmodSync, existsSync, lstatSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';

export const CLIENT_ID = 'dcms-cli';
export const SCOPES = 'openid offline_access profile email roles dcms.admin';

/** Where sign-in lives: identity's own host, `auth.` beside the console's `admin.`. */
export function authOrigin(server) {
  const url = new URL(server);
  if (url.hostname.startsWith('admin.')) url.hostname = `auth.${url.hostname.slice('admin.'.length)}`;
  return url.origin;
}

export function configDir(env = process.env) {
  return join(env.XDG_CONFIG_HOME || join(homedir(), '.config'), 'dcms');
}

export function credentialsPath(env = process.env) {
  return join(configDir(env), 'credentials.json');
}

// ponytail: a 0600 file, like gh and npm without a keyring; move to the OS keychain if asked.
export function readCredentials(path = credentialsPath()) {
  return existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : null;
}

export function writeCredentials(value, path = credentialsPath()) {
  writePrivate(path, value);
}

function writePrivate(path, value) {
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
  writeFileSync(path, JSON.stringify(value, null, 2), { mode: 0o600 });
  chmodSync(path, 0o600);
}

export function removeCredentials(path = credentialsPath()) {
  rmSync(path, { force: true });
}

/** The tokens a token endpoint answered, as kept: the expiry as an instant. */
export function keptTokens(tokens, now = Date.now()) {
  return {
    accessToken: tokens.access_token,
    // Identity rotates it; a reply without one keeps the one we have.
    ...(tokens.refresh_token ? { refreshToken: tokens.refresh_token } : {}),
    expiresAt: now + (tokens.expires_in ?? 600) * 1000,
  };
}


export function git(root, ...args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

/** The clone's root, or the folder itself outside git. */
export function repoRoot(cwd = process.cwd()) {
  try {
    return git(cwd, 'rev-parse', '--show-toplevel');
  } catch {
    return cwd;
  }
}

// Which app a clone is, kept with this machine's sign-in and keyed by the clone's real path:
// never in the clone, which is someone else's content and could otherwise name another app.
const linksPath = (env) => join(configDir(env), 'links.json');
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;

export function writeLink(root, link, env = process.env) {
  const { tenant, environment, app, repoId } = link;
  if (![tenant, environment, app].every((v) => typeof v === 'string' && NAME.test(v)) || !GUID.test(String(repoId))) {
    throw new Error('The server answered with a link this command line does not understand.');
  }
  const path = linksPath(env);
  const links = existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : {};
  links[realpathSync(root)] = { tenant, environment, app, repoId, plugin: link.plugin, repo: link.repo };
  writePrivate(path, links);
}

export function readLink(root, env = process.env) {
  const path = linksPath(env);
  const link = existsSync(path) ? JSON.parse(readFileSync(path, 'utf8'))[realpathSync(root)] : undefined;
  if (!link) throw new Error('This clone is not linked. Run `dcms link` in it.');
  return link;
}

/**
 * Whether path, under root, is reached through no symbolic link: a clone is someone else's
 * content, and a link in it could point a read or a write anywhere on this machine.
 * True for a path that does not exist yet, as long as no folder on the way is a link.
 */
export function plainUnder(root, path) {
  const parts = path.split(/[\\/]/);
  if (parts.some((p) => p === '..' || p === '')) return false;
  let at = root;
  for (const part of parts) {
    at = join(at, part);
    let stat;
    try {
      stat = lstatSync(at);
    } catch {
      return true; // not there yet: nothing below it can be a link
    }
    if (stat.isSymbolicLink()) return false;
  }
  return lstatSync(at).isFile();
}

/** Writes files under root, creating folders; returns what changed. */
export function writeFiles(root, files) {
  const changed = [];
  for (const [path, text] of Object.entries(files)) {
    if (!plainUnder(root, path)) throw new Error(`Refusing to write ${path}: it, or a folder on the way, is a symbolic link or not a file.`);
    const full = join(root, path);
    if (existsSync(full) && Buffer.compare(readFileSync(full), Buffer.from(text)) === 0) continue;
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, text);
    changed.push(path);
  }
  return changed;
}

/** `.dcms/` (generated per environment) and a desktop build's bin/ and obj/ in .gitignore, once. */
export function ignoreDcms(root) {
  if (!plainUnder(root, '.gitignore')) throw new Error('Refusing to touch .gitignore: it is a symbolic link or not a file.');
  const path = join(root, '.gitignore');
  const text = existsSync(path) ? readFileSync(path, 'utf8') : '';
  const has = new Set(text.split(/\r?\n/).map((l) => l.trim().replace(/\/$/, '')));
  const missing = ['.dcms/', 'bin/', 'obj/'].filter((l) => !has.has(l.replace(/\/$/, '')));
  if (missing.length === 0) return false;
  writeFileSync(path, `${text}${text && !text.endsWith('\n') ? '\n' : ''}${missing.join('\n')}\n`);
  return true;
}

/** What the server's build reads: source, and JSON a function may import. */
const SOURCE = /\.(ts|mts|js|mjs|cs|json)$/;

/**
 * The working tree as the server checks it: tracked and new source files git does not ignore.
 * Never a symbolic link, nothing under a dot-folder or named with a dot (.env, .npmrc…), and
 * nothing that is not source: this is sent to the server, and a clone is someone else's content.
 */
export function workingFiles(root, maxBytes = 1024 * 1024) {
  const files = {};
  for (const path of git(root, 'ls-files', '-co', '--exclude-standard', '-z').split('\0')) {
    if (!path || !SOURCE.test(path) || path.split('/').some((p) => p.startsWith('.'))) continue;
    const full = join(root, path);
    if (!existsSync(full) || !plainUnder(root, path)) continue; // deleted, or a link
    const bytes = readFileSync(full);
    if (bytes.length > maxBytes || bytes.includes(0)) continue; // binary or huge: not source
    files[path] = bytes.toString('utf8');
  }
  return files;
}

/** The editor config that makes a local checker see what the server's build sees. */
export function editorFiles() {
  const lib = ['esnext', 'webworker'];
  return {
    'deno.json': `${JSON.stringify({ imports: { '@dcms/functions': './.dcms/functions.ts' }, compilerOptions: { strict: true, lib } }, null, 2)}\n`,
    'tsconfig.json': `${JSON.stringify(
      {
        compilerOptions: {
          target: 'esnext',
          module: 'esnext',
          moduleResolution: 'bundler',
          strict: true,
          lib,
          noEmit: true,
          allowImportingTsExtensions: true,
          paths: { '@dcms/functions': ['./.dcms/functions.ts'] },
        },
        include: ['functions', 'lib'],
      },
      null,
      2,
    )}\n`,
  };
}

/** Whether the repository has C# functions (any .cs outside .dcms). */
export function hasCSharp(root) {
  return git(root, 'ls-files', '-co', '--exclude-standard', '*.cs').split('\n').some((p) => p && !p.startsWith('.dcms/'));
}

/**
 * A desktop C# build: the server's Dcms.Functions.Sdk saved into .dcms/packages, the version it
 * is, and (when absent) a project and a NuGet source that use it.
 */
export function csharpFiles(version, nupkg, exists) {
  if (!/^[0-9A-Za-z.-]+$/.test(version)) throw new Error(`The server named an odd package version: ${version}`);
  const files = {
    [`.dcms/packages/dcms.functions.sdk.${version}.nupkg`]: nupkg,
    '.dcms/version.props': `<Project>\n  <PropertyGroup>\n    <DcmsFunctionsSdkVersion>${version}</DcmsFunctionsSdkVersion>\n  </PropertyGroup>\n</Project>\n`,
  };
  if (!exists('Functions.csproj')) {
    files['Functions.csproj'] = `<Project Sdk="Microsoft.NET.Sdk">
  <!-- A desktop build of this repository's C# functions (dcms sdk). The server builds from the
       files under functions/ and lib/, not from this project, and checks them the same way. -->
  <Import Project=".dcms/version.props" Condition="Exists('.dcms/version.props')" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Dcms.Functions.Sdk" Version="$(DcmsFunctionsSdkVersion)" />
  </ItemGroup>
</Project>
`;
  }
  if (!exists('nuget.config')) {
    files['nuget.config'] = `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="dcms" value=".dcms/packages" />
  </packageSources>
</configuration>
`;
  }
  return files;
}

/** `--input '{"a":1}'` or `--input @file.json`. */
export function parseInput(value, read = (p) => readFileSync(p, 'utf8')) {
  if (value === undefined) return null;
  const text = value.startsWith('@') ? read(value.slice(1)) : value;
  try {
    return JSON.parse(text);
  } catch {
    throw new Error('--input must be JSON, or @file with JSON in it.');
  }
}

/** A build problem as editors and terminals link it: file:line:column: message. */
export function formatProblem(p) {
  const where = p.file ? `${p.file}${p.line ? `:${p.line}${p.column ? `:${p.column}` : ''}` : ''}: ` : '';
  return `${where}${p.message}${p.code ? ` (${p.code})` : ''}`;
}
