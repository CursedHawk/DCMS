import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, statSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import {
  authOrigin,
  csharpFiles,
  hasCSharp,
  editorFiles,
  formatProblem,
  ignoreDcms,
  keptTokens,
  parseInput,
  readCredentials,
  readLink,
  writeLink,
  workingFiles,
  writeCredentials,
  writeFiles,
} from '../src/lib.mjs';

const temp = () => mkdtempSync(join(tmpdir(), 'dcms-cli-'));

test('sign-in is on the auth host beside the console', () => {
  assert.equal(authOrigin('https://admin.highgeek.eu/x'), 'https://auth.highgeek.eu');
  assert.equal(authOrigin('http://localhost:8080'), 'http://localhost:8080');
});

test('credentials are private to the user and a reply without a refresh token keeps the old one', () => {
  const path = join(temp(), 'dcms', 'credentials.json');
  writeCredentials({ accessToken: 'a', refreshToken: 'r' }, path);
  assert.equal(statSync(path).mode & 0o777, 0o600);
  const next = { ...readCredentials(path), ...keptTokens({ access_token: 'b', expires_in: 60 }, 0) };
  assert.deepEqual(next, { accessToken: 'b', refreshToken: 'r', expiresAt: 60_000 });
});

test('the working tree is what git does not ignore, less .dcms and binaries', () => {
  const root = temp();
  execFileSync('git', ['init', '-q'], { cwd: root });
  writeFiles(root, { 'functions/a.ts': 'export {}\n', '.dcms/functions.ts': 'x', 'dist/out.js': 'y', '.gitignore': 'dist/\n' });
  writeFileSync(join(root, 'logo.png'), Buffer.from([137, 80, 0, 1]));
  assert.deepEqual(Object.keys(workingFiles(root)).sort(), ['functions/a.ts']);
});

test('.dcms/ is ignored once', () => {
  const root = temp();
  writeFileSync(join(root, '.gitignore'), 'node_modules');
  assert.equal(ignoreDcms(root), true);
  assert.equal(ignoreDcms(root), false);
  assert.equal(readFileSync(join(root, '.gitignore'), 'utf8'), 'node_modules\n.dcms/\nbin/\nobj/\n');
});

test('editor config sees the SDK and the runtime the server builds against', () => {
  const files = editorFiles();
  const deno = JSON.parse(files['deno.json']);
  assert.equal(deno.imports['@dcms/functions'], './.dcms/functions.ts');
  assert.deepEqual(deno.compilerOptions.lib, ['esnext', 'webworker']);
  assert.deepEqual(JSON.parse(files['tsconfig.json']).compilerOptions.paths['@dcms/functions'], ['./.dcms/functions.ts']);
});

test('input is JSON inline or from a file', () => {
  assert.equal(parseInput(undefined), null);
  assert.deepEqual(parseInput('{"a":1}'), { a: 1 });
  assert.deepEqual(parseInput('@in.json', () => '[2]'), [2]);
  assert.throws(() => parseInput('nope'), /must be JSON/);
});

test('problems print as file:line:column', () => {
  assert.equal(formatProblem({ file: 'functions/a.ts', line: 3, column: 7, message: 'Bad.', code: 'TS2322' }), 'functions/a.ts:3:7: Bad. (TS2322)');
  assert.equal(formatProblem({ message: 'Nothing to build.' }), 'Nothing to build.');
});

test('a C# repository gets the server\'s SDK package, and a project and source only when it has none', () => {
  const root = temp();
  execFileSync('git', ['init', '-q'], { cwd: root });
  writeFiles(root, { 'functions/a.ts': 'export {}\n' });
  assert.equal(hasCSharp(root), false);
  writeFiles(root, { 'functions/Ping.cs': 'class Ping {}\n' });
  assert.equal(hasCSharp(root), true);

  const fresh = csharpFiles('1.0.0-sdk.abc', new Uint8Array([1]), () => false);
  assert.deepEqual(Object.keys(fresh).sort(), ['.dcms/packages/dcms.functions.sdk.1.0.0-sdk.abc.nupkg', '.dcms/version.props', 'Functions.csproj', 'nuget.config']);
  assert.match(fresh['.dcms/version.props'], /<DcmsFunctionsSdkVersion>1.0.0-sdk.abc</);
  assert.deepEqual(Object.keys(csharpFiles('1.0.0-sdk.abc', new Uint8Array([1]), () => true)).length, 2);
  assert.deepEqual(writeFiles(root, { 'bytes.bin': new Uint8Array([0, 1]) }), ['bytes.bin']);
  assert.deepEqual(writeFiles(root, { 'bytes.bin': new Uint8Array([0, 1]) }), []);
});

test('a clone cannot point a read or a write outside itself, or a command at another app', () => {
  const root = temp();
  const outside = temp();
  writeFileSync(join(outside, 'id_rsa'), 'secret');
  execFileSync('git', ['init', '-q'], { cwd: root });
  writeFiles(root, { 'functions/a.ts': 'export {}\n', '.env': 'KEY=1\n', 'README.md': '# x\n' });
  symlinkSync(join(outside, 'id_rsa'), join(root, 'functions', 'key.ts'));
  symlinkSync(outside, join(root, 'lib'));
  assert.deepEqual(Object.keys(workingFiles(root)), ['functions/a.ts']);

  symlinkSync(join(outside, 'id_rsa'), join(root, 'deno.json'));
  assert.throws(() => writeFiles(root, { 'deno.json': '{}' }), /symbolic link/);
  assert.throws(() => writeFiles(root, { 'lib/x.ts': '' }), /symbolic link/);
  assert.equal(readFileSync(join(outside, 'id_rsa'), 'utf8'), 'secret');

  // The link lives with the sign-in, keyed by the clone's real path; the clone cannot supply one.
  const env = { XDG_CONFIG_HOME: temp() };
  mkdirSync(join(root, '.dcms'));
  writeFileSync(join(root, '.dcms', 'link.json'), '{"app":"other"}');
  assert.throws(() => readLink(root, env), /not linked/);
  const link = { tenant: 'acme', environment: 'production', app: 'fn', repoId: '0b0e7c2e-1111-4222-8333-944445555666' };
  writeLink(root, link, env);
  assert.equal(readLink(root, env).app, 'fn');
  assert.throws(() => writeLink(root, { ...link, repoId: '../x' }, env), /does not understand/);

  symlinkSync(join(outside, 'id_rsa'), join(root, '.gitignore'));
  assert.throws(() => ignoreDcms(root), /symbolic link/);
  assert.equal(readFileSync(join(outside, 'id_rsa'), 'utf8'), 'secret');
});
