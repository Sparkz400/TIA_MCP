const { performance } = require('node:perf_hooks');
const os = require('node:os');
const fs = require('node:fs');
const path = require('node:path');
const M = require('../model.js');
const { synthetic } = require('./fixture.cjs');
function p95(values) { return values.sort((a,b) => a-b)[Math.floor(values.length * .95)]; }
const report = { evidenceKind: 'synthetic_cache_performance_only', timestamp: new Date().toISOString(), runtime: process.version, machine: { platform: process.platform, release: os.release(), cpu: os.cpus()[0]?.model, logicalCpuCount: os.cpus().length, memoryBytes: os.totalmem() }, nativeCapture: 'Measured independently by native qualification; these timings do not certify native latency.', runs: [] };
for (const count of [1000,10000,50000]) {
  const snapshot = synthetic(count), start = performance.now(), index = new M.SnapshotIndex(snapshot), coldIndexMs = performance.now() - start, searches = [], children = [];
  for (let i=0;i<50;i++) { let t = performance.now(); index.search({ text: 'Object 00' + (i%10), pageSize: 200 }); searches.push(performance.now()-t); t=performance.now(); index.childPage('root', i*17,200); children.push(performance.now()-t); }
  const graph = { nodes: snapshot.nodes.slice(0,500), edges: snapshot.edges.slice(0,1000) }, t = performance.now(); const svg = M.graphSvg(graph,M.exportManifest(snapshot,{},null)); const graphSvgMs = performance.now()-t;
  report.runs.push({ count, snapshotBytes:M.bytes(snapshot), coldIndexMs, warmSearchP95Ms:p95(searches), warmChildrenP95Ms:p95(children), graphNodes:graph.nodes.length, graphEdges:graph.edges.length, graphSvgMs, graphSvgBytes:Buffer.byteLength(svg), processMemory:process.memoryUsage(), passesCacheTarget:p95(searches)<200 && p95(children)<200 });
}
const output = process.argv[2] || path.resolve(__dirname,'../../../artifacts/project-explorer/cache-performance.json'); fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(report,null,2));console.log(JSON.stringify(report,null,2));if(report.runs.some(r=>!r.passesCacheTarget))process.exitCode=1;
