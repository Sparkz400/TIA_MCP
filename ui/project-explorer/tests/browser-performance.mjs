// Focused cold/warm UI/cache measurements. Never measures or certifies native capture.
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { pack } from '../pack.mjs';
const require=createRequire(import.meta.url),{synthetic}=require('./fixture.cjs');
const directory=path.dirname(fileURLToPath(import.meta.url)),root=path.resolve(directory,'../../..');
const output=path.resolve(process.argv[2]||path.join(root,'artifacts/project-explorer/browser-performance.json'));
await fs.mkdir(path.dirname(output),{recursive:true});
let playwright;try{playwright=require('playwright');}catch{playwright=require(process.env.PLAYWRIGHT_MODULE||path.join(os.homedir(),'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));}
const executablePath=process.env.EXPLORER_BROWSER||(process.platform==='win32'?'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe':undefined);
const browser=await playwright.chromium.launch({headless:true,executablePath,args:['--allow-file-access-from-files','--enable-precise-memory-info']});
const report={schemaVersion:'project-explorer-performance.v1',timestamp:new Date().toISOString(),evidenceKind:'synthetic_browser_cold_warm_performance',browser:browser.version(),executablePath,nodeVersion:process.version,viewport:{width:1440,height:1000},machine:{platform:process.platform,release:os.release(),cpu:os.cpus()[0]?.model,logicalCpuCount:os.cpus().length,totalMemoryBytes:os.totalmem()},methodology:{cold:'New browser context and fresh page for each node count; first model load. Chromium process itself is already started.',warm:'Second model load in the same page with the same pre-delivered dataset; includes snapshot validation and indexing again.',firstViewport:'Measured in browser from data already resident in a JS variable to two requestAnimationFrame callbacks after load; browser binding transfer is reported separately and is not native transport.',cache:'100 search and child queries after three warmup queries; p95 uses nearest-rank percentile. Both first-query and warm p95 are recorded.',graph:'500 nodes and 1000 edges; direct full graph render after data arrival, then second render in same page. Independent of native analysis.',memory:'Windows public APIs sample Chromium root+descendant working sets and private commit about every50ms. Sums may count shared pages multiple times; reported peak is the largest observed sample, not certified continuous peak. Browser JS heap is also sampled.',nativeCapture:'Not measured here. Use independent native qualification evidence.'},runs:[],errors:[],networkRequests:[]};
let sampler,samples=[],sampleBuffer='',samplerError='',browserRootPid;
const fixtureHtml=path.join(path.dirname(output),'performance-empty-viewer.html');await pack(null,fixtureHtml);
try{
  if(process.platform==='win32'){
    const cdp=await browser.newBrowserCDPSession();const processes=await cdp.send('SystemInfo.getProcessInfo');browserRootPid=processes.processInfo.find(p=>p.type==='browser')?.id;
    if(browserRootPid){sampler=spawn(process.env.EXPLORER_PYTHON||'python',[path.join(directory,'sample-browser-memory.py'),String(browserRootPid),'0.05'],{windowsHide:true,stdio:['pipe','pipe','pipe']});sampler.stdout.on('data',chunk=>{sampleBuffer+=chunk;let end;while((end=sampleBuffer.indexOf('\n'))>=0){const line=sampleBuffer.slice(0,end);sampleBuffer=sampleBuffer.slice(end+1);try{samples.push(JSON.parse(line));}catch{}}});sampler.stderr.on('data',data=>samplerError+=data);sampler.on('error',error=>samplerError+=error.message);}
  }
  for(const count of[1000,10000,50000]){
    const snapshot=synthetic(count),context=await browser.newContext({viewport:report.viewport,colorScheme:'light'}),page=await context.newPage();
    page.on('pageerror',error=>report.errors.push(error.message));page.on('request',request=>{if(/^https?:/.test(request.url()))report.networkRequests.push(request.url());});
    await page.goto(pathToFileURL(fixtureHtml).href);await page.waitForFunction(()=>!!globalThis.projectExplorer);
    const deliveryStart=performance.now();await page.evaluate(doc=>{globalThis.performanceSnapshot=doc;},snapshot);const browserBindingDeliveryMs=performance.now()-deliveryStart;
    const workloadStartedAtMs=Date.now();
    const measurements=await page.evaluate(async()=>{
      const doc=globalThis.performanceSnapshot,frames=()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))),now=()=>performance.now();
      async function load(){const start=now();await projectExplorer.load(doc,new ProjectExplorerClient.OfflineTransport(doc));await frames();return now()-start;}
      const coldFirstViewportMs=await load(),coldHeap=performance.memory?{used:performance.memory.usedJSHeapSize,total:performance.memory.totalJSHeapSize}:null;
      const coldDomRows=document.querySelectorAll('.tree-row').length;
      const index=projectExplorer.client.transport.index;
      const queries={search:[],children:[]};let started=now();index.search({text:'Object 000',pageSize:200});const firstCachedSearchMs=now()-started;started=now();index.childPage('root',0,200);const firstCachedChildMs=now()-started;
      for(let i=0;i<3;i++){index.search({text:'Object 000',pageSize:200});index.childPage('root',0,200);}
      for(let i=0;i<100;i++){started=now();index.search({text:'Object '+String(i%50).padStart(3,'0'),pageSize:200});queries.search.push(now()-started);started=now();index.childPage('root',i*13,200);queries.children.push(now()-started);}
      const p95=values=>[...values].sort((a,b)=>a-b)[Math.ceil(values.length*.95)-1];
      const warmFirstViewportMs=await load(),warmHeap=performance.memory?{used:performance.memory.usedJSHeapSize,total:performance.memory.totalJSHeapSize}:null;
      const warmDomRows=document.querySelectorAll('.tree-row').length;document.querySelector('#tree').scrollTop=1000000;await frames();const scrolledDomRows=document.querySelectorAll('.tree-row').length;
      const graph={nodes:doc.nodes.slice(0,500),edges:doc.edges.slice(0,1000),unresolved:[],limits:{maxNodes:500,maxEdges:1000}};projectExplorer.selected='root';projectExplorer.tab='relationships';projectExplorer.graph=graph;
      started=now();projectExplorer.renderGraph();await frames();const coldGraphRenderMs=now()-started;started=now();projectExplorer.renderGraph();await frames();const warmGraphRenderMs=now()-started;
      return{coldFirstViewportMs,warmFirstViewportMs,firstCachedSearchMs,firstCachedChildMs,warmCachedSearchP95Ms:p95(queries.search),warmCachedChildP95Ms:p95(queries.children),cachedQuerySamples:queries,graph:{nodes:graph.nodes.length,edges:graph.edges.length,coldRenderMs:coldGraphRenderMs,warmRenderMs:warmGraphRenderMs},domRows:{cold:coldDomRows,warm:warmDomRows,scrolled:scrolledDomRows},heapSamples:{cold:coldHeap,warm:warmHeap,afterGraph:performance.memory?{used:performance.memory.usedJSHeapSize,total:performance.memory.totalJSHeapSize}:null}};
    });
    const workloadCompletedAtMs=Date.now();
    const run={nodes:count,snapshotBytes:Buffer.byteLength(JSON.stringify(snapshot)),browserBindingDeliveryMs,workloadStartedAtMs,workloadCompletedAtMs,...measurements};
    run.targets={cachedQueriesP95Under200Ms:run.warmCachedSearchP95Ms<200&&run.warmCachedChildP95Ms<200,coldViewportUnder1000Ms:run.coldFirstViewportMs<1000,warmViewportUnder1000Ms:run.warmFirstViewportMs<1000,graphUnder2000Ms:run.graph.coldRenderMs<2000&&run.graph.warmRenderMs<2000,boundedDomRows:Math.max(...Object.values(run.domRows))<70};
    report.runs.push(run);await context.close();
  }
}catch(error){report.errors.push(error.stack);}finally{
  if(sampler){sampler.stdin.end('\n');await Promise.race([once(sampler,'exit').catch(()=>{}),new Promise(resolve=>setTimeout(()=>{if(sampler.exitCode===null)sampler.kill();resolve();},3000))]);}
  const measurements=samples.filter(s=>typeof s.workingSetBytes==='number');
  report.browserProcessMemory={rootPid:browserRootPid||null,available:measurements.length>0,sampleCount:measurements.length,requestedIntervalMs:50,peakSampledWorkingSetBytes:measurements.length?Math.max(...measurements.map(s=>s.workingSetBytes)):null,peakSampledPrivateCommitBytes:measurements.length?Math.max(...measurements.map(s=>s.privateCommitBytes)):null,maxObservedProcessCount:measurements.length?Math.max(...measurements.map(s=>s.processCount)):null,samplerError:samplerError||samples.find(s=>s.unavailable)?.unavailable||null,certifiedContinuousPeak:false};
  for(const run of report.runs){const during=measurements.filter(s=>s.timestampMs>=run.workloadStartedAtMs&&s.timestampMs<=run.workloadCompletedAtMs);run.processMemory={sampleCount:during.length,peakSampledWorkingSetBytes:during.length?Math.max(...during.map(s=>s.workingSetBytes)):null,peakSampledPrivateCommitBytes:during.length?Math.max(...during.map(s=>s.privateCommitBytes)):null};}
  await fs.writeFile(output.replace(/\.json$/,'-memory-samples.json'),JSON.stringify({methodology:report.methodology.memory,samples},null,2));
  report.passed=!report.errors.length&&!report.networkRequests.length&&report.runs.length===3&&report.runs.every(run=>Object.values(run.targets).every(Boolean));
  await fs.writeFile(output,JSON.stringify(report,null,2));console.log(JSON.stringify({...report,runs:report.runs.map(({cachedQuerySamples,...run})=>run)},null,2));await browser.close();if(!report.passed)process.exitCode=1;
}
