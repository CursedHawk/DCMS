// Site builds: rebuild a site's release tree and wait for the build to succeed.
//
// RUN THIS ALONE. SitePublishConsumer's concurrency is DCMS_BUILD_CONCURRENCY (default 1),
// and a Mode B build gets a sandbox container with DCMS_BUILD_CPUS=2 and DCMS_BUILD_MEM=2g
// (SandboxOptions.cs). Two concurrent Mode B builds therefore claim four CPUs and four
// gigabytes -- the entire host. That is the hypothesis: not that a build is slow, but that
// two of them plus anything else is more than the box has.
//
// --- The three modes ---
//
// BUILD_MODE selects which fixture site each VU republishes, and the three are different
// programs, not three sizes of one:
//
//   StaticFiles    (Mode C, default) unzip an already-staged bundle into the artifact
//                  prefix. No git, no toolchain. This is the FLOOR: everything a publish
//                  costs when the build itself is nearly free -- queue wait, consumer
//                  dispatch, MinIO writes, activation, the SitePublished fan-out.
//
//   StaticPrerender (Mode A) read the committed HTML/CSS out of Forgejo and assemble the
//                  pages in C#. Adds a git read and work proportional to page count, still
//                  with no Node anywhere. The gap from Mode C is the assembler's real cost.
//
//   ReactApp       (Mode B) materialize the project and run its own package install and
//                  vite build inside a per-build sandbox container. Minutes, not seconds,
//                  and the only mode that competes for the host's CPU with everything else
//                  running on it. Requires seed.modeB=true at provision time.
//
// Set BUILD_MODE=all to round-robin across whichever modes the fixtures actually have,
// which is the run that shows contention BETWEEN pipelines rather than within one.
//
// VUs are builds in flight. Above the consumer's concurrency the extra ones simply queue,
// which is the point: site_publish_to_live_ms is queue wait plus work, and
// dcms_site_build_duration_seconds in the evidence bundle is the work alone. The gap
// between them is the wait.

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { pickTenant, thresholds } from '../lib/k6.js';

const BASE = __ENV.ADMIN_BASE;
const TOKEN = __ENV.ACCESS_TOKEN;
const MODE = __ENV.BUILD_MODE || 'StaticFiles';

// One trend per mode as well as the combined one. Averaging a 4-second Mode C extract with
// a 4-minute Mode B install produces a number that describes neither, and BUILD_MODE=all
// would otherwise do exactly that.
const publishToLive = new Trend('site_publish_to_live_ms', true);
const byMode = {
  StaticFiles: new Trend('site_publish_to_live_ms_static_files', true),
  StaticPrerender: new Trend('site_publish_to_live_ms_prerender', true),
  ReactApp: new Trend('site_publish_to_live_ms_react', true),
};
const failedBuilds = new Counter('site_build_failed');
const stuck = new Counter('site_build_never_finished');
const missingFixture = new Counter('site_build_no_fixture');

// Mode B legitimately takes minutes: ReactAppBuilder allows 10 for install and 8 for the
// build, so a give-up shorter than that would report a working pipeline as wedged.
const GIVE_UP_MS = Number(__ENV.BUILD_TIMEOUT_MS || (MODE === 'StaticFiles' ? 300000 : 1200000));
const POLL_MS = Number(__ENV.BUILD_POLL_MS || 2000);

export const options = {
  scenarios: {
    sitebuild: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 2),
      duration: __ENV.DURATION || '5m',
    },
  },
  thresholds: thresholds({
    'site_build_failed': ['count==0'],
    'site_build_never_finished': ['count==0'],
    // A run that found no fixture for the requested mode measured nothing, and must not
    // pass quietly. Seed with -e seed.modeB=true for ReactApp.
    'site_build_no_fixture': ['count==0'],
  }),
};

export function setup() {
  if (!TOKEN) fail('ACCESS_TOKEN is empty; run this through loadtest/run.sh');
  const known = ['StaticFiles', 'StaticPrerender', 'ReactApp', 'all'];
  if (!known.includes(MODE)) fail(`BUILD_MODE must be one of ${known.join(', ')} (got '${MODE}')`);
}

/** The fixture site this iteration builds, and the mode it is in. */
function target(tenant, iteration) {
  const byName = tenant.sitesByMode ?? { StaticFiles: tenant.site };
  if (MODE !== 'all') return { mode: MODE, site: byName[MODE] };

  // Round-robin over the modes this tenant actually has, so `all` degrades to whatever
  // was seeded rather than failing on the mode that was not.
  const present = ['StaticFiles', 'StaticPrerender', 'ReactApp'].filter((m) => byName[m]);
  const mode = present[iteration % present.length];
  return { mode, site: byName[mode] };
}

export default function () {
  // One site per VU where possible: two VUs republishing the same site would serialize on
  // that site rather than on the builder, which is not the thing being measured.
  const tenant = pickTenant(__VU % 1000);
  const { mode, site } = target(tenant, __ITER);
  if (!site) {
    missingFixture.add(1);
    sleep(1);
    return;
  }

  const auth = { Authorization: `Bearer ${TOKEN}`, 'X-Dcms-Tenant': tenant.slug };
  const siteId = site.siteId;

  const startedAt = Date.now();

  // For the git-backed modes this is `POST .../builds`, which rebuilds the current release
  // tree and returns a build id. Publishing instead would try to merge a source branch into
  // release, find nothing to merge on the second iteration, and return `upToDate` with no
  // build at all -- so the scenario would measure one build and then nothing.
  const url = mode === 'StaticFiles'
    ? `${BASE}/api/admin/sites/${siteId}/publish`
    : `${BASE}/api/admin/sites/${siteId}/builds`;

  const requested = http.post(url, '{}', {
    headers: { ...auth, 'Content-Type': 'application/json' },
    tags: { step: 'publish', mode },
  });
  if (!check(requested, { 'build accepted': (r) => r.status === 200 || r.status === 202 })) return;

  const buildId = requested.json('buildId');
  if (!buildId) { failedBuilds.add(1); return; }

  for (;;) {
    if (Date.now() - startedAt > GIVE_UP_MS) { stuck.add(1); break; }

    const builds = http.get(`${BASE}/api/admin/sites/${siteId}/builds?limit=10`, {
      headers: auth, tags: { step: 'poll', mode },
    });
    if (builds.status !== 200) { sleep(POLL_MS / 1000); continue; }

    const build = (builds.json() || []).find((b) => b.id === buildId);
    if (build?.status === 'Succeeded') {
      const ms = Date.now() - startedAt;
      publishToLive.add(ms);
      byMode[mode]?.add(ms);
      break;
    }
    if (build?.status === 'Failed') { failedBuilds.add(1); break; }
    sleep(POLL_MS / 1000);
  }
}
