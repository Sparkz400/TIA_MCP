// Render the exact native HMI explorer export, retaining its capture limitations.
// This never connects to TIA, executes project scripts, or renders a native HMI.
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
import { pack } from '../ui/project-explorer/pack.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const argument = name => {
  const index = process.argv.indexOf(name);
  if (index < 0 || !process.argv[index + 1] || process.argv[index + 1].startsWith('--')) throw new Error(name + ' requires a path');
  return path.resolve(process.argv[index + 1]);
};
const bridgePath = argument('--bridge'), output = argument('--output'), screenshots = argument('--screenshots');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
for (const directory of [output, screenshots]) {
  try { await fs.access(directory); throw new Error('Evidence path already exists: ' + directory); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
}
const bridgeBytes = await fs.readFile(bridgePath), bridge = JSON.parse(bridgeBytes.toString('utf8').replace(/^\uFEFF/, ''));
const profile = bridge.profiles.find(item => item.profile === 'unified-comfort');
assert.equal(profile.gates.explorerIntegration.status, 'passed', 'A successful native populated explorer capture is required');
const explorer = profile.explorer, exported = explorer.htmlExport;
const htmlBytes = await fs.readFile(exported.artifact.path), snapshotBytes = await fs.readFile(exported.snapshotArtifact.path);
assert.equal(hash(htmlBytes), exported.artifact.sha256);
assert.equal(hash(snapshotBytes), exported.snapshotArtifact.sha256);
const snapshot = JSON.parse(snapshotBytes.toString('utf8').replace(/^\uFEFF/, ''));
assert.equal(snapshot.snapshotId, explorer.capture.snapshotId);
assert.ok(explorer.populatedRelationships.length >= 8, 'Both HMI owners must have populated tag/alarm captures');
await fs.mkdir(output, { recursive: true }); await fs.mkdir(screenshots, { recursive: true });
const report = { schemaVersion: 1, completed: false, passed: false, observedAtUtc: new Date().toISOString(),
  scope: 'Desktop rendering of exact captured HMI explorer metadata and graph evidence; no native screen or runtime acceptance',
  nativeVisualVerified: false, scriptsExecuted: false, runtimeVerified: false, checks: [], screenshots: [],
  bridge: { path: bridgePath, sha256: hash(bridgeBytes) }, nativeHtml: exported.artifact,
  snapshot: exported.snapshotArtifact, snapshotId: snapshot.snapshotId,
  scriptSha256: hash(await fs.readFile(fileURLToPath(import.meta.url))) };
const check = label => { report.checks.push({ label, passed: true }); console.log('PASS ' + label); };
let browser;
try {
  let htmlPath = exported.artifact.path;
  if (process.argv.includes('--repack-current')) {
    htmlPath = path.join(output, 'current-renderer.html');
    await pack(snapshot, htmlPath);
    report.renderer = { origin: 'current-repository-repack', html: htmlPath, sha256: hash(await fs.readFile(htmlPath)), sources: [] };
    for (const name of ['explorer.js', 'model.js', 'client.js', 'style.css', 'index.template.html', 'pack.mjs']) {
      const source = path.join(root, 'ui/project-explorer', name);
      report.renderer.sources.push({ path: source, sha256: hash(await fs.readFile(source)) });
    }
  } else report.renderer = { origin: 'exact-native-host-export', html: htmlPath, sha256: hash(htmlBytes) };
  browser = await chromium.launch({ headless: true }); report.browserVersion = browser.version();
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [], network = []; report.pageErrors = errors; report.networkRequests = network;
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (/^https?:/i.test(request.url())) network.push(request.url()); });
  const shot = async name => {
    const target = path.join(screenshots, name + '.png'); await page.screenshot({ path: target, fullPage: true });
    report.screenshots.push({ path: target, sha256: hash(await fs.readFile(target)) });
  };
  await page.goto(pathToFileURL(htmlPath).href); await page.locator('.project-name').waitFor();
  assert.equal(await page.evaluate(() => projectExplorer.snapshot.snapshotId), snapshot.snapshotId);
  check('Exact native snapshot and artifact hashes retained');
  const selectedScreen = snapshot.nodes.find(node => node.nodeId === explorer.details.reference.nodeId);
  assert.ok(selectedScreen && selectedScreen.kind === 'hmiScreen');
  await page.getByRole('searchbox').fill(selectedScreen.name);
  await page.locator(`[data-node="${selectedScreen.nodeId}"]`).click();
  await page.getByRole('tab', { name: 'Details', exact: true }).click();
  await page.waitForFunction(id => projectExplorer.details?.reference?.nodeId === id, selectedScreen.nodeId);
  assert.equal(await page.evaluate(() => projectExplorer.details.details.provider), 'hmi.v21');
  await page.getByText('Inspect structured detail response', { exact: true }).click();
  const renderedDetails = await page.locator('.detail-body pre.data').innerText();
  assert.ok(renderedDetails.includes(selectedScreen.objectPath));
  for (const item of explorer.details.details.observation.items) assert.ok(renderedDetails.includes(item.name));
  await shot('native-hmi-details-1440'); check('Exact native HMI detail payload and scoped item names render');
  await page.getByRole('tab', { name: 'Coverage', exact: true }).click();
  assert.match(await page.locator('.detail-body').innerText(), /screen items|incomplete mappings|content.*separate/i);
  await shot('native-hmi-coverage-1440'); check('Partial native content and relationship coverage remains visible');
  report.relationships = [];
  for (const entry of explorer.populatedRelationships) {
    const node = snapshot.nodes.find(item => item.nodeId === entry.reference.nodeId);
    assert.ok(node && node.objectPath === entry.reference.objectPath);
    await page.getByRole('searchbox').fill(node.name);
    await page.locator(`[data-node="${node.nodeId}"]`).click();
    await page.getByRole('tab', { name: 'Relationships', exact: true }).click();
    await page.waitForFunction(id => projectExplorer.selected === id && projectExplorer.graph?.nodes?.[0]?.nodeId === id
      && projectExplorer.graph?.edges?.length > 0 && !document.querySelector('.detail-body .loading'), node.nodeId);
    const nativeEdges = entry.result.edges.filter(edge => ['hmiAlarmTag', 'hmiTagConnection'].includes(edge.evidence?.native?.relation)
      && edge.evidence.resolution === 'resolved' && (edge.source?.nodeId === node.nodeId || edge.target?.nodeId === node.nodeId));
    assert.ok(nativeEdges.length > 0);
    const rendered = await page.locator('.edge-row').evaluateAll(rows => rows.map(row => [...row.querySelectorAll('.endpoint')].map(item => item.title)));
    for (const edge of nativeEdges) assert.ok(rendered.some(pair => pair[0] === edge.source.objectPath && pair[1] === edge.target.objectPath));
    report.relationships.push({ objectPath: node.objectPath, nodeId: node.nodeId, expectedPopulatedEdges: nativeEdges.length, renderedRows: rendered.length });
  }
  await shot('native-hmi-relationships-1440'); check('Both HMI owners render exact source and destination paths for populated tag/alarm/connection edges');
  const before = await page.evaluate(() => projectExplorer.graph.edges.filter(edge => edge.kind === 'hmiBinding').length);
  assert.ok(before > 0);
  await page.locator('#relation-kind').selectOption({ label: 'HMI bindings' });
  await page.waitForFunction(() => !document.querySelector('.detail-body .loading'));
  assert.equal(await page.locator('#relation-kind').inputValue(), 'hmiBinding', 'HMI filter must send the native relationship kind');
  assert.equal(await page.locator('.edge-row').count(), before);
  check('HMI binding filter preserves the actual native graph');
  await page.setViewportSize({ width: 1024, height: 800 }); await page.getByRole('button', { name: 'Toggle color theme' }).click();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await shot('native-hmi-relationships-dark-1024'); check('Dark desktop 1024px relationship layout has no page overflow');
  assert.deepEqual(errors, []); assert.deepEqual(network, []);
  report.passed = true;
} catch (error) {
  report.error = error.stack; process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  report.completed = true;
  await fs.writeFile(path.join(output, 'browser-results.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ passed: report.passed, report: path.join(output, 'browser-results.json'), error: report.error || null }));
}
