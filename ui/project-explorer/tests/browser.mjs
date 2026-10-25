import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { pack } from '../pack.mjs';
import { hostFixture } from './host-fixture.mjs';
import { runtimeCaptureBrowser } from './runtime-capture-browser.mjs';
import { observeImports, importSnapshot } from './import-measurement.mjs';
const require = createRequire(import.meta.url);
const { fixture, synthetic } = require('./fixture.cjs');
const { graphFixture } = require('./graph-fixture.cjs');
const directory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(directory, '../../..');
const argument = name => {
  const index = process.argv.indexOf(name);
  if (index < 0) return null;
  if (!process.argv[index + 1] || process.argv[index + 1].startsWith('--')) throw new Error(name + ' requires a path');
  return path.resolve(process.argv[index + 1]);
};
const runId = new Date().toISOString().replace(/[:.]/g, '-') + '-' + process.pid;
const output = argument('--output') || path.join(root, 'artifacts/project-explorer/runs', runId);
const screenshots = argument('--screenshots') || path.join(root, 'output/playwright/project-explorer', runId);
for (const directory of [output, screenshots]) {
  try { await fs.access(directory); throw new Error('Evidence directory already exists; select a fresh path: ' + directory); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
}
await fs.mkdir(path.dirname(output), { recursive: true }); await fs.mkdir(output);
await fs.mkdir(path.dirname(screenshots), { recursive: true }); await fs.mkdir(screenshots);
let playwright;
try { playwright = require('playwright'); } catch { const bundled = process.env.PLAYWRIGHT_MODULE || path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'); playwright = require(bundled); }
const bundledBrowser = playwright.chromium.executablePath();
const bundledBrowserInstalled = await fs.stat(bundledBrowser).then(info => info.isFile(), () => false);
const executablePath = process.env.EXPLORER_BROWSER || (bundledBrowserInstalled ? bundledBrowser : (process.platform === 'win32' ? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' : undefined));
const browser = await playwright.chromium.launch({ headless: true, executablePath, downloadsPath: path.join(output, 'downloads'), args: ['--allow-file-access-from-files'] });
const report = { timestamp: new Date().toISOString(), evidenceKind: 'fixture_browser_qualification', browser: browser.version(), executablePath, checks: [], performance: [], screenshots: [] };
const errors = [], requests = [];
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true, colorScheme: 'light' });
page.on('pageerror', e => errors.push(e.message)); page.on('request', r => { if (/^https?:/.test(r.url())) requests.push(r.url()); });
const check = name => report.checks.push(name);
const shot = async name => { await page.evaluate(()=>{const toast=document.querySelector('.toast');if(toast)toast.hidden=true;});const filename = path.join(screenshots, name + '.png'); await page.screenshot({ path: filename, fullPage: true }); report.screenshots.push(filename); };
try {
  const doc = fixture(), viewer = path.join(output, 'fixture-viewer.html'); await pack(doc, viewer);
  await page.goto(pathToFileURL(viewer).href); await page.locator('.project-name').waitFor();
  await runtimeCaptureBrowser(page,root,output,check,shot);
  assert.equal(await page.locator('script[src]').count(), 0); assert.match(await page.locator('.notice').innerText(), /Offline/); check('Self-contained offline viewer starts with zero network requests');
  for (const theme of ['light', 'dark']) {
    if (await page.evaluate(() => document.documentElement.dataset.theme) !== theme) await page.getByRole('button', { name: 'Toggle color theme' }).click();
    const contrast = await page.evaluate(() => {
      const css = getComputedStyle(document.documentElement);
      const luminance = name => {
        const hex = css.getPropertyValue('--' + name).trim().slice(1);
        const rgb = [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16) / 255).map(v => v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4);
        return rgb[0] * .2126 + rgb[1] * .7152 + rgb[2] * .0722;
      };
      return [['ink', 'panel', 4.5], ['muted', 'panel2', 4.5], ['accent', 'accent-bg', 4.5], ['warn', 'warn-bg', 4.5], ['danger', 'danger-bg', 4.5], ['focus', 'panel', 3]].map(([fg, bg, minimum]) => {
        const a = luminance(fg), b = luminance(bg);
        return { fg, bg, minimum, ratio: (Math.max(a, b) + .05) / (Math.min(a, b) + .05) };
      });
    });
    for (const pair of contrast) assert.ok(pair.ratio >= pair.minimum, `${theme}: ${pair.fg}/${pair.bg} contrast ${pair.ratio}`);
    await page.getByRole('searchbox', { name: 'Search project' }).focus();
    await page.keyboard.press('Tab');
    assert.ok(await page.evaluate(() => document.activeElement.matches(':focus-visible') && parseFloat(getComputedStyle(document.activeElement).outlineWidth) >= 3));
  }
  await page.getByRole('button', { name: 'Toggle color theme' }).click();
  await page.emulateMedia({ reducedMotion: 'reduce' });
  assert.equal(await page.evaluate(() => {
    const probe = document.createElement('div'); probe.className = 'loading'; document.body.append(probe);
    const animation = getComputedStyle(probe).animationName; probe.remove(); return animation;
  }), 'none');
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  check('Both themes retain readable text/status contrast, visible keyboard focus and reduced-motion support');
  await page.getByRole('searchbox', { name: 'Search project' }).fill('MotorControl'); await page.waitForFunction(() => document.querySelectorAll('.tree-row').length === 2);
  await page.locator('[data-node="motor-a"]').click(); await page.waitForFunction(() => projectExplorer.details?.reference?.nodeId === 'motor-a'); const a = await page.evaluate(() => ProjectExplorerModel.reference(projectExplorer.snapshot, projectExplorer.nodes.get(projectExplorer.selected)));
  await page.locator('[data-node="motor-b"]').click(); await page.waitForFunction(() => projectExplorer.details?.reference?.nodeId === 'motor-b'); const b = await page.evaluate(() => ProjectExplorerModel.reference(projectExplorer.snapshot, projectExplorer.nodes.get(projectExplorer.selected))); assert.notEqual(a.objectPath, b.objectPath); check('Duplicate display names preserve distinct exact native references');
  assert.equal(await page.locator('.object-icon').innerText(),'BLK');assert.equal(await page.locator('[data-node="motor-a"] .glyph').innerText(),'FB');assert.equal(await page.locator('[data-node="motor-b"] .glyph').innerText(),'BLK');check('Block icons use exposed type metadata and remain neutral when type is unknown');
  await page.locator('[data-node="motor-a"]').click(); await page.getByRole('tab', { name: 'Relationships', exact: true }).click(); await page.locator('.edge-row').first().waitFor(); assert.equal(await page.locator('.edge-row').count(), 3); await page.getByRole('button', { name: 'Evidence', exact: true }).last().click(); assert.match(await page.locator('#edge-evidence').innerText(), /unresolved/); check('Relationships, unresolved endpoints and source evidence are visible');
  const svgDownload = page.waitForEvent('download'); await page.getByRole('button', { name: 'SVG', exact: true }).click(); const svg = await svgDownload; await svg.saveAs(path.join(output,'fixture-graph.svg')); assert.match(await fs.readFile(path.join(output,'fixture-graph.svg'),'utf8'), /<metadata>/); check('Graph SVG export includes evidence manifest');
  const pngDownload = page.waitForEvent('download'); await page.getByRole('button', { name: 'PNG', exact: true }).click(); const png = await pngDownload; await png.saveAs(path.join(output,'fixture-graph.png')); assert.ok((await fs.stat(path.join(output,'fixture-graph.png'))).size>1000); check('Graph PNG rasterization succeeds offline');
  await shot('fixture-relationships-light-1440');
  await page.getByRole('tab', { name: 'Details', exact: true }).click(); await page.getByRole('searchbox').fill(''); await page.waitForFunction(() => !projectExplorer.filtering()); await page.evaluate(async () => { await projectExplorer.toggle('plc-a'); await projectExplorer.toggle('blocks-a'); });
  await page.locator('[data-node="empty"]').click(); assert.match(await page.locator('.selection-head').innerText(), /Native object/); check('Empty native groups remain native selections');
  await page.locator('[data-node="protected"]').click(); assert.match(await page.locator('.detail-body').innerText(), /Protected object/); check('Protected content explicitly distinguished from empty');
  await page.getByRole('searchbox').fill('ÄÖÜ'); await page.waitForFunction(() => projectExplorer.searchResults.length === 1); await page.locator('[data-node="hostile"]').click(); assert.equal(await page.locator('.detail-body img').count(), 0); assert.match(await page.locator('.selection-head').innerText(), /<script>/); check('Adversarial Unicode HTML labels are text, never executed');
  await page.getByRole('searchbox').fill(''); await page.waitForFunction(() => !projectExplorer.filtering()); await page.locator('[data-node="hardware"]').click(); assert.equal(await page.getByRole('button', { name: 'Copy reference' }).isDisabled(), true); assert.match(await page.locator('.detail-body').innerText(), /virtual display folder/); check('Virtual folder cannot become engineering selection');
  await page.locator('#tree').focus(); await page.keyboard.press('Home'); await page.keyboard.press('ArrowDown'); assert.equal(await page.evaluate(() => projectExplorer.selected), 'hardware'); check('Keyboard tree navigation follows visible hierarchy');
  await page.getByRole('searchbox').fill('MotorControl'); await page.waitForFunction(() => projectExplorer.searchResults.length === 2); await page.locator('[data-node="motor-a"]').click(); await page.getByRole('tab', { name: 'Previews', exact: true }).click(); assert.match(await page.locator('.detail-body').innerText(), /No preview supplied/); check('Unavailable HMI and typed preview providers remain explicit');
  // Provider contract integration: structured content only, separately labelled content revision.
  await page.evaluate(async () => { const node = projectExplorer.nodes.get('motor-a'); node.details = { capturedAtUtc: '2026-10-08T10:01:00Z', contentRevision: { status: 'available', scope: 'graphicalNetwork', value: 'fixture-content' }, previews: [{ provider: 'typedLadFbd', status: 'partial', reason: 'Fixture-only structured preview integration', attachments: [{ kind: 'ladFbdNetwork', source: { objectPath: node.objectPath, contentRevision: 'fixture-content', capturedAtUtc: '2026-10-08T10:01:00Z' }, content: { language: 'LAD', nodes: [{ kind: 'contact', name: 'Enable <script>', operand: 'Enable' }, { kind: 'coil', name: 'Run', operand: 'MotorRun' }], connections: [{ from: 'Enable', to: 'Run' }] }, coverage: { status: 'partial' } }] }] }; await projectExplorer.loadTab(); }); assert.equal(await page.locator('.preview-node').count(),2); assert.match(await page.locator('.preview-frame').innerText(),/Approximate structured view/); check('Provider attachments integrate without executing HMI scripts or treating raw XML as typed content');
  await page.evaluate(async()=>{const node=projectExplorer.nodes.get('motor-a');node.details.previews[0].attachments[0].content={networkId:'42',language:'LAD',coverage:'partial',nodes:[{key:'native:21',nativeUid:'21',nativeName:'Contact',kind:'contact',supported:true,pins:[{name:'in',direction:'input',dataType:'Bool'}]},{key:'native:22',nativeUid:'22',nativeName:'Coil',kind:'coil',supported:true},{key:'native:23',nativeUid:'23',nativeName:'Unknown <script>',kind:'opaque',supported:false}],connections:[{nativeUid:'31',endpoints:[{node:'native:21',port:'out'},{node:'native:22',port:'in'}],resolved:true},{nativeUid:'32',endpoints:[{node:'native:22',port:'out'},{node:'native:23',port:null}],resolved:false}]};await projectExplorer.loadTab();});assert.ok(await page.locator('.preview-frame svg path').count()>0);assert.match(await page.locator('.preview-frame').innerText(),/1 opaque/);assert.match(await page.locator('.preview-frame').innerText(),/1 unresolved connections/);check('Real typed provider node keys, native UIds, pins, opaque nodes and unresolved wires render schematically');await shot('fixture-typed-preview-light-1440');

  // An explicit user file attachment is retained only on the exact selected object.
  const graphPath=argument('--graph-report'), graphReport=graphPath?JSON.parse(await fs.readFile(graphPath,'utf8')):graphFixture();
  const importGraph=async report=>{const chooser=page.waitForEvent('filechooser');await page.getByRole('button',{name:'Attach supplied GRAPH report',exact:true}).click();await (await chooser).setFiles({name:'inspection.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(report))});};
  await importGraph(graphReport);await page.locator('.graph-preview').waitFor();
  assert.match(await page.locator('.graph-preview').innerText(),/No native rendering or runtime qualification/);
  assert.equal(await page.evaluate(()=>projectExplorer.graphAttachments[0].source.nodeId),'motor-a');
  assert.equal(await page.locator('.graph-preview svg').count(),1);
  await page.locator('[data-action="graph-item"]').first().click();assert.match(await page.locator('.graph-item-detail').innerText(),/Idle/);
  await page.locator('.graph-preview summary').filter({hasText:'Chart pre/post'}).click();
  assert.match(await page.locator('.graph-preview').innerText(),/preOperations/);
  const sequenceOptions=await page.locator('.graph-sequence option').count();if(sequenceOptions>1){await page.locator('.graph-sequence').selectOption('1');assert.equal(await page.locator('[data-action="graph-item"]').count(),0);await page.locator('.graph-sequence').selectOption('0');}
  check('Supplied GRAPH report navigates exact sequence and item records with explicit offline provenance');
  const sourceDownload=page.waitForEvent('download');await page.getByRole('button',{name:'Download original XML',exact:true}).click();await (await sourceDownload).saveAs(path.join(output,'graph-original.xml'));
  assert.deepEqual(await fs.readFile(path.join(output,'graph-original.xml')),Buffer.from(graphReport.originalSourceBase64,'base64'));
  const graphExport=page.waitForEvent('download');await page.getByRole('button',{name:'Export JSON',exact:true}).click();await (await graphExport).saveAs(path.join(output,'graph-snapshot.json'));
  const graphSnapshot=JSON.parse(await fs.readFile(path.join(output,'graph-snapshot.json'),'utf8'));assert.deepEqual(graphSnapshot.nodes.find(n=>n.nodeId==='motor-a').previews[0].content,graphReport);
  check('GRAPH source download is byte exact and derived snapshot retains exact object attachment');
  const badGraph=structuredClone(graphReport);badGraph.sourceSha256='a'.repeat(64);await importGraph(badGraph);await page.locator('.notice').filter({hasText:'SHA-256 differs'}).waitFor();assert.equal(await page.locator('.graph-preview').count(),1);
  check('Tampered GRAPH report rejected without replacing the existing attachment');
  await page.evaluate(()=>{projectExplorer.notice();document.querySelector('.graph-preview').scrollIntoView({block:'start'});});await shot('fixture-graph-preview-light-1440');
  await page.locator('.graph-preview').screenshot({path:path.join(screenshots,'graph-panel.png')});report.screenshots.push(path.join(screenshots,'graph-panel.png'));
  await page.locator('[data-node="motor-b"]').click();await page.getByRole('tab',{name:'Previews',exact:true}).click();assert.equal(await page.locator('.graph-preview').count(),0);
  await page.evaluate(async snapshot=>{await projectExplorer.load(snapshot,new ProjectExplorerClient.OfflineTransport(snapshot));await projectExplorer.select('motor-a');projectExplorer.tab='previews';await projectExplorer.loadTab();},graphSnapshot);
  assert.equal(await page.locator('.graph-preview').count(),1);assert.equal(await page.locator('.graph-preview img,.graph-preview script').count(),0);
  check('Saved GRAPH attachment survives snapshot reopen without leaking to same-name object or executing opaque content');
  // Change selection while the chooser is open; the upload must not bind to its new selection.
  const staleChooser=page.waitForEvent('filechooser');await page.getByRole('button',{name:'Attach supplied GRAPH report',exact:true}).click();const staleFile=await staleChooser;
  await page.evaluate(()=>projectExplorer.select('motor-b'));await staleFile.setFiles({name:'inspection.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(graphReport))});
  await page.locator('.notice').filter({hasText:'Selection changed while importing'}).waitFor();assert.equal(await page.evaluate(()=>projectExplorer.graphAttachments.length),0);
  check('GRAPH file chooser cannot attach an old selection to a newer object');
  await page.evaluate(async()=>{projectExplorer.notice();await projectExplorer.select('motor-a');});

  await page.getByRole('tab', { name: 'Details', exact: true }).click(); await shot('fixture-details-light-1440');
  await page.setViewportSize({width:1024,height:800}); await page.getByRole('button', { name: 'Toggle color theme' }).click(); await shot('fixture-details-dark-1024'); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); check('1024px dark desktop layout has no page overflow');
  await page.setViewportSize({width:760,height:1000}); await shot('fixture-responsive-760'); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); check('760px stacked layout has no page overflow');
  await page.setViewportSize({width:390,height:844});
  assert.ok(await page.evaluate(() => {
    const heading = document.querySelector('.selection-title h2');
    return document.documentElement.scrollWidth <= innerWidth && heading.getBoundingClientRect().height <= parseFloat(getComputedStyle(heading).lineHeight) * 2;
  }), 'Phone layout must keep the selected object title readable beside its actions');
  await shot('fixture-responsive-390'); check('390px phone layout keeps the object title readable without page overflow');
  await page.evaluate(async () => { const doc = structuredClone(projectExplorer.snapshot); doc.expiresAtUtc='2020-01-01T00:00:00Z'; await projectExplorer.load(doc,new ProjectExplorerClient.OfflineTransport(doc)); }); assert.match(await page.locator('.notice').innerText(),/expired/); check('Expired historical snapshot is visibly stale');
  // Delayed old details must not overwrite a new selection; transport failure is visible.
  await page.evaluate(async () => { const original=projectExplorer.client.transport.invoke.bind(projectExplorer.client.transport); projectExplorer.client.transport.invoke=async (action,args)=>{ if(action==='details'&&args.reference.nodeId==='motor-a') await new Promise(r=>setTimeout(r,100)); return original(action,args); }; const first=projectExplorer.select('motor-a'); await projectExplorer.select('motor-b'); await first; }); assert.equal(await page.evaluate(()=>projectExplorer.details.reference.nodeId),'motor-b'); check('Delayed old detail result cannot overwrite newer exact selection');
  await page.evaluate(async()=>{projectExplorer.client.transport.invoke=async()=>{const e=new Error('Worker session lost');e.code='session_lost';throw e;};await projectExplorer.select('motor-a');}); assert.match(await page.locator('#connection-state').innerText(),/Disconnected/);check('Worker session loss is visible; old reference is retained without name substitution');
  // Real host envelope and protocol exercised in a browser parent fixture.
  const empty = path.join(output,'mcp-app.html'); await pack(null,empty);
  const scripts = (await Promise.all(['model.js','client.js'].map(n=>fs.readFile(path.join(directory,'..',n),'utf8')))).join('\n');
  const hostPage = path.join(output,'mcp-host-fixture.html');
  await fs.writeFile(hostPage,hostFixture(scripts,fixture()));
  const compactHost = path.join(output,'mcp-compact-host-fixture.html');
  await fs.writeFile(compactHost,hostFixture(scripts,fixture()).replace('height:100vh','height:300px').replace('<script>','<script>window.resizeApp=true;'));
  await page.setViewportSize({width:460,height:1000}); await page.goto(pathToFileURL(compactHost).href);
  await page.waitForFunction(()=>sizes.length>0 && document.getElementById('app').getBoundingClientRect().height>500);
  const compactFrame=page.frameLocator('#app');
  await compactFrame.getByRole('searchbox').fill('MotorControl'); await compactFrame.locator('[data-node="motor-a"]').click();
  await compactFrame.getByRole('button',{name:'Send selection ↗'}).waitFor();
  assert.ok(await compactFrame.locator('body').evaluate(body=>body.scrollWidth<=document.documentElement.clientWidth));
  await shot('mcp-compact-host-resized');
  check('Compact MCP host grows from 300px to fit content and supports search and exact selection without horizontal overflow');
  await page.setViewportSize({width:1440,height:1000}); await page.goto(pathToFileURL(hostPage).href); const frame=page.frameLocator('#app');await frame.locator('.project-name').waitFor();await frame.getByRole('searchbox').fill('MotorControl');await frame.locator('[data-node="motor-a"]').waitFor();await frame.locator('[data-node="motor-a"]').click();await frame.getByRole('button',{name:'Send selection ↗'}).click();await page.waitForFunction(()=>selections.length===1);const protocol=await page.evaluate(()=>({requests,selections}));assert.ok(protocol.requests.every(r=>r.name==='project_model'&&r.arguments.request));assert.ok(protocol.requests.some(r=>r.arguments.request.action==='verify'&&r.arguments.request.reference.objectPath===a.objectPath));assert.match(protocol.selections[0].content[0].text,/block:PLC_A\/Blocks\/MotorControl/);check('MCP App handshake, real host request envelope, lazy children, search, exact verify and explicit agent selection work through parent protocol');
  await frame.getByRole('tab',{name:'Relationships',exact:true}).click();await frame.locator('.edge-row').first().waitFor();
  for(const [direction,native,count] of [['outgoing','out',2],['incoming','in',1]]){await frame.locator('#relation-direction').selectOption(direction);await page.waitForFunction(([native,count])=>requests.some(r=>r.name==='project_model'&&r.arguments.request.action==='relationships'&&r.arguments.request.direction===native)&&document.getElementById('app').contentDocument.querySelectorAll('.edge-row').length===count,[native,count]);assert.equal(await frame.locator('.edge-row').count(),count);assert.equal(await page.evaluate(()=>requests.filter(r=>r.name==='project_model'&&r.arguments.request.action==='relationships').at(-1).arguments.request.direction),native);}
  check('MCP relationship incoming/outgoing filters send native in/out and return only correctly directed edges');
  const liveExport=page.waitForEvent('download');await frame.getByRole('button',{name:'Export JSON',exact:true}).click();const liveDownload=await liveExport;await liveDownload.saveAs(path.join(output,'mcp-export.json'));const transferred=JSON.parse(await fs.readFile(path.join(output,'mcp-export.json'),'utf8'));assert.equal(transferred.snapshotId,doc.snapshotId);assert.ok(transferred.exportManifest.coverage.length);assert.ok(await page.evaluate(()=>requests.some(r=>r.name==='artifact_read'&&r.arguments.count===65536)));check('Live JSON export transfers host-owned artifact via bounded artifact_read and verifies SHA-256');
  await page.evaluate(()=>window.tamperArtifact=true);await frame.getByRole('button',{name:'Export JSON',exact:true}).click();await frame.locator('.notice').filter({hasText:'SHA-256 integrity'}).waitFor();check('Tampered live artifact is rejected instead of downloading corrupted project data');
  const childSnapshot=fixture(),ownerPath='plc:PLC_A/technology/Axis';
  childSnapshot.nodes.push({nodeId:'axis',parentId:'plc-a',kind:'technologyObject',name:'Axis',origin:'native',objectPath:ownerPath,plcPath:'plc:PLC_A',metadata:{ownerPath:'plc:PLC_A/technology',composition:'TechnologicalObjects'}});
  for(const composition of ['OutputCams','MeasuringInputs'])childSnapshot.nodes.push({nodeId:composition,parentId:'axis',kind:'technologyObject',name:'Shared',origin:'native',objectPath:ownerPath+'/children/'+composition+'/Shared',plcPath:'plc:PLC_A',metadata:{ownerPath,composition}});
  const childHost=path.join(output,'technology-child-host.html');await fs.writeFile(childHost,hostFixture(scripts,childSnapshot));
  await page.goto(pathToFileURL(childHost).href);const childFrame=page.frameLocator('#app');await childFrame.locator('[data-node="plc-a"]').waitFor();
  await childFrame.locator('[data-node="plc-a"] .toggle').click();await childFrame.locator('[data-node="axis"]').waitFor();
  assert.equal(await childFrame.locator('[data-node="axis"] .toggle').isEnabled(),true,'Live technology owners must allow first child discovery');
  await childFrame.locator('[data-node="axis"] .toggle').click();await childFrame.locator('[data-node="OutputCams"]').waitFor();await childFrame.locator('[data-node="MeasuringInputs"]').waitFor();
  assert.ok(await page.evaluate(()=>requests.some(r=>r.arguments?.request?.action==='children'&&r.arguments.request.parentId==='axis')));
  check('Live technology owners expand lazily through the existing children protocol');
  await childFrame.locator('[data-node="MeasuringInputs"]').click();await childFrame.getByRole('button',{name:'Send selection ↗'}).click();await page.waitForFunction(()=>selections.length===1);
  assert.match(await page.evaluate(()=>selections[0].content[0].text),/children\/MeasuringInputs\/Shared/);
  assert.ok(await page.evaluate(()=>requests.some(r=>r.arguments?.request?.action==='verify'&&r.arguments.request.reference.objectPath.endsWith('/children/MeasuringInputs/Shared'))));
  check('Same-named technology children retain exact composition selection through agent handoff');await shot('technology-children-live-fixture');
  // Import a saved model through the user-facing file picker, preserving independent edge expectations.
  await page.goto(pathToFileURL(viewer).href);await page.locator('.project-name').waitFor();
  const oracle=require('./relationship-fixture.cjs').oracleFixture(), oracleFile=process.env.TIA_RELATIONSHIP_EVIDENCE?path.join(process.env.TIA_RELATIONSHIP_EVIDENCE,'store-export.json'):path.join(output,'relationship-oracle.json');
  if(!process.env.TIA_RELATIONSHIP_EVIDENCE)await fs.writeFile(oracleFile,JSON.stringify(oracle));
  await page.locator('.file-input').setInputFiles(oracleFile);await page.waitForFunction(()=>projectExplorer.nodes.has('A'));
  await page.locator('[data-node="A"]').click();await page.getByRole('tab',{name:'Relationships',exact:true}).click();
  await page.locator('#relation-kind').selectOption('calls');await page.locator('#relation-direction').selectOption('outgoing');
  await page.waitForFunction(()=>projectExplorer.graph?.edges.length===4 && projectExplorer.graph.relationshipScope?.direction==='outgoing');
  assert.match(await page.locator('.relationship-coverage').innerText(),/Whole-project relationship completeness is not established/);
  assert.match(await page.locator('.relationship-coverage').innerText(),/Stopped by: depth/);
  assert.deepEqual(await page.evaluate(()=>projectExplorer.graph.edges.map(e=>e.edgeId).sort()),['ab','ab-second','missing-target','self']);
  await page.getByRole('button',{name:/Expand one step/}).click();await page.waitForFunction(()=>projectExplorer.graph?.edges.length===5);
  await page.getByRole('button',{name:/Expand one step/}).click();await page.waitForFunction(()=>projectExplorer.graph?.edges.length===6);
  assert.equal(await page.evaluate(()=>projectExplorer.graph.truncated),false);assert.match(await page.locator('.relationship-coverage').innerText(),/Traversal complete within captured edges/);
  await page.getByText('Provider scopes and capture limits',{exact:false}).click();
  if(process.env.TIA_RELATIONSHIP_EVIDENCE)assert.match(await page.locator('.relationship-coverage').innerText(),/nativeRelationships[\s\S]*partial/);
  await shot('relationship-scope-saved-model');
  const scopedSvg=page.waitForEvent('download');await page.getByRole('button',{name:'SVG',exact:true}).click();await (await scopedSvg).saveAs(path.join(output,'relationship-scope.svg'));
  assert.match(await fs.readFile(path.join(output,'relationship-scope.svg'),'utf8'),/relationshipScope/);
  check('Saved graph import preserves independent cycle, parallel evidence, unresolved endpoints, depth truncation and provider scope in UI and SVG');
  // Capture/response time is excluded from render timing. Heap numbers are Chromium samples.
  await page.goto(pathToFileURL(viewer).href);await page.locator('.project-name').waitFor();
  // Exercise the actual portable-file path. Passing 50k nested objects through
  // Playwright evaluate() leaves automation deserialization/GC in frame timings.
  await observeImports(page);
  for (const count of [1000, 10000, 50000]) {
    const snapshot = require('./relationship-fixture.cjs').scaleFixture(count, true);
    const file = path.join(output, `scale-${count}.json`);
    await fs.writeFile(file, JSON.stringify(snapshot), { flag: 'wx' });
    const imported = await importSnapshot(page, file);
    assert.equal(imported.error, undefined);
    const result = await page.evaluate(async () => {
      const renderedRows = document.querySelectorAll('.tree-row').length;
      document.querySelector('#tree').scrollTop = 1000000;
      await new Promise(resolve => requestAnimationFrame(resolve));
      const afterScrollRows = document.querySelectorAll('.tree-row').length;
      const graph = projectExplorer.index.relationships('root', { depth: 1, maxNodes: 500, maxEdges: 1000 });
      projectExplorer.selected = 'root'; projectExplorer.tab = 'relationships'; projectExplorer.graph = graph;
      const graphStart = performance.now(); projectExplorer.renderGraph();
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      return { count: projectExplorer.snapshot.nodes.length, retainedEdges: projectExplorer.snapshot.edges.length,
        graphTruncated: graph.truncated, stopReasons: graph.relationshipScope.stopReasons, renderedRows, afterScrollRows,
        graphRenderMs: performance.now() - graphStart, graphNodes: graph.nodes.length, graphEdges: graph.edges.length,
        heap: performance.memory ? { usedJSHeapSize: performance.memory.usedJSHeapSize, totalJSHeapSize: performance.memory.totalJSHeapSize } : null };
    });
    result.firstViewportMs = imported.firstViewportMs;
    result.viewportMethod = 'file-input-change-through-file-read-parse-validation-index-and-two-frames';
    assert.ok(result.renderedRows > 0 && result.renderedRows < 70); assert.ok(result.afterScrollRows > 0 && result.afterScrollRows < 70);
    assert.equal(result.retainedEdges, count);
    if (count > 1000) {
      assert.equal(result.graphTruncated, true); assert.ok(result.stopReasons.includes('edges'));
      assert.match(await page.locator('.relationship-coverage').innerText(), /Stopped by: edges/);
    }
    result.passesViewportTarget = result.firstViewportMs < 1000; result.passesGraphTarget = result.graphRenderMs < 2000;
    report.performance.push(result);
  }
  check('1k/10k/50k saved nodes and edges virtualize tree rows; actual bounded 500-node/1000-edge query renders with visible large connected graph truncation');
  const nativeArg=process.argv.indexOf('--snapshot');
  if(nativeArg>=0){
    const nativePath=path.resolve(process.argv[nativeArg+1]),snapshot=JSON.parse(await fs.readFile(nativePath,'utf8')),nativeHtml=path.join(output,'native-viewer.html');
    await pack(snapshot,nativeHtml);await page.goto(pathToFileURL(nativeHtml).href);await page.locator('.project-name').waitFor();await shot('native-overview-light-1440');
    for(const [kind,label] of [['FC','FC'],['FB','FB'],['OB','OB'],['InstanceDB','DB']]){const block=snapshot.nodes.find(n=>n.kind==='block'&&n.metadata?.kind===kind);if(block){await page.evaluate(id=>projectExplorer.select(id),block.nodeId);assert.equal(await page.locator('.object-icon').innerText(),label);assert.equal(await page.locator(`[data-node="${block.nodeId}"] .glyph`).innerText(),label);}}
    check('Native FC/FB/OB/instance DB icons match captured block type in tree and selection');
    const selection=snapshot.nodes.find(n=>n.kind==='block'&&n.details)||snapshot.nodes.find(n=>n.kind==='block');
    if(selection){await page.evaluate(id=>projectExplorer.select(id),selection.nodeId);await shot('native-details-light-1440');await page.getByRole('tab',{name:'Relationships',exact:true}).click();await shot('native-relationships-light-1440');}
    const typed=snapshot.nodes.find(n=>n.details?.previews?.some(p=>p.attachments?.some(a=>a.kind==='ladFbdNetwork')));
    if(typed){await page.evaluate(id=>projectExplorer.select(id),typed.nodeId);await page.getByRole('tab',{name:'Previews',exact:true}).click();assert.ok(await page.locator('.preview-frame svg').count()>0);await shot('native-typed-preview-light-1440');check('Real native LAD/FBD provider snapshot renders source-hashed structural preview');}
    const hardware=snapshot.nodes.find(n=>n.details?.details?.items?.some(i=>i.addresses?.length));
    if(hardware){await page.evaluate(id=>projectExplorer.select(id),hardware.nodeId);await page.getByRole('tab',{name:'Hardware',exact:true}).click();await shot('native-hardware-light-1440');check('Real native address/channel observations render in hardware tables');}
    report.nativeSnapshot={path:nativePath,snapshotId:snapshot.snapshotId,nodeCount:snapshot.nodes.length,edgeCount:snapshot.edges?.length||0,typedPreviewObject:typed?.objectPath||null,hardwareDetailObject:hardware?.objectPath||null};
    check('Native captured snapshot loads in same offline viewer and preserves exact selected object');
  }
  assert.deepEqual(errors,[]);assert.deepEqual(requests,[]);report.passed=true;
}catch(error){report.passed=false;report.error=error.stack;await shot('failure');throw error;}finally{report.pageErrors=errors;report.networkRequests=requests;await fs.writeFile(path.join(output,'browser-results.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report,null,2));await browser.close();}
