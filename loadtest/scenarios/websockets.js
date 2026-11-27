// The WebSockets plugin's site hub (ADR 0037): many browsers holding /hub/ws open, each in a
// room of a keyed channel, publishing now and then. What it answers is what a connection costs
// content-api (watch its memory while the plateau holds) and how long a message takes from one
// browser to the others in its room.
//
// Needs a WebSockets app on the target site with a keyed channel browsers may publish on, e.g.
// { "name": "chat", "keyed": true, "subscribe": "public", "publish": "subscribers" }.
//
//   docker run --rm -i -v "$PWD/loadtest:/lt" grafana/k6 run \
//     -e WS_HOST=test.highgeek.eu -e WS_INSTANCE=rt -e WS_VUS=500 /lt/scenarios/websockets.js
//
// The edge allows 1200 requests a minute per IP and every connection starts with one, so the
// ramp spreads the upgrades out; a connection lasts WS_HOLD seconds and then reconnects.

import ws from 'k6/ws';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const HOST = __ENV.WS_HOST;
const INSTANCE = __ENV.WS_INSTANCE || 'rt';
const CHANNEL = __ENV.WS_CHANNEL || 'chat';
const VUS = Number(__ENV.WS_VUS || 500);
const HOLD = Number(__ENV.WS_HOLD || 120);
const ROOMS = Number(__ENV.WS_ROOMS || 25);
const RS = '\u001e';

const delivered = new Counter('ws_messages_received');
const published = new Counter('ws_messages_published');
const refused = new Counter('ws_invocation_errors');
const latency = new Trend('ws_delivery_ms', true);

export const options = {
  scenarios: {
    hub: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: __ENV.WS_RAMP || '90s', target: VUS },
        { duration: __ENV.WS_PLATEAU || '3m', target: VUS },
        { duration: '15s', target: 0 },
      ],
      gracefulRampDown: '10s',
    },
  },
  thresholds: {
    checks: ['rate>0.99'],
    ws_delivery_ms: [`p(95)<${__ENV.WS_P95 || 500}`],
    ws_invocation_errors: ['count<10'],
  },
};

export default function () {
  if (!HOST) throw new Error('WS_HOST is required');
  const room = `load-${__VU % ROOMS}`;
  let id = 0;
  const frame = (m) => JSON.stringify(m) + RS;

  const res = ws.connect(`wss://${HOST}/hub/ws?instance=${INSTANCE}`, { headers: { Origin: `https://${HOST}` } }, (socket) => {
    socket.on('open', () => {
      socket.send(frame({ protocol: 'json', version: 1 }));
      socket.send(frame({ type: 1, invocationId: String(++id), target: 'Subscribe', arguments: [CHANNEL, room] }));
      // Spread the publishes: a room of ~20 hears one message every half second or so.
      socket.setTimeout(() => {
        socket.setInterval(() => {
          socket.send(frame({ type: 1, invocationId: String(++id), target: 'Publish', arguments: [CHANNEL, room, { text: `${Date.now()}` }] }));
          published.add(1);
        }, 10000);
      }, Math.random() * 10000);
      socket.setTimeout(() => socket.close(), HOLD * 1000);
    });
    socket.on('message', (data) => {
      for (const part of data.split(RS)) {
        if (!part) continue;
        const m = JSON.parse(part);
        if (m.type === 1 && m.target === 'message') {
          delivered.add(1);
          latency.add(Date.now() - Number(m.arguments[2].text));
        } else if (m.type === 3 && m.error) {
          refused.add(1);
        } else if (m.type === 6) {
          socket.send(frame({ type: 6 })); // keep-alive ping back
        } else if (m.type === 7) {
          socket.close();
        }
      }
    });
  });
  check(res, { 'upgraded (101)': (r) => r && r.status === 101 });
}
