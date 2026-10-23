/** Browser acceptance for the completed native 1,000-function qualification. */
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
import { pack } from '../pack.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const argument = name => {
  const index = process.argv.indexOf(name);
  assert.ok(index >= 0 && process.argv[index + 1] && !process.argv[index + 1].startsWith('--'), `${name} requires a path`);
  return path.resolve(process.argv[index + 1]);
};
const input = argument('--report'), output = argument('--output'), screenshots = argument('--screenshots');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const nativeBytes = await fs.readFile(input), native = JSON.parse(nativeBytes);
assert.equal(native.completed, true, 'Native acceptance must finish before browser acceptance');
assert.equal(native.generatedFunctionCount, 1000);
assert.equal(native.failure, undefined);
assert.equal(native.cleanupFailure, undefined);
const snapshotPath = path.resolve(native.exports.json.path), snapshotBytes = await fs.readFile(snapshotPath);
assert.equal(hash(snapshotBytes), native.exports.json.sha256);
const snapshot = JSON.parse(snapshotBytes);
const names = Array.from({ length: 1000 }, (_, i) => `TiaMcp_Index_Explorer_${String(i).padStart(4, '0')}`);
const functions = snapshot.nodes.filter(n => n.name.startsWith('TiaMcp_Index_Explorer_'));
assert.deepEqual(functions.map(n => n.name).sort(), names);
assert.ok(functions.every(n => n.origin === 'native' && n.kind === 'block' && n.objectPath === `${native.plcPath}/blocks/${n.name}`));
const gate = snapshot.nodes.find(n => n.objectPath === `${native.plcPath}/blocks/TiaMcp_EnableGate`);
assert.ok(gate);
const last = functions.find(n => n.name === names.at(-1));
assert.ok(last.details, 'Native last-function details must be retained in the export');
for (const directory of [output, screenshots]) {
  await fs.mkdir(path.dirname(directory), { recursive: true });
  await fs.mkdir(directory); // Refuse to overwrite an earlier evidence directory.
}
const sources = {};
for (const relative of ['package.json', 'package-lock.json', 'ui/project-explorer/model.js', 'ui/project-explorer/client.js', 'ui/project-explorer/explorer.js', 'ui/project-explorer/style.css', 'ui/project-explorer/pack.mjs', 'ui/project-explorer/tests/browser-scale.mjs']) {
  sources[relative] = hash(await fs.readFile(path.join(root, relative)));
}
const report = { startedAtUtc: new Date().toISOString(), passed: false,
  scope: 'Current project UI renders a hash-verified native offline export. This is not desktop MCP Apps host or PLC runtime acceptance.',
  input: { path: input, sha256: hash(nativeBytes) }, snapshot: { path: snapshotPath, sha256: hash(snapshotBytes), snapshotId: snapshot.snapshotId },
  sources, checks: [], screenshots: [], pageErrors: [], networkRequests: [] };
const browser = await chromium.launch({ headless: true, downloadsPath: path.join(output, 'downloads'), args: ['--allow-file-access-from-files'] });
report.browser = { version: browser.version(), executablePath: chromium.executablePath() };
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, colorScheme: 'light', acceptDownloads: true });
page.on('pageerror', error => report.pageErrors.push(error.message));
page.on('request', request => { if (/^https?:/.test(request.url())) report.networkRequests.push(request.url()); });
const check = name => report.checks.push(name);
const shot = async name => {
  await page.evaluate(() => { const toast = document.querySelector('.toast'); if (toast) toast.hidden = true; });
  const destination = path.join(screenshots, name + '.png');
  await page.screenshot({ path: destination, fullPage: true });
  report.screenshots.push({ path: destination, sha256: hash(await fs.readFile(destination)) });
};
try {
  const viewer = path.join(output, 'native-viewer.html');
  await pack(snapshot, viewer);
  report.viewer = { path: viewer, sha256: hash(await fs.readFile(viewer)) };
  await page.goto(pathToFileURL(viewer).href);
  await page.locator('.project-name').waitFor();
  await page.getByRole('searchbox', { name: 'Search project' }).fill('TiaMcp_Index_Explorer_');
  await page.waitForFunction(() => projectExplorer.searchResults.length === 200);
  for (let count = 400; count <= 1000; count += 200) {
    await page.getByRole('button', { name: 'Load more', exact: true }).click();
    await page.waitForFunction(count => projectExplorer.searchResults.length === count, count);
  }
  const rows = await page.locator('.tree-row').count();
  assert.ok(rows > 0 && rows < 70);
  const actualNames = await page.evaluate(() => projectExplorer.searchResults.map(node => node.name).sort());
  assert.deepEqual(actualNames, names);
  check('All 1,000 exact native functions are searchable with fewer than 70 rendered rows');
  await shot('native-thousand-functions-light-1440');
  await page.getByRole('searchbox').fill(last.name);
  await page.waitForFunction(() => projectExplorer.searchResults.length === 1);
  await page.locator(`[data-node="${last.nodeId}"]`).click();
  await page.waitForFunction(id => projectExplorer.details?.reference?.nodeId === id, last.nodeId);
  const reference = await page.evaluate(() => ProjectExplorerModel.reference(projectExplorer.snapshot, projectExplorer.nodes.get(projectExplorer.selected)));
  assert.deepEqual(reference, { schemaVersion: 'project-model.v1', snapshotId: snapshot.snapshotId, sessionGeneration: snapshot.sessionGeneration,
    projectPath: snapshot.project.projectPath, nodeId: last.nodeId, objectPath: last.objectPath });
  assert.equal(await page.locator('.object-icon').innerText(), 'FC');
  assert.ok((await page.locator('.detail-body').innerText()).includes(last.objectPath));
  check('The last native function selects its full exact reference and captured detail response');
  await shot('native-last-function-light-1440');
  await page.getByRole('searchbox').fill(gate.name);
  await page.locator(`[data-node="${gate.nodeId}"]`).click();
  await page.getByRole('tab', { name: 'Relationships', exact: true }).click();
  await page.locator('.edge-row').first().waitFor();
  const graph = await page.evaluate(() => projectExplorer.graph);
  for (const edge of graph.edges) {
    assert.deepEqual(edge, snapshot.edges.find(original => original.edgeId === edge.edgeId));
  }
  const calls = graph.edges.filter(e => e.kind === 'calls');
  for (const [source, target] of [['Main', 'TiaMcp_EnableGate'], ['TiaMcp_EnableGate', 'TiaMcp_IsEnabled']]) {
    assert.ok(calls.some(e => e.source?.objectPath === `${native.plcPath}/blocks/${source}` && e.target?.objectPath === `${native.plcPath}/blocks/${target}`));
  }
  assert.ok(graph.edges.some(e => e.kind === 'instanceOf' && e.source?.objectPath === `${native.plcPath}/blocks/TiaMcp_EnableGate_DB` && e.target?.objectPath === gate.objectPath));
  for (const [kind, member] of [['reads', 'Enable'], ['writes', 'Active']]) {
    assert.ok(graph.edges.some(e => e.kind === kind && e.source?.objectPath === gate.objectPath && e.unresolved?.target?.name === member));
  }
  for (const edge of calls) assert.ok(edge.evidence?.sourcePath && edge.evidence?.location);
  check('Native authored call, instance, read and write relationships retain direction and evidence');
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await shot('native-relationships-light-1440');
  await page.locator('#relation-kind').selectOption('calls');
  for (const [direction, endpoint] of [['outgoing', 'source'], ['incoming', 'target']]) {
    await page.locator('#relation-direction').selectOption(direction);
    await page.waitForFunction(([endpoint, objectPath]) => projectExplorer.graph?.edges?.length > 0 && projectExplorer.graph.edges.every(e => e.kind === 'calls' && e[endpoint]?.objectPath === objectPath), [endpoint, gate.objectPath]);
    const edges = await page.evaluate(() => projectExplorer.graph.edges);
    assert.ok(edges.every(e => snapshot.edges.some(original => original.edgeId === e.edgeId && JSON.stringify(original) === JSON.stringify(e))));
  }
  check('Incoming and outgoing browser filters retain original native edges without substitution');
  await page.locator('#relation-direction').selectOption('both');
  await page.locator('#relation-kind').selectOption('');
  await page.waitForFunction(count => projectExplorer.graph?.edges?.length === count, graph.edges.length);
  await page.getByRole('button', { name: 'Evidence', exact: true }).first().click();
  assert.ok((await page.locator('#edge-evidence').innerText()).length > 40);
  const downloadEvent = page.waitForEvent('download');
  await page.getByRole('button', { name: 'SVG', exact: true }).click();
  const download = await downloadEvent, svgPath = path.join(output, 'native-relationships.svg');
  await download.saveAs(svgPath);
  const exported = await page.evaluate(svg => {
    const document = new DOMParser().parseFromString(svg, 'image/svg+xml');
    if (document.querySelector('parsererror')) throw new Error('Invalid SVG export');
    return JSON.parse(document.querySelector('metadata').textContent);
  }, await fs.readFile(svgPath, 'utf8'));
  assert.equal(exported.snapshotId, snapshot.snapshotId);
  assert.equal(exported.sessionGeneration, snapshot.sessionGeneration);
  assert.deepEqual(exported.project, snapshot.project);
  assert.deepEqual(exported.coverage, snapshot.coverage);
  assert.deepEqual(exported.evidence, graph.edges);
  assert.deepEqual(exported.graphLimits, graph.limits);
  assert.equal(exported.truncated, !!graph.truncated);
  check('Native graph evidence is inspectable and included in the SVG export');
  await page.setViewportSize({ width: 1024, height: 800 });
  await page.getByRole('button', { name: 'Toggle color theme' }).click();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await shot('native-relationships-dark-1024');
  check('Native relationship view renders at 1440px light and 1024px dark without page overflow');
  assert.deepEqual(report.pageErrors, []);
  assert.deepEqual(report.networkRequests, []);
  assert.equal(hash(await fs.readFile(input)), report.input.sha256);
  assert.equal(hash(await fs.readFile(snapshotPath)), report.snapshot.sha256);
  report.passed = true;
} catch (error) {
  report.error = error.stack;
  process.exitCode = 1;
  await shot('failure').catch(() => {});
} finally {
  await browser.close();
  report.completedAtUtc = new Date().toISOString();
  await fs.writeFile(path.join(output, 'result.json'), JSON.stringify(report, null, 2), { flag: 'wx' });
  console.log(JSON.stringify({ passed: report.passed, checks: report.checks.length, output, error: report.error }));
}
