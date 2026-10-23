const { test } = require('node:test');
const assert = require('node:assert/strict');
const { fixture, synthetic } = require('./fixture.cjs');
const M = require('../model.js');
const { graphFixture } = require('./graph-fixture.cjs');
test('independent hierarchy preserves native empty groups, explicit virtual folder and ancestry', () => {
  const index = new M.SnapshotIndex(fixture());
  assert.deepEqual(index.ancestors('empty').map(n => n.nodeId), ['project', 'plc-a', 'blocks-a', 'empty']);
  assert.deepEqual(index.childPage('empty').nodes, []);
  assert.equal(index.nodes.get('empty').origin, 'native');
  assert.equal(index.nodes.get('hardware').origin, 'virtual');
  assert.throws(() => M.reference(index.snapshot, index.nodes.get('hardware')), /Display folders/);
});
test('same-name objects have exact case-sensitive distinct refs; cross-session/copy/rename rejected', () => {
  const index = new M.SnapshotIndex(fixture()), a = M.reference(index.snapshot, index.nodes.get('motor-a')), b = M.reference(index.snapshot, index.nodes.get('motor-b'));
  assert.notEqual(a.objectPath, b.objectPath);
  for (const [key, value] of [['snapshotId', 'new'], ['sessionGeneration', 'new'], ['projectPath', a.projectPath.toLowerCase()], ['objectPath', b.objectPath], ['nodeId', 'motor-b']]) assert.throws(() => M.exactNode(index.snapshot, index.nodes, { ...a, [key]: value }), /Selection|selected/);
});
test('every child page appears once and search preserves scope and Unicode', () => {
  const index = new M.SnapshotIndex(synthetic(1001)); let offset = 0, result = [];
  while (offset !== null) { const page = index.childPage('root', offset, 137); result.push(...page.nodes); offset = page.nextOffset; }
  assert.equal(result.length, 1000); assert.equal(new Set(result.map(n => n.nodeId)).size, 1000);
  const special = new M.SnapshotIndex(fixture()); assert.equal(special.search({ text: 'ÄÖÜ' }).nodes[0].nodeId, 'hostile'); assert.equal(special.search({ text: 'MotorControl', scopePath: 'plc:PLC_A' }).totalMatches, 1);
});
test('cyclic edges terminate, graph limits enforced and unresolved endpoints retained', () => {
  const doc = fixture(); doc.edges.push({ ...doc.edges[0], edgeId: 'back', source: doc.edges[0].target, target: doc.edges[0].source }); const index = new M.SnapshotIndex(doc);
  const graph = index.relationships('motor-a', { depth: 5, maxNodes: 500, maxEdges: 1000 }); assert.equal(graph.edges.length, 4); assert.equal(graph.unresolved.length, 1);
  const limited = index.relationships('motor-a', { depth: 5, maxNodes: 1, maxEdges: 1 }); assert.equal(limited.truncated, true); assert.ok(limited.nodes.length <= 1); assert.ok(limited.edges.length <= 1);
});
test('invalid hierarchy, virtual mutation identities and false content claims are rejected', () => {
  for (const mutate of [d => d.nodes[0].parentId = 'empty', d => d.nodes[0].parentId = 'missing', d => d.nodes[1].objectPath = 'invented', d => d.metadataRevision.includesContent = true, d => d.nodes.push(d.nodes[0])]) { const doc = fixture(); mutate(doc); assert.throws(() => M.validateSnapshot(doc)); }
});
test('SVG safely escapes project labels and carries coverage, filters and capture provenance', () => {
  const doc = fixture(); doc.project.name = '<script>alert(1)</script>'; const index = new M.SnapshotIndex(doc), graph = { nodes: doc.nodes, edges: doc.edges }; const manifest = M.exportManifest(doc, { kind: 'calls' }, null); const svg = M.graphSvg(graph, manifest, 'motor-a');
  assert.ok(!svg.includes('<script>')); assert.ok(svg.includes('&lt;script&gt;')); assert.ok(svg.includes('coverage')); assert.ok(svg.includes('captureCompletedAtUtc')); assert.ok(svg.includes('calls'));
});
test('PNG export embeds Unicode manifest in a standalone iTXt chunk',()=>{const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jV9sAAAAASUVORK5CYII=','base64');const output=M.pngManifest(png,{name:'Mötör',coverage:[{status:'partial'}]});const text=Buffer.from(output).toString('utf8');assert.ok(text.includes('project-model-manifest'));assert.ok(text.includes('Mötör'));assert.ok(text.includes('partial'));assert.equal(Buffer.from(output).subarray(-8,-4).toString(),'IEND');});
test('offline graph bounds full response including duplicated unresolved evidence and drops details',()=>{const doc=fixture();doc.nodes[6].details={content:'x'.repeat(700000)};doc.edges[2].evidence.native={text:'x'.repeat(300000)};const index=new M.SnapshotIndex(doc),graph=index.relationships('motor-a',{maxNodes:500,maxEdges:1000,maxBytes:524288});assert.ok(M.bytes(graph)<=524288);assert.equal(graph.truncated,true);assert.equal(graph.nodes.some(n=>'details'in n||'metadata'in n),false);assert.equal(graph.edges.some(e=>e.edgeId==='alias-1'),false);assert.ok(graph.narrowScopeSuggestion);});
test('snapshot rejects more than 50000 saved relationship records independently of graph response limits',()=>{const doc=fixture();doc.edges=Array.from({length:50001},(_,i)=>({...doc.edges[0],edgeId:'edge'+i}));assert.throws(()=>M.validateSnapshot(doc),/50,000/);});
test('GRAPH verifies retained bytes while rejecting qualification claims, tampering and malformed inventories',async()=>{
  const report=graphFixture();assert.deepEqual(Buffer.from(await M.verifyGraphReport(report)),Buffer.from(report.originalSourceBase64,'base64'));
  for(const mutate of [r=>r.nativeQualified=true,r=>r.fullSchemaValidated=true,r=>r.sourceSha256='a'.repeat(64),r=>r.sourceBytes++,r=>r.originalSourceBase64='!',r=>r.graphs[0].sequences[0].steps=[null],r=>r.graphs[0].sequences[0].connections=null,r=>r.graphs[0].sequences=Array(2001).fill(r.graphs[0].sequences[0])]){
    const bad=structuredClone(report);mutate(bad);await assert.rejects(()=>M.verifyGraphReport(bad));
  }
});
test('GRAPH connection resolution respects chart step scope, local branch scope, missing and duplicate identities',()=>{
  const a={steps:[{number:1,name:'<script>'}],transitions:[{number:1}],branches:[{number:1}],connections:[{ordinal:1,from:{kind:'Step',number:1},to:{kind:'Branch',number:1}}]}, b={steps:[{number:2}],transitions:[],branches:[{number:1}],connections:[]};
  assert.equal(M.graphSequenceNetwork(a,[a,b]).connections[0].resolved,true);
  b.steps[0].number=1;assert.equal(M.graphSequenceNetwork(a,[a,b]).connections[0].resolved,false);
  b.steps[0].number=2;a.steps[0].isMissing=true;assert.equal(M.graphSequenceNetwork(a,[a,b]).connections[0].resolved,false);
  a.steps[0].isMissing=false;a.connections[0].to={kind:'Step',number:2};assert.equal(M.graphSequenceNetwork(a,[a,b]).connections[0].resolved,false);
  assert.ok(!M.networkSvg(M.graphSequenceNetwork(a,[a,b])).includes('<script>'));
  assert.match(M.networkSvg(M.graphSequenceNetwork(a,[a,b])),/Schematic document connections/);
  a.steps=Array.from({length:501},(_,i)=>({number:i+1}));assert.equal(M.graphSequenceNetwork(a).nodes.length,500);
});
test('GRAPH attachment export keeps original capture immutable and exact selection scoped',()=>{
  const snapshot=fixture(),original=JSON.stringify(snapshot),index=new M.SnapshotIndex(snapshot);
  const attachment={kind:'graphSequence',reportFileSha256:'b'.repeat(64),source:M.reference(snapshot,index.nodes.get('motor-a')),content:graphFixture()};
  const copy=M.withGraphAttachments(snapshot,[attachment]);assert.equal(copy.nodes.find(n=>n.nodeId==='motor-a').previews.length,1);assert.equal(JSON.stringify(snapshot),original);
  assert.equal(M.withGraphAttachments(copy,[attachment]).nodes.find(n=>n.nodeId==='motor-a').previews.length,1);
  attachment.source.snapshotId='another';assert.throws(()=>M.withGraphAttachments(snapshot,[attachment]),/Selection|selected/);
});
test('checked-in Explorer entry point contains the current source assets',async()=>{
  const fs=require('node:fs/promises'),os=require('node:os'),path=require('node:path');
  const {pack}=await import('../pack.mjs');const temp=await fs.mkdtemp(path.join(os.tmpdir(),'explorer-pack-'));
  try{
    const target=path.join(temp,'index.html');await pack(null,target);
    assert.deepEqual(await fs.readFile(path.join(__dirname,'../index.html')),await fs.readFile(target),'Run node ui/project-explorer/pack.mjs --empty ui/project-explorer/index.html after changing viewer assets');
  }finally{await fs.rm(temp,{recursive:true,force:true});}
});
