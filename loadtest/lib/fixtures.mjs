// The handoff between the Node seeder and the k6 scenarios.
//
// k6 cannot log in (see auth.mjs) and cannot upload fixture media efficiently, so the
// seeder does that work once and writes down what it made. Scenarios read this file
// through k6's SharedArray, which parses it once per run rather than once per VU — at
// 50 VUs the difference is 50 copies of the tenant list in memory versus one.

import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { LOADTEST_ROOT } from './config.mjs';

export const fixturesPath = (profileName) =>
  join(LOADTEST_ROOT, 'fixtures', `${profileName}.json`);

export async function writeFixtures(profileName, fixtures) {
  const path = fixturesPath(profileName);
  await mkdir(dirname(path), { recursive: true });
  const payload = { ...fixtures, profile: profileName, writtenAt: new Date().toISOString() };
  await writeFile(path, JSON.stringify(payload, null, 2));
  return path;
}

export async function readFixtures(profileName) {
  const path = fixturesPath(profileName);
  try {
    return JSON.parse(await readFile(path, 'utf8'));
  } catch (cause) {
    throw new Error(
      `No fixtures for profile '${profileName}'. Run: node loadtest/seed/provision.mjs --env ${profileName}`,
      { cause });
  }
}
