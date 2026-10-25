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
 // Pending hash verification must not replace a newer import.
 await page.goto(pathToFileURL(path.join(root,'ui/project-explorer/index.html')).href);
 const ua=await fs.readFile(path.join(directory,'capture-observation.json'),'utf8'),web=await fs.readFile(path.join(root,'fixtures/physical-runtime/capture-portable.json'),'utf8');
 check(await page.evaluate(async ({ua,web})=>{const pending=projectExplorer.loadRuntimeCapture(ua);projectExplorer.loadRuntimeCapture(web);await pending;return projectExplorer.runtimeCapture.doc.schemaVersion;},{ua,web})==='runtime-capture-export.v1','Newer Web capture survives older asynchronous UA import');
 check(report.errors.length===0&&report.requests.length===0&&report.dialogs.length===0,'No browser errors, remote requests or dialogs');
 report.status='passed';
}catch(e){report.status='failed';report.error=String(e);throw e;}
finally{await browser.close();await fs.writeFile(path.join(output,'result.json'),JSON.stringify(report,null,2)+'\n');}
console.log(JSON.stringify({status:report.status,checks:report.checks.length}));
