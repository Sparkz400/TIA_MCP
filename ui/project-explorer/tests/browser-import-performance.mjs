/** Repeatable portable-file import acceptance. Never claims native/desktop-host acceptance. */
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { pack } from '../pack.mjs';
import { observeImports, importSnapshot, meetsViewportTarget } from './import-measurement.mjs';

const require = createRequire(import.meta.url), { scaleFixture, oracleFixture } = require('./relationship-fixture.cjs');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const argument = name => { const index = process.argv.indexOf(name); return index < 0 ? null : process.argv[index + 1]; };
const output = path.resolve(argument('--output') || path.join(root, 'artifacts/explorer-import', new Date().toISOString().replace(/[:.]/g, '-')));
const conditions = argument('--conditions');
assert.ok(conditions && !conditions.startsWith('--'), '--conditions must record contention and machine conditions');
const trials = Number(argument('--trials') || 10);
assert.ok(Number.isSafeInteger(trials) && trials >= 1 && trials <= 100, 'Use 1–100 trials; acceptance requires at least ten');
await fs.mkdir(path.dirname(output), { recursive: true });
await fs.mkdir(output); // Earlier misses and failures cannot be overwritten.
let playwright;
try { playwright = require('playwright'); }
catch { playwright = require(process.env.PLAYWRIGHT_MODULE || path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')); }
const executablePath = process.env.EXPLORER_BROWSER || playwright.chromium.executablePath();
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const sources = {};
for (const file of ['model.js', 'client.js', 'explorer.js', 'style.css', 'index.template.html', 'pack.mjs', 'tests/browser-import-performance.mjs', 'tests/import-measurement.mjs', 'tests/relationship-fixture.cjs', 'tests/fixture.cjs']) {
  sources[file] = hash(await fs.readFile(path.join(root, 'ui/project-explorer', file)));
}
const fixture = scaleFixture(50000, true), fixtureBytes = Buffer.from(JSON.stringify(fixture));
const file = path.join(output, '50000-nodes-50000-edges.json');
await fs.writeFile(file, fixtureBytes, { flag: 'wx' });
const viewer = argument('--viewer') ? path.resolve(argument('--viewer')) : path.join(output, 'viewer.html');
if (!argument('--viewer')) await pack(null, viewer);
const report = {
  schemaVersion: 'project-explorer-import-performance.v1', startedAtUtc: new Date().toISOString(), passed: false,
  scope: { portableFileImport: true, nativeCapture: false, nativeCompleteness: false, desktopMcpAppsHost: false },
  conditions, machine: { platform: process.platform, release: os.release(), cpu: os.cpus()[0]?.model, logicalCpuCount: os.cpus().length, totalMemoryBytes: os.totalmem(), freeMemoryAtStartBytes: os.freemem(), node: process.version },
  methodology: {
    cold: 'One new browser process, context and page per trial. OS file cache is uncontrolled; this is browser-process cold, not disk cold.',
    warm: 'Second import of the same file in the same page. Includes file read, JSON parsing, validation and indexing again.',
    interval: 'Capture-phase file-input change listener to two requestAnimationFrame callbacks after the actual Explorer load resolves. File chooser interaction and browser startup are excluded. No dataset is injected via evaluate().',
    acceptance: 'At least ten paired cold/warm trials; every import strictly below 1000ms, 1–69 tree DOM rows, all independent oracles/refusals pass; no page errors or HTTP(S) requests. No outliers discarded.',
    observer: 'Test-only load/error wrappers call the unchanged production methods. No profiler or CPU sampler during acceptance.',
  },
  sources, viewer: { path: viewer, sha256: hash(await fs.readFile(viewer)) }, fixture: { path: file, sha256: hash(fixtureBytes), bytes: fixtureBytes.length, nodes: 50000, edges: 50000 },
  trials: [], checks: [], negativeControls: [], pageErrors: [], networkRequests: [],
};
const check = name => report.checks.push(name);
const expectedEdges = [...Array.from({ length: 499 }, (_, i) => 'back-' + String(i + 1).padStart(3, '0')),
  ...Array.from({ length: 499 }, (_, i) => 'out-' + String(i + 1).padStart(3, '0')), 'unresolved-1', 'unresolved-2'];
function verifyGraph(graph) {
  assert.deepEqual(graph.edges.map(edge => edge.edgeId).sort(), expectedEdges);
  assert.deepEqual(graph.nodes.map(node => node.nodeId).sort(), ['root', ...Array.from({ length: 499 }, (_, i) => 'n' + (i + 1))].sort());
  assert.deepEqual(graph.unresolved.map(edge => edge.edgeId).sort(), ['unresolved-1', 'unresolved-2']);
  assert.equal(graph.truncated, true);
  assert.deepEqual(graph.relationshipScope.stopReasons, ['edges']);
  assert.equal(graph.relationshipScope.projectCompleteness, 'notEstablished');
  const originals = new Map(fixture.edges.map(edge => [edge.edgeId, edge]));
  for (const edge of graph.edges) assert.deepEqual(edge, originals.get(edge.edgeId));
}
let browser;
try {
  // The target comparator itself must reject the exact boundary and missing measurements.
  for (const run of [{ firstViewportMs: 1000, renderedRows: 20 }, { firstViewportMs: NaN, renderedRows: 20 }, { renderedRows: 20 }, { firstViewportMs: 10 }, { firstViewportMs: 10, renderedRows: 70 }, { firstViewportMs: 10, renderedRows: 20, error: 'invalid_edge' }]) assert.equal(meetsViewportTarget(run), false);
  report.negativeControls.push('Target rejects 1000ms, nonfinite/missing timing or rows, 70 rows and failed imports');
  for (let trial = 1; trial <= trials; trial++) {
    browser = await playwright.chromium.launch({ headless: true, executablePath, args: ['--allow-file-access-from-files'] });
    report.browser = { version: browser.version(), executablePath, viewport: { width: 1440, height: 1000 }, headless: true };
    const page = await browser.newPage({ viewport: report.browser.viewport, colorScheme: 'light' });
    page.on('pageerror', error => report.pageErrors.push(error.message));
    page.on('request', request => { if (/^https?:/.test(request.url())) report.networkRequests.push(request.url()); });
    await page.goto(pathToFileURL(viewer).href);
    await page.waitForFunction(() => !!globalThis.projectExplorer);
    await observeImports(page);
    const row = { trial, startedAtUtc: new Date().toISOString(), freeMemoryBytes: os.freemem() };
    report.trials.push(row);
    row.cold = await importSnapshot(page, file);
    row.warm = await importSnapshot(page, file);
    row.passed = meetsViewportTarget(row.cold) && meetsViewportTarget(row.warm);
    assert.equal(row.cold.error, undefined); assert.equal(row.warm.error, undefined);
    for (const imported of [row.cold, row.warm]) {
      assert.equal(imported.snapshotId, 'synthetic-50000');
      assert.equal(imported.fileBytes, fixtureBytes.length);
    }
    assert.deepEqual(await page.locator('.tree-row').evaluateAll(rows => rows.slice(0, 6).map(row => row.dataset.node)), ['root', 'n1', 'n2', 'n3', 'n4', 'n5']);
    assert.equal(await page.evaluate(() => projectExplorer.nodes.size), 50000);
    assert.equal(await page.evaluate(() => projectExplorer.snapshot.edges.length), 50000);
    await page.getByRole('searchbox').fill('Object 049999');
    await page.waitForFunction(() => projectExplorer.searchResults.length === 1);
    await page.locator('[data-node="n49999"]').click();
    await page.waitForFunction(() => projectExplorer.details?.reference?.nodeId === 'n49999');
    assert.deepEqual(await page.evaluate(() => projectExplorer.details.reference), {
      schemaVersion: 'project-model.v1', snapshotId: 'synthetic-50000', sessionGeneration: 'fixture-session-A',
      projectPath: 'D:\\Fixture Projects\\Packaging\\Packaging.ap21', nodeId: 'n49999', objectPath: 'block:Synthetic/Scope1/Object49999',
    });
    await page.getByRole('searchbox').fill('');
    await page.waitForFunction(() => !projectExplorer.filtering() && projectExplorer.visible.length === 50000);
    await page.locator('#tree').evaluate(tree => { tree.scrollTop = 0; });
    await page.locator('[data-node="root"]').click();
    await page.getByRole('tab', { name: 'Relationships', exact: true }).click();
    // UI deliberately starts with smaller graph budgets; exercise its supported maximum query.
    const graph = await page.evaluate(() => projectExplorer.index.relationships('root', { depth: 1, maxNodes: 500, maxEdges: 1000 }));
    verifyGraph(graph);
    check(`Trial ${trial}: exact initial tree, 50k inventories, last-object search/reference and 1000 original bounded edges`);
    if (trial === 1) {
      for (const [label, mutate] of [['missing edge', g => g.edges.pop()], ['wrong endpoint', g => g.edges[0].source.objectPath = '/wrong'], ['false completeness', g => g.truncated = false]]) {
        const changed = structuredClone(graph); mutate(changed); assert.throws(() => verifyGraph(changed)); report.negativeControls.push('Independent graph oracle rejects ' + label);
      }
      const active = await page.evaluate(() => ({ id: projectExplorer.snapshot.snapshotId, selected: projectExplorer.selected, count: projectExplorer.nodes.size }));
      for (const [label, content, code] of [
        ['malformed JSON', '{', 'SyntaxError'],
        ['duplicate edge', (() => { const doc = oracleFixture(); doc.edges.push(doc.edges[0]); return JSON.stringify(doc); })(), 'invalid_edge'],
        ['changed endpoint', (() => { const doc = oracleFixture(); doc.edges[0].target.objectPath = '/wrong'; return JSON.stringify(doc); })(), 'invalid_edge'],
        ['oversized file', ' '.repeat(33554433), 'snapshot_too_large'],
      ]) {
        const rejected = await importSnapshot(page, { name: label + '.json', mimeType: 'application/json', buffer: Buffer.from(content) });
        assert.equal(rejected.error, code);
        assert.deepEqual(await page.evaluate(() => ({ id: projectExplorer.snapshot.snapshotId, selected: projectExplorer.selected, count: projectExplorer.nodes.size })), active);
        report.negativeControls.push(label + ' import refuses and preserves active model/selection');
      }
      await page.screenshot({ path: path.join(output, 'relationships.png'), fullPage: true });
    }
    await browser.close(); browser = null;
    // Persist every completed trial, including misses, before starting the next browser.
    await fs.writeFile(path.join(output, 'progress.json'), JSON.stringify(report, null, 2));
  }
  assert.deepEqual(report.pageErrors, []); assert.deepEqual(report.networkRequests, []);
  assert.equal(hash(await fs.readFile(file)), report.fixture.sha256);
  report.sourcesAfter = {};
  for (const file of Object.keys(sources)) report.sourcesAfter[file] = hash(await fs.readFile(path.join(root, 'ui/project-explorer', file)));
  report.viewer.sha256After = hash(await fs.readFile(viewer));
  assert.deepEqual(report.sourcesAfter, sources, 'Measured implementation and harness sources changed during the run');
  assert.equal(report.viewer.sha256After, report.viewer.sha256, 'Measured viewer changed during the run');
  const values = report.trials.flatMap(row => [row.cold.firstViewportMs, row.warm.firstViewportMs]).sort((a, b) => a - b);
  report.summary = { count: values.length, minMs: values[0], medianMs: values[Math.floor(values.length / 2)], p95Ms: values[Math.ceil(values.length * 0.95) - 1], maxMs: values.at(-1), missedTrials: report.trials.filter(row => !row.passed).map(row => row.trial) };
  report.passed = trials >= 10 && report.trials.every(row => row.passed);
} catch (error) { report.error = error.stack; }
finally {
  if (browser) await browser.close();
  report.completedAtUtc = new Date().toISOString();
  report.machine.freeMemoryAtEndBytes = os.freemem();
  await fs.writeFile(path.join(output, 'result.json'), JSON.stringify(report, null, 2), { flag: 'wx' });
  console.log(JSON.stringify({ passed: report.passed, trials: report.trials.length, summary: report.summary, checks: report.checks.length, negativeControls: report.negativeControls.length, error: report.error, output }));
  if (!report.passed) process.exitCode = 1;
}
