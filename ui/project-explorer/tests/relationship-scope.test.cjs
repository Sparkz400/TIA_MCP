const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const M = require(process.env.TIA_EXPLORER_MODEL || '../model.js');
const { fixture } = require('./fixture.cjs');

const { oracleFixture } = require('./relationship-fixture.cjs');
const ids=graph=>graph.edges.map(e=>e.edgeId).sort().join(',');
function qualify(doc) {
  const original=JSON.stringify(doc),index=new M.SnapshotIndex(doc);
  let g=index.relationships('A',{depth:5,maxNodes:500,maxEdges:1000});
  assert.equal(ids(g),'ab,ab-second,bc,ca,incoming,missing-source,missing-target,read,self');assert.equal(g.unresolved.length,2);assert.equal(g.truncated,false);
  assert.equal(g.relationshipScope.projectCompleteness,'notEstablished');
  g=index.relationships('A',{kinds:['calls'],direction:'outgoing',depth:1,maxNodes:500,maxEdges:1000});
  assert.equal(ids(g),'ab,ab-second,missing-target,self');assert.deepEqual(g.relationshipScope.stopReasons,['depth']);assert.equal(g.continuationAvailable,true);
  g=index.relationships('A',{kinds:['calls'],direction:'incoming',depth:1,maxNodes:500,maxEdges:1000});assert.equal(ids(g),'ca,incoming,missing-source,self');assert.equal(g.truncated,true);
  g=index.relationships('A',{kinds:['calls'],direction:'out',depth:3,maxNodes:500,maxEdges:1000});assert.equal(ids(g),'ab,ab-second,bc,ca,missing-target,self');assert.equal(g.truncated,false);
  for(const [options,reason] of [[{maxNodes:1},'nodes'],[{maxEdges:1},'edges']]){g=index.relationships('A',{depth:5,...options});assert.equal(g.truncated,true);assert.ok(g.relationshipScope.stopReasons.includes(reason));}
  g=index.relationships('A',{kinds:['unsupported'],depth:5});assert.equal(ids(g),'');assert.equal(g.relationshipScope.projectCompleteness,'notEstablished');
  assert.equal(JSON.stringify(doc),original,'Queries must preserve source observations');
}
test('independently expected scoped edges and depth/node/edge truncation survive save/reopen',()=>{qualify(JSON.parse(JSON.stringify(oracleFixture())));});
test('C# store export opens with identical expected edges and retained partial provider coverage',(t)=>{
  if(!process.env.TIA_RELATIONSHIP_EVIDENCE){t.skip('Set TIA_RELATIONSHIP_EVIDENCE to a completed ProjectModel.Tests export directory');return;}
  const path=require('node:path').join(process.env.TIA_RELATIONSHIP_EVIDENCE,'store-export.json'),doc=JSON.parse(fs.readFileSync(path,'utf8'));qualify(doc);
  assert.equal(doc.coverage.find(c=>c.provider==='nativeRelationships').status,'partial');
});
test('virtual endpoint paths accept only nullish absence in all omitted/null combinations',()=>{
  for(const nodeNull of [false,true])for(const endpointNull of [false,true]){
    const doc=oracleFixture(),node={nodeId:'display',parentId:'root',name:'Display folder',kind:'virtualFolder',origin:'virtual'},endpoint={nodeId:'display'};
    if(nodeNull)node.objectPath=null;if(endpointNull)endpoint.objectPath=null;
    doc.nodes.push(node);doc.edges.push({...doc.edges[0],edgeId:'virtual-edge',kind:'contains',source:endpoint,target:{nodeId:'A',objectPath:'/fixture/A'}});
    const graph=new M.SnapshotIndex(JSON.parse(JSON.stringify(doc))).relationships('A',{kinds:['contains']});
    assert.equal(ids(graph),'virtual-edge');assert.equal(graph.nodes.length,2);
    for(const badPath of ['', '/invented']){const malformed=structuredClone(doc);malformed.edges.at(-1).source.objectPath=badPath;assert.throws(()=>new M.SnapshotIndex(malformed),/Relationship endpoint/);}
  }
  for(const missing of [null,undefined]){const doc=oracleFixture();doc.edges[0].target.objectPath=missing;assert.throws(()=>new M.SnapshotIndex(doc),/Relationship endpoint/);}
});
test('C# virtual endpoint export preserves null path without inventing a native identity',(t)=>{
  if(!process.env.TIA_RELATIONSHIP_EVIDENCE){t.skip('Set TIA_RELATIONSHIP_EVIDENCE to a completed ProjectModel.Tests export directory');return;}
  const file=require('node:path').join(process.env.TIA_RELATIONSHIP_EVIDENCE,'virtual-endpoint-export.json'),doc=JSON.parse(fs.readFileSync(file,'utf8'));
  assert.equal(Object.hasOwn(doc.nodes.find(n=>n.nodeId==='display'),'objectPath'),false);
  assert.equal(doc.edges.find(e=>e.edgeId==='virtual-edge').source.objectPath,null);
  const graph=new M.SnapshotIndex(doc).relationships('A',{kinds:['contains']});assert.equal(ids(graph),'virtual-edge');assert.equal(graph.nodes.length,2);
});
test('duplicate identities, exact endpoint mismatches and unmarked missing endpoints fail closed',()=>{
  for(const mutate of [d=>d.edges.push(d.edges[0]),d=>d.edges[0].target.objectPath='/same-name-other',d=>d.edges[0].target.nodeId='absent',d=>d.edges[5].unresolved=null,d=>d.edges[0].source=null]){const d=oracleFixture();mutate(d);assert.throws(()=>new M.SnapshotIndex(d),/Relationship|Missing/);}
  const index=new M.SnapshotIndex(oracleFixture());assert.throws(()=>index.relationships('absent'),/seed/);assert.throws(()=>index.relationships('A',{direction:'wrong'}),/direction/);
});
test('byte bound preserves seed and exact edge evidence, accounts duplicated unresolved records',()=>{
  const doc=oracleFixture();doc.edges.find(e=>e.edgeId==='missing-target').evidence.text='x'.repeat(8000);const original=JSON.stringify(doc);
  const graph=new M.SnapshotIndex(doc).relationships('A',{depth:5,maxNodes:500,maxEdges:1000,maxBytes:4096});
  assert.ok(graph.nodes.some(n=>n.nodeId==='A'));assert.ok(graph.relationshipScope.stopReasons.includes('bytes'));assert.ok(!graph.edges.some(e=>e.edgeId==='missing-target'));assert.equal(graph.payloadBytes,M.bytes(graph));assert.ok(M.bytes(graph)<=4096);
  const retained=new Set(graph.nodes.map(n=>n.nodeId));assert.ok(graph.edges.every(e=>[e.source,e.target].filter(Boolean).every(n=>retained.has(n.nodeId))));assert.equal(JSON.stringify(doc),original);
  doc.coverage[0].warnings=['w'.repeat(5000)];const bounded=new M.SnapshotIndex(doc).relationships('A',{maxBytes:4096});assert.ok(bounded.relationshipScope.stopReasons.includes('coverageBytes'));assert.deepEqual(bounded.coverage,[]);
});
module.exports={oracleFixture,qualify};

