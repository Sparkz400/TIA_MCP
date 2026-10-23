// Detached saved-model performance and independent full-edge oracle; no TIA/native latency claim.
const fs=require('node:fs'),path=require('node:path'),os=require('node:os'),assert=require('node:assert/strict');
const {performance}=require('node:perf_hooks'),M=require(process.env.TIA_EXPLORER_MODEL || '../model.js'),{scaleFixture}=require('./relationship-fixture.cjs');
const output=process.argv[2];if(!output)throw new Error('Usage: node relationship-benchmark.cjs OUTPUT.json');
const report={evidenceKind:'synthetic_saved_relationship_scope',nativeQualified:false,desktopQualified:false,timestamp:new Date().toISOString(),node:process.version,platform:process.platform,cpu:os.cpus()[0]?.model,runs:[]};
for(const count of [1000,10000,50000]){
  const doc=scaleFixture(count);
  const saved=JSON.stringify(doc),start=performance.now(),index=new M.SnapshotIndex(JSON.parse(saved)),coldReopenAndIndexMs=performance.now()-start,times=[];
  let graph;
  for(let i=0;i<30;i++){const start=performance.now();graph=index.relationships('root',{depth:1,maxNodes:500,maxEdges:1000});times.push(performance.now()-start);}
  assert.equal(graph.truncated,false);assert.equal(graph.nodes.length,500);assert.equal(graph.edges.length,1000);assert.equal(graph.unresolved.length,2);assert.equal(graph.relationshipScope.projectCompleteness,'notEstablished');
  // Expected IDs originate from the authored star, not from traversal output or adjacency helpers.
  const expected=[];for(let i=1;i<=499;i++)expected.push('out-'+String(i).padStart(3,'0'),'back-'+String(i).padStart(3,'0'));expected.push('unresolved-1','unresolved-2');
  assert.deepEqual(graph.edges.map(e=>e.edgeId).sort(),expected.sort());assert.equal(M.bytes(graph),graph.payloadBytes);assert.ok(graph.payloadBytes<=M.LIMITS.graphBytes);
  const bounded=index.relationships('root',{depth:1,maxNodes:100,maxEdges:200});assert.equal(bounded.truncated,true);assert.ok(bounded.relationshipScope.stopReasons.includes('nodes'));assert.equal(JSON.stringify(doc),saved);
  const connected=scaleFixture(count,true);
  const connectedStart=performance.now(),connectedIndex=new M.SnapshotIndex(connected),connectedIndexMs=performance.now()-connectedStart;
  const wide=connectedIndex.relationships('root',{depth:1,maxNodes:500,maxEdges:1000});
  if(count>1000){assert.equal(wide.truncated,true);assert.ok(wide.relationshipScope.stopReasons.includes('edges'));assert.equal(wide.relationshipScope.retainedEdges,count);}
  assert.ok(M.bytes(wide)<=M.LIMITS.graphBytes);assert.ok(wide.nodes.length<=500);assert.ok(wide.edges.length<=1000);
  times.sort((a,b)=>a-b);report.runs.push({nodeCount:count,retainedEdges:doc.edges.length,snapshotBytes:Buffer.byteLength(saved),coldReopenAndIndexMs,connectedIndexMs,connectedStopReasons:wide.relationshipScope.stopReasons,connectedReturnedEdges:wide.edges.length,warmGraphP95Ms:times[Math.floor(times.length*.95)],warmRuns:times.length,graphNodes:graph.nodes.length,graphEdges:graph.edges.length,graphBytes:graph.payloadBytes,stopReasons:bounded.relationshipScope.stopReasons,processMemory:process.memoryUsage(),passesWarmTarget:times[Math.floor(times.length*.95)]<200});
}
report.passed=report.runs.every(r=>r.passesWarmTarget);fs.mkdirSync(path.dirname(path.resolve(output)),{recursive:true});fs.writeFileSync(output,JSON.stringify(report,null,2));console.log(JSON.stringify(report,null,2));if(!report.passed)process.exitCode=1;
