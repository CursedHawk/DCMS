// The media pipeline: upload an image and wait for media-worker to finish with it.
//
// RUN THIS ALONE, at least the first time. On a four-core host the image consumer runs
// MaxAckPending=4 (src/Services/Dcms.MediaWorker/ImageProcessingConsumer.cs:28) and each
// job re-encodes through ImageSharp and writes a webp ladder to MinIO, so a handful of
// VUs is enough to saturate the box. Adding a delivery scenario on top of that measures
// contention, which is a later question.
//
// What it measures is queue latency, not request latency. The upload returns as soon as
// admin-api has stored the original and dispatched media.process.image; the interesting
// number is how long the asset then sits at status Processing. If upload stays fast while
// that gap grows with VU count, the bottleneck is worker concurrency and not the API.
//
// Uploads rotate three widths because the ladder skips rungs above the source: a 240px
// image generates almost nothing, a 1920px one generates the full 320/640/1280/1920 set
// plus a thumbnail. Averaging them together would hide that difference.

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { pickTenant, thresholds } from '../lib/k6.js';

const BASE = __ENV.ADMIN_BASE;
const TOKEN = __ENV.ACCESS_TOKEN;
const PROFILE = __ENV.PROFILE || 'local';

// open() is init-context only and returns an ArrayBuffer for binary reads. The seeder
// wrote these; they are not in git.
const images = [
  { name: 'small.png', bytes: open(`../fixtures/assets/small.png`, 'b'), width: 240 },
  { name: 'medium.png', bytes: open(`../fixtures/assets/medium.png`, 'b'), width: 1280 },
  { name: 'large.png', bytes: open(`../fixtures/assets/large.png`, 'b'), width: 1920 },
];

// Measured from the moment the upload RESPONSE lands, not from when it was sent.
// admin-api stores the original and dispatches media.process.image before responding, so
// this is exactly the worker's queue-plus-processing time -- which is the thing under
// test. Folding the upload in would charge the worker for the load generator's uplink,
// and at ~4 MB a request that is most of the number.
const queueToReady = new Trend('media_queue_to_ready_ms', true);
const stuck = new Counter('media_never_ready');

const GIVE_UP_MS = Number(__ENV.MEDIA_TIMEOUT_MS || 120000);

export const options = {
  scenarios: {
    media: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 4),
      duration: __ENV.DURATION || '3m',
    },
  },
  thresholds: thresholds({
    'http_req_duration{step:upload}': [`p(95)<${__ENV.MEDIA_UPLOAD_P95 || 5000}`],
    'media_queue_to_ready_ms': [`p(95)<${__ENV.MEDIA_READY_P95 || 60000}`],
    'media_never_ready': ['count==0'],
  }),
};

export function setup() {
  if (!TOKEN) fail('ACCESS_TOKEN is empty; run this through loadtest/run.sh');
}

export default function () {
  const tenant = pickTenant(__ITER);
  const image = images[__ITER % images.length];
  const auth = { Authorization: `Bearer ${TOKEN}`, 'X-Dcms-Tenant': tenant.slug };

  const uploaded = http.post(`${BASE}/api/admin/media`, {
    file: http.file(image.bytes, `loadtest-${__VU}-${__ITER}-${image.name}`, 'image/png'),
  }, { headers: auth, tags: { step: 'upload', width: String(image.width) } });

  if (!check(uploaded, { 'upload 200/201': (r) => r.status === 200 || r.status === 201 })) return;
  const id = uploaded.json('id');
  const acceptedAt = Date.now();

  // Poll the asset until the worker marks it Ready. This is the queue, not the API.
  for (;;) {
    if (Date.now() - acceptedAt > GIVE_UP_MS) { stuck.add(1); break; }

    const asset = http.get(`${BASE}/api/admin/media/${id}`, {
      headers: auth, tags: { step: 'poll' },
    });
    const status = asset.status === 200 ? asset.json('status') : null;

    if (status === 'Ready') {
      queueToReady.add(Date.now() - acceptedAt, { width: String(image.width) });
      break;
    }
    if (status === 'Failed') {
      // A worker that refuses the fixture is a harness bug, not a performance finding,
      // and must not be averaged into the latency trend.
      fail(`media asset ${id} failed processing: ${asset.json('error')}`);
    }
    sleep(0.5);
  }
}
