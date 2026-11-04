#!/usr/bin/env node
// Deploys the HTTP functions the `functions` scenario calls, in the Dynamic Apps stress tenant.
//
//   node loadtest/seed/functions.mjs --env vps1            # tenant da-stress, app fnload
//   node loadtest/seed/functions.mjs --env vps1 -e fn.tenant=other
//
// Idempotent: the app is adopted if it exists, and an unchanged repository ships nothing new.
// Everything goes through functions.code@1, as the console's assistant would do it: write to
// main, ship to release (which builds), deploy the build.

import { loadProfile, parseArgs, targets } from '../lib/config.mjs';
import { edgeSession } from '../lib/auth.mjs';
import { AdminApi } from '../lib/api.mjs';

const log = (...args) => console.log('[fn-seed]', ...args);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const FILES = {
  // The sandbox and the hop, and nothing else: what every HTTP call costs.
  'functions/ping.ts': `import { defineFunction } from "@dcms/functions";
export default defineFunction({
  description: "Load test: answers at once.",
  access: { http: ["public"] },
  handler: (input: { n?: string }) => ({ ok: true, n: input.n ?? null }),
});
`,
  // A little CPU in the sandbox, to see where the per-tenant cap starts to queue.
  'functions/work.ts': `import { defineFunction } from "@dcms/functions";
export default defineFunction({
  description: "Load test: about 50 ms of work.",
  access: { http: ["public"] },
  handler: () => {
    const until = Date.now() + 50;
    let x = 0;
    while (Date.now() < until) x++;
    return { ok: true, x };
  },
});
`,
  // The same two in C# (ADR 0030): .NET's WebAssembly runtime in the same sandbox.
  'functions/CsPing.cs': `namespace LoadTest;

public record PingInput(string? N);

[Function("cs-ping", Description = "Load test: answers at once, in C#.")]
[Http(Access.Public)]
public sealed class CsPing : Function<PingInput, object>
{
    public override Task<object> Run(PingInput input, Context ctx) => Task.FromResult<object>(new { ok = true, n = input?.N });
}
`,
  'functions/CsWork.cs': `namespace LoadTest;

[Function("cs-work", Description = "Load test: about 50 ms of work, in C#.")]
[Http(Access.Public)]
public sealed class CsWork : Function<JsonNode, object>
{
    public override Task<object> Run(JsonNode input, Context ctx)
    {
        var until = DateTime.UtcNow.AddMilliseconds(50);
        long x = 0;
        while (DateTime.UtcNow < until) x++;
        return Task.FromResult<object>(new { ok = true, x });
    }
}
`,
};

async function main() {
  const { env, overrides } = parseArgs(process.argv.slice(2));
  const profile = await loadProfile(env, { overrides });
  const t = targets(profile);
  const tenant = profile.fn?.tenant ?? 'da-stress';
  const slug = profile.fn?.slug ?? 'fnload';
  const session = await edgeSession({ admin: t.admin, username: profile.auth.username, password: profile.auth.password });
  const api = new AdminApi({ base: t.admin, session }).forTenant(tenant);
  const code = (op, body = {}) => api.post(`/api/admin/contracts/functions.code@1/${op}?instance=${slug}&plane=admin`, body);

  if (!(await api.get('/api/admin/plugins/instances')).some((x) => x.slug === slug)) {
    const created = await api.post('/api/admin/plugins/instances', { pluginId: 'functions', slug, name: 'Load test functions' });
    await api.post(`/api/admin/plugins/instances/${created.id}/enable`, undefined, { expect: [200, 204, 409] });
    log(`created ${tenant}/${slug}`);
  }

  const written = await code('WriteFiles', { message: 'Load test functions', put: FILES });
  const shipped = await code('Ship', {});
  log(`main ${written.sha?.slice(0, 7)}, release ${shipped.sha?.slice(0, 7)} (${shipped.upToDate ? 'up to date' : 'merged'})`);

  // The build of release's head: wait for it, then make it live.
  const sha = shipped.sha?.slice(0, 7);
  for (let i = 0; i < 90; i++) {
    const overview = await code('Overview');
    const build = overview.builds.find((b) => b.commit === sha);
    if (build?.status === 'Active') { log(`deployment ${build.number} is already live`); return; }
    if (build?.status === 'Failed') throw new Error(`build ${build.number} failed: ${build.error}`);
    if (build?.status === 'Ready') {
      const live = await code('Deploy', { number: build.number });
      log(`deployment ${live.number} is ${live.status}: GET|POST /api/${slug}/fn/ping|work|cs-ping|cs-work on ${tenant}`);
      return;
    }
    await sleep(2000);
  }
  throw new Error(`no build of ${sha} after 3 minutes`);
}

main().catch((e) => { console.error('[fn-seed]', e.message); process.exit(1); });
