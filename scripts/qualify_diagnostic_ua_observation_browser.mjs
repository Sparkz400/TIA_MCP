// Portable UA report and Explorer integration: actual retained exports, headless, no target access.
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
const [input, destination, samples] = process.argv.slice(2);
assert.ok(input && destination, 'Usage: node qualify_diagnostic_ua_observation_browser.mjs <MCP output> <fresh output>');
const output=path.resolve(destination), root=path.resolve('.');
await fs.mkdir(path.dirname(output),{recursive:true});await fs.mkdir(output);
const workflow=JSON.parse(await fs.readFile(path.join(input,'result.json')));assert.equal(workflow.status,'passed');
const require=createRequire(import.meta.url), bundled=path.join(os.homedir(),'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
let playwright;try{playwright=require('playwright');}catch{playwright=require(process.env.PLAYWRIGHT_MODULE||path.join(bundled,'playwright'));}
const {marked}=await import(pathToFileURL(require.resolve('marked',{paths:[root,bundled]})).href);
const M=require(path.join(root,'ui/project-explorer/model.js'));
const executablePath=process.env.EXPLORER_BROWSER || (await fs.stat(playwright.chromium.executablePath()).then(s=>s.isFile(),()=>false)?playwright.chromium.executablePath():process.platform==='win32'?'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe':undefined);
const browser=await playwright.chromium.launch({headless:true,executablePath});
const sha=v=>createHash('sha256').update(v).digest('hex');
const report={status:'running',nativeQualification:false,browser:browser.version(),checks:[],screenshots:[],errors:[],requests:[],dialogs:[],inputs:{}};
function check(ok,label){assert.ok(ok,label);report.checks.push(label);}
try{
 if(samples){
  const fixture=JSON.parse(await fs.readFile(samples)).uaObservationFixture;assert.equal(fixture.authority,'syntheticManagedProducer');
  await M.verifyUaObservation(fixture.empty);check(true,'Empty original committed prefix remains observable');
  const changed=structuredClone(fixture.empty);changed.captureSource.recordedProgress.elapsedMilliseconds=99;
  await assert.rejects(()=>M.verifyUaObservation(changed));check(true,'Empty prefix cannot invent progress absent from its original plan');
  await M.verifyUaObservation(fixture.observed);check(true,'Managed UInt64 null, uncertain value, host error and gap remain observable');
 }
 const page=await browser.newPage();page.on('pageerror',e=>report.errors.push(String(e)));page.on('request',r=>{if(/^https?:/.test(r.url()))report.requests.push(r.url());});page.on('dialog',async d=>{report.dialogs.push(d.message());await d.dismiss();});
 const directory=path.join(input,'observations');
 for(const filename of (await fs.readdir(directory)).filter(f=>f.endsWith('.json'))){
  const file=path.resolve(directory,filename),text=await fs.readFile(file,'utf8'),doc=JSON.parse(text);report.inputs[filename]=sha(text);
  await M.verifyUaObservation(doc);check(true,filename+' original chain and independently derived summaries accepted');
  for(const [label,mutate] of [
   ['promoted claim',d=>d.sourceCompletenessVerified=true],['changed count',d=>d.signals[0].goodValues++],
   ['changed binding bytes',d=>d.bindingArtifact.base64='e30='],['changed source bytes',d=>d.captureSource.originalFiles[0].base64='e30='],
   ['changed timestamp',d=>{const s=d.signals.find(s=>s.firstObservation);if(s)s.firstObservation.value.sourceTimestamp='2020-01-01T00:00:00Z';else d.events.failureEvents++;}],
   ['hypothesis promotion',d=>d.hypotheses[0].verdict='supported'],['changed monitored item',d=>d.signals[0].monitoredItem={node:{}}]
  ]){const changed=structuredClone(doc);mutate(changed);await assert.rejects(()=>M.verifyUaObservation(changed));check(true,filename+' refuses '+label);}
  await page.goto(pathToFileURL(path.join(root,'ui/project-explorer/index.html')).href);
  await page.locator('.capture-input').setInputFiles(file);
  await page.locator('.runtime-panel h2').filter({hasText:'Diagnostic observations'}).waitFor();
  check((await page.locator('.runtime-panel').innerText()).includes('Hypotheses remain unresolved'),'Explorer uncertainty visible '+filename);
  check(await page.locator('.ua-signal').count()===doc.signals.length,'All selected signals visible '+filename);
  check(await page.locator('.runtime-panel script,.runtime-panel img,.runtime-panel iframe').count()===0,'Hostile report evidence remains text '+filename);
  if(doc.captureSource.plan.request?.events){
   check(await page.locator('.ua-event').count()===2,'Original repeated event records visible');
   const evidence=await page.locator('.runtime-panel').innerText();
   check(evidence.includes('Übertemperatur <script>alert(1)</script>')&&evidence.includes('repeated EventId')&&evidence.includes('ReceiveTime'),'Exact localized event, repetition and separate clocks visible');
   await page.setViewportSize({width:1440,height:900});
   await page.locator('.ua-event').first().scrollIntoViewIfNeeded();
   const bounds=await page.locator('.ua-event').first().boundingBox();
   check(bounds&&bounds.y>=0&&bounds.y<900,'Event timeline is in the visible viewport');
   const shot=path.join(output,'event-timeline.png');await page.screenshot({path:shot});report.screenshots.push(shot);
  }
  const downloadPromise=page.waitForEvent('download');await page.getByRole('button',{name:'Download capture JSON',exact:true}).click();
  const download=await downloadPromise,saved=path.join(output,filename);await download.saveAs(saved);
  check(await fs.readFile(saved,'utf8')===text,'Explorer download preserves original report bytes '+filename);
  const changed=structuredClone(doc);changed.signals[0].goodValues=999999;
  const rejection=await page.evaluate(async value=>{try{await projectExplorer.loadRuntimeCapture(JSON.stringify(value));return 'accepted';}catch(e){return e.message;}},changed);
  check(rejection.includes('Invalid UA observation'),'Explorer refuses forged summary '+filename);
  check(await page.evaluate(()=>projectExplorer.runtimeCapture.doc.signals[0].goodValues)===doc.signals[0].goodValues,'Refusal preserves previous valid view '+filename);
  if(!['capture-observation.json','subscription-observation.json','events-observation.json'].includes(filename))continue;
  await page.locator('.ua-signal summary').nth(1).click();
  check((await page.locator('.ua-signal').nth(1).innerText()).includes('18446744073709551615'),'Exact UInt64 visible '+filename);
  for(const width of [1440,390]){
   await page.setViewportSize({width,height:900});
   check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Explorer fits viewport '+filename+' '+width);
   const shot=path.join(output,filename.replace('.json','')+'-'+width+'.png');await page.screenshot({path:shot});report.screenshots.push(shot);
  }
  const html=path.resolve(directory,filename.replace('.json','.html'));
  for(const width of [1440,390]){
   await page.setViewportSize({width,height:900});await page.goto(pathToFileURL(html).href);
   check(await page.locator('script,img,iframe,object,embed,a[href]').count()===0,'Standalone HTML inert '+filename+' '+width);
   check(await page.locator('pre code').last().textContent()===text,'Standalone HTML embeds exact original JSON '+filename+' '+width);
   check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Standalone HTML fits viewport '+filename+' '+width);
   check(await page.locator('pre').evaluateAll(nodes=>nodes.every(n=>n.tabIndex===0)),'HTML evidence is keyboard accessible '+filename);
  }
  const md=await fs.readFile(path.resolve(directory,filename.replace('.json','.md')),'utf8'),rendered=path.join(output,filename.replace('.json','-markdown.html'));
  await fs.writeFile(rendered,'<!doctype html><meta charset="utf-8">'+marked.parse(md));await page.goto(pathToFileURL(rendered).href);
  check(await page.locator('script,img,iframe,object,embed,a[href]').count()===0,'Rendered Markdown keeps hostile content inert '+filename);
  check(await page.locator('pre code').last().textContent()===text+'\n','Markdown dynamic fence retains exact source '+filename);
 }
 const comparisons=path.join(input,'comparisons');
 if(samples){const fixture=JSON.parse(await fs.readFile(samples)).uaComparisonFixture;for(const name of ['observed','empty','numericLimits','aligned','fractional','anyShape'])await M.verifyUaComparison(fixture[name]);check(true,'Managed exact UInt64 ranges, numeric exclusions, empty prefixes, distinct anchors, fractional boundaries and any-shape declarations verified');}
 for(const filename of (await fs.readdir(comparisons)).filter(f=>f.endsWith('.json'))){
  const file=path.resolve(comparisons,filename),text=await fs.readFile(file,'utf8'),doc=JSON.parse(text);report.inputs['comparison/'+filename]=sha(text);
  await M.verifyUaComparison(doc);check(true,'Independent window, mapping, scalar and event counts verified '+filename);
  for(const [label,mutate] of [
   ['count',d=>d.signals[0].before.goodValues++],['window',d=>d.beforeWindow.selectedRowSequences.push(999)],['mapping',d=>d.signals[0].beforeMapping.node.identifier='wrong'],
   ['range',d=>{const s=d.signals.find(s=>s.before.numericRanges.length);if(s)s.before.numericRanges[0].maximum='1';else d.beforeEvents.notifications++;}],
   ['event count',d=>d.beforeEvents.notifications++],['group omission',d=>d.eventGroups.length?d.eventGroups.pop():d.eventGroups.push({})],
   ['hidden gap',d=>d.beforeWindow.fullPrefixEvidence.gapRows++],['authority',d=>d.bugFixProven=true],['original',d=>d.after.captureSource.originalFiles[0].base64='e30=']
  ]){const changed=structuredClone(doc);mutate(changed);await assert.rejects(()=>M.verifyUaComparison(changed));check(true,'Comparison refuses changed '+label+' '+filename);}
  await page.goto(pathToFileURL(path.join(root,'ui/project-explorer/index.html')).href);await page.locator('.capture-input').setInputFiles(file);
  await page.locator('.runtime-panel h2').filter({hasText:'Before / after captures'}).waitFor();
  check(await page.locator('.ua-comparison-signal').count()===doc.signals.length,'All explicit signal pairs rendered '+filename);
  check(await page.locator('.runtime-panel script,.runtime-panel img,.runtime-panel iframe').count()===0,'Comparison evidence stays inert '+filename);
  await page.locator('[data-action=capture-compare-side][data-side=after]').click();check(await page.evaluate(()=>projectExplorer.runtimeCapture.side)==='after','After original rows accessible');
  await page.locator('[data-action=capture-compare-side][data-side=before]').click();
  const downloadPromise=page.waitForEvent('download');await page.locator('[data-action=capture-download]').click();const downloaded=await downloadPromise;
  check(await fs.readFile(await downloaded.path(),'utf8')===text,'Downloaded comparison preserves supplied original bytes');
  if(filename==='events.json'){
   const hostile='<img data-count-injection="true">';
   // Exercise the renderer separately from the importer validation boundary.
   await page.evaluate(value=>{for(const key of ['beforeCount','afterCount','countDifference'])projectExplorer.runtimeCapture.doc.eventGroups[0][key]=value;projectExplorer.renderUaComparison();},hostile);
   check(await page.locator('.ua-comparison-events [data-count-injection]').count()===0,'Event counts cannot create HTML elements');
   check((await page.locator('.ua-comparison-events tbody tr').first().locator('td').allTextContents()).slice(1).every(value=>value===hostile),'All three event counts preserve hostile text literally');
   await page.evaluate(value=>projectExplorer.loadRuntimeCapture(value),text);
  }
  for(const width of [1440,390]){await page.setViewportSize({width,height:900});await page.evaluate(()=>scrollTo(0,0));check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Comparison fits viewport '+width+' '+filename);}
  if(filename==='events.json'){await page.setViewportSize({width:1440,height:1000});await page.evaluate(()=>scrollTo(0,0));const shot=path.join(output,'ua-before-after-comparison.png');await page.screenshot({path:shot});report.screenshots.push(shot);}
  for(const width of [1440,390]){
   await page.setViewportSize({width,height:900});await page.goto(pathToFileURL(path.resolve(comparisons,filename.replace('.json','.html'))).href);
   check(await page.locator('script,img,iframe,object,embed,a[href]').count()===0,'Comparison HTML inert');
   check(await page.locator('pre code').last().textContent()===text,'Comparison HTML embeds complete original JSON');
   check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Comparison HTML fits viewport '+width);
   check(await page.locator('pre').evaluateAll(nodes=>nodes.every(n=>n.tabIndex===0)),'Comparison HTML evidence keyboard accessible');
  }
  const md=await fs.readFile(path.resolve(comparisons,filename.replace('.json','.md')),'utf8'),rendered=path.join(output,filename.replace('.json','-comparison-markdown.html'));
  await fs.writeFile(rendered,'<!doctype html><meta charset="utf-8">'+marked.parse(md));await page.goto(pathToFileURL(rendered).href);
  check(await page.locator('script,img,iframe').count()===0&&await page.locator('pre code').last().textContent()===text+'\n','Comparison Markdown retains exact source without executing evidence');
 }
 // Pending hash verification must not replace a newer import.
 await page.goto(pathToFileURL(path.join(root,'ui/project-explorer/index.html')).href);
 const ua=await fs.readFile(path.join(directory,'capture-observation.json'),'utf8'),web=await fs.readFile(path.join(root,'fixtures/physical-runtime/capture-portable.json'),'utf8');
 check(await page.evaluate(async ({ua,web})=>{const pending=projectExplorer.loadRuntimeCapture(ua);projectExplorer.loadRuntimeCapture(web);await pending;return projectExplorer.runtimeCapture.doc.schemaVersion;},{ua,web})==='runtime-capture-export.v1','Newer Web capture survives older asynchronous UA import');
 check(report.errors.length===0&&report.requests.length===0&&report.dialogs.length===0,'No browser errors, remote requests or dialogs');
 report.status='passed';
}catch(e){report.status='failed';report.error=String(e);throw e;}
finally{await browser.close();await fs.writeFile(path.join(output,'result.json'),JSON.stringify(report,null,2)+'\n');}
console.log(JSON.stringify({status:report.status,checks:report.checks.length}));
