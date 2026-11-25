#!/usr/bin/env node
/**
 * Layer 4 CI validator (offline, zero API calls, zero npm dependencies):
 *  - Parses every scenario under `e2e/midscene/scenarios/*.yaml`
 *  - Verifies required metadata, unique IDs, supported platforms, and valid Midscene actions
 *  - Verifies every `parity_ids` reference exists in `docs/parity.yaml`
 *  - Verifies every `fixtures` path exists under `fixtures/`
 *  - Verifies every scenario includes at least one `aiAssert` step
 *  - Dry-renders every scenario for all of its target platforms
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseScenarioYaml, renderMidsceneYaml } from './run-midscene.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const MIDSCENE_DIR = path.resolve(HERE, '..');
const ROOT = path.resolve(MIDSCENE_DIR, '..', '..');
const SCENARIOS_DIR = path.join(MIDSCENE_DIR, 'scenarios');
const PARITY_PATH = path.join(ROOT, 'docs', 'parity.yaml');

const ALLOWED_ACTIONS = new Set(['ai', 'aiAct', 'aiAssert', 'aiQuery', 'aiWaitFor', 'sleep']);
const ALLOWED_PLATFORMS = new Set(['windows', 'macos', 'android', 'ios', 'web']);

function loadParityIds() {
  const raw = fs.readFileSync(PARITY_PATH, 'utf8');
  const ids = new Set();
  for (const line of raw.split(/\r?\n/)) {
    const m = /^\s{2}-\s+id:\s+(.+?)\s*$/.exec(line);
    if (m) ids.add(m[1].replace(/^['"]|['"]$/g, ''));
  }
  return ids;
}

function main() {
  const parityIds = loadParityIds();
  const files = fs
    .readdirSync(SCENARIOS_DIR)
    .filter((f) => f.endsWith('.yaml'))
    .sort();

  const errors = [];
  const seenIds = new Set();
  let totalSteps = 0;
  let totalAsserts = 0;

  if (!files.length) errors.push('No Midscene scenarios found under e2e/midscene/scenarios/');

  for (const file of files) {
    const full = path.join(SCENARIOS_DIR, file);
    let sc;
    try {
      sc = parseScenarioYaml(fs.readFileSync(full, 'utf8'), file);
    } catch (e) {
      errors.push(e.message);
      continue;
    }

    if (!sc.id) errors.push(`${file}: missing id`);
    if (seenIds.has(sc.id)) errors.push(`${file}: duplicate id "${sc.id}"`);
    seenIds.add(sc.id);
    if (!sc.title) errors.push(`${file}: missing title`);
    if (!sc.testing_ref) errors.push(`${file}: missing testing_ref`);
    if (!sc.parity_ids.length) errors.push(`${file}: must reference at least one parity_ids entry`);
    if (!sc.platforms.length) errors.push(`${file}: must specify at least one platform`);
    if (!sc.tasks.length) errors.push(`${file}: must define at least one task`);

    for (const pid of sc.parity_ids) {
      if (!parityIds.has(pid)) {
        errors.push(`${file}: parity_id "${pid}" does not exist in docs/parity.yaml`);
      }
    }
    for (const rel of sc.fixtures) {
      const abs = path.join(ROOT, 'fixtures', rel);
      if (!fs.existsSync(abs)) {
        errors.push(`${file}: missing fixture fixtures/${rel}`);
      }
    }
    for (const p of sc.platforms) {
      if (!ALLOWED_PLATFORMS.has(p)) {
        errors.push(`${file}: unknown platform "${p}"`);
        continue;
      }
      const rendered = renderMidsceneYaml(sc, p, {});
      if (!rendered.includes('tasks:') || !rendered.includes('flow:')) {
        errors.push(`${file}: rendered Midscene YAML for ${p} missing tasks/flow`);
      }
    }

    let assertsInScenario = 0;
    for (const t of sc.tasks) {
      if (!t.name) errors.push(`${file}: task missing name`);
      if (!t.flow.length) errors.push(`${file}: task "${t.name}" has empty flow`);
      for (const step of t.flow) {
        totalSteps++;
        if (!ALLOWED_ACTIONS.has(step.action)) {
          errors.push(`${file}:${step.line}: unsupported Midscene action "${step.action}"`);
        }
        if (step.action === 'sleep') {
          if (!Number.isFinite(step.value) || step.value <= 0) {
            errors.push(`${file}:${step.line}: sleep must be a positive number of milliseconds`);
          }
        } else if (!String(step.value).trim()) {
          errors.push(`${file}:${step.line}: empty prompt for ${step.action}`);
        }
        if (step.action === 'aiAssert') {
          assertsInScenario++;
          totalAsserts++;
        }
      }
    }
    if (assertsInScenario === 0) {
      errors.push(`${file}: scenario must contain at least one aiAssert`);
    }
  }

  console.log(
    `Midscene E2E suite: ${files.length} shared scenarios, ${totalSteps} steps (${totalAsserts} aiAssert checks)`,
  );
  if (errors.length) {
    console.error(`\nMidscene flow validation FAILED (${errors.length} issue${errors.length === 1 ? '' : 's'}):`);
    for (const e of errors) console.error(`  - ${e}`);
    process.exit(1);
  }
  console.log('Midscene flow validation PASSED.');
}

main();
