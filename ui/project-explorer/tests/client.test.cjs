const { test } = require('node:test');
const assert = require('node:assert/strict');
const { fixture } = require('./fixture.cjs');
const M = require('../model.js'), C = require('../client.js');
test('embedded content reports changed size to the pinned host and stops on teardown', async () => {
  let listener, resized, disconnected = false, height = 640;
  const messages = [], body = { getBoundingClientRect: () => ({ height }) };
  const parent = { postMessage(message, origin) {
    messages.push({ message, origin });
    if (message.method === 'ui/initialize') queueMicrotask(() => listener({ source: parent, origin: 'https://host.example', data: { jsonrpc: '2.0', id: message.id, result: { protocolVersion: '2026-01-26', hostCapabilities: { serverTools: {} } } } }));
  } };
  const win = { parent, document: { body, documentElement: { clientWidth: 420 } },
    ResizeObserver: class { constructor(callback) { resized = callback; } observe(target) { assert.equal(target, body); resized(); } disconnect() { disconnected = true; } },
    addEventListener: (_type, fn) => listener = fn, removeEventListener() {} };
  const transport = await C.connectMcpHost({ windowObject: win });
  const settle = () => new Promise(resolve => setTimeout(resolve, 10));
  const sizes = () => messages.filter(row => row.message.method === 'ui/notifications/size-changed');
  await settle(); assert.deepEqual(sizes().map(row => row.message.params), [{ width: 420, height: 640 }]);
  assert.equal(sizes()[0].origin, 'https://host.example');
  resized(); await settle(); assert.equal(sizes().length, 1);
  height = 900; resized(); resized(); await settle(); assert.equal(sizes().length, 2);
  assert.equal(sizes()[1].message.params.height, 900);
  height = 1000; resized(); transport.close('test complete'); await settle();
  assert.equal(disconnected, true); assert.equal(sizes().length, 2);
});
test('viewer transport blocks all mutation tools and unsupported actions before dispatch', async () => { let count = 0; const client = new C.ReadOnlyClient({ invoke: () => count++ }); for (const action of ['change_apply', 'change_prepare', 'download', 'hardware_write', 'project_close', 'tool']) await assert.rejects(client.invoke(action), /read operations only/); assert.equal(count, 0); });
test('offline cursor is bound to query and released snapshot cannot be reused', async () => { const transport = new C.OfflineTransport(fixture()), client = new C.ReadOnlyClient(transport); const page = await client.invoke('children', { parentId: 'project', pageSize: 1 }); assert.ok(page.nextCursor); await assert.rejects(client.invoke('children', { parentId: 'hardware', pageSize: 1, cursor: page.nextCursor }), /Cursor/); await client.invoke('release'); await assert.rejects(client.invoke('status'), /released/); });
test('offline metadata never claims live content verification', async () => { const transport = new C.OfflineTransport(fixture()); const reference = M.reference(transport.snapshot, transport.index.nodes.get('motor-a')); const detail = await transport.invoke('details', { reference }); assert.equal(detail.contentRevision, null); assert.equal(detail.contentFreshness, 'historicalObservationOnly'); assert.equal((await transport.invoke('verify', { reference })).valid, false); });
test('stale or inconclusive verification prevents sending exact selection', async () => { let sent = 0; for (const reply of [{ valid: false }, { stale: true }, { status: 'expired' }, {}]) { const client = new C.ReadOnlyClient({ invoke: async () => reply, sendSelection: async () => sent++ }); await assert.rejects(client.sendSelection({ snapshotId: 'old' })); } assert.equal(sent, 0); });
test('metadata mismatch is rejected even when snapshot header validity remains true', async () => { let sent=false; for(const reply of [{valid:true,matchesCurrentMetadata:false},{valid:true,freshness:'stale'}]){const client=new C.ReadOnlyClient({invoke:async()=>reply,sendSelection:async()=>sent=true});await assert.rejects(client.sendSelection({snapshotId:'old'}));}assert.equal(sent,false); });
test('offline native detail observations retain provider previews and own content hashes',async()=>{const doc=fixture();doc.nodes[6].details={capturedAtUtc:'2026-10-08T11:00:00Z',contentRevision:{value:'independent-content',scope:'nativeExport'},details:{sections:[{name:'Input',members:[{name:'Enable',dataType:'Bool'}]}]},previews:[{provider:'typedLadFbd',status:'unsupported'}]};const transport=new C.OfflineTransport(doc);const result=await transport.invoke('details',{reference:M.reference(doc,doc.nodes[6])});assert.equal(result.contentRevision.value,'independent-content');assert.equal(result.details.sections[0].members[0].name,'Enable');assert.equal(result.previews[0].provider,'typedLadFbd');assert.equal(result.capturedAtUtc,'2026-10-08T11:00:00Z');});
test('verified selection uses same exact reference and session loss is visible', async () => { let actual; const ref = { snapshotId: 's', objectPath: 'block:ExactCase' }; const client = new C.ReadOnlyClient({ invoke: async () => ({ valid: true }), sendSelection: async r => actual = r }); await client.sendSelection(ref); assert.strictEqual(actual, ref); const failed = new C.ReadOnlyClient({ invoke: async () => { const e = new Error('Worker session lost'); e.code = 'session_lost'; throw e; } }); await assert.rejects(failed.invoke('status')); assert.equal(failed.state, 'disconnected'); });
test('MCP SDK adapter requires negotiated host support and fixes tool/action routes', async () => { assert.throws(() => C.mcpAppTransport({ getHostCapabilities: () => undefined }), /negotiated/); let call; const adapter = C.mcpAppTransport({ getHostCapabilities: () => ({ serverTools: {} }), callServerTool: async p => call = p }); await adapter.invoke('search', { text: 'Motor', kinds: ['block'], pageSize: 200 }); assert.deepEqual(call, { name: 'project_model', arguments: { request: { text: 'Motor', kind: 'block', maxItems: 200, action: 'search' } } }); assert.throws(() => adapter.invoke('change_apply', {}), /not allowed/); });
test('live relationships map display directions to native in/out while offline keeps its own vocabulary', async () => {
  const calls=[],adapter=C.mcpAppTransport({getHostCapabilities:()=>({serverTools:{}}),callServerTool:async p=>calls.push(p)}),offline=new C.OfflineTransport(fixture());
  for(const [direction,native,expected] of [['incoming','in',['call-1']],['outgoing','out',['alias-1','io-1']],['both','both',['alias-1','call-1','io-1']]]){
    const args={nodeId:'motor-a',direction};await adapter.invoke('relationships',args);assert.equal(calls.at(-1).arguments.request.direction,native);assert.equal(args.direction,direction);
    assert.deepEqual((await offline.invoke('relationships',args)).edges.map(e=>e.edgeId).sort(),expected);
  }
});
test('protocol bridge negotiates and pins parent origin; unrelated messages cannot resolve calls', async () => {
  let listener, notification, dispatched = [], closed = false;
  const parent = { postMessage(message, origin) { dispatched.push({ message, origin }); if (message.method === 'ui/initialize') queueMicrotask(() => listener({ source: parent, origin: 'https://host.example', data: { jsonrpc: '2.0', id: message.id, result: { protocolVersion: '2026-01-26', hostCapabilities: { serverTools: {} }, hostInfo: { name: 'fixture' } } } })); else if (message.method === 'tools/call') queueMicrotask(() => { listener({ source: {}, origin: 'https://evil.example', data: { jsonrpc: '2.0', id: message.id, result: { forged: true } } }); listener({ source: parent, origin: 'https://host.example', data: { jsonrpc: '2.0', id: message.id, result: { valid: true } } }); }); } };
  const win = { parent, addEventListener: (_t, fn) => listener = fn, removeEventListener: () => closed = true };
  const transport = await C.connectMcpHost({ windowObject: win, onResult: r => notification = r });
  const result = await transport.invoke('verify', { snapshotId: 's' }); assert.equal(result.valid, true); assert.equal(result.forged, undefined); assert.equal(dispatched[0].origin, '*'); assert.equal(dispatched.at(-1).origin, 'https://host.example');
  assert.throws(() => transport.invoke('change_apply', {})); transport.close('test complete'); assert.equal(closed, true);
});
test('bounded artifact transfer verifies every chunk and final SHA-256',async()=>{const {createHash}=require('node:crypto'),bytes=Buffer.from('native snapshot ä'.repeat(9000)),hash=createHash('sha256').update(bytes).digest('hex'),manifest={path:'D:\\host-artifacts\\snapshot.json',bytes:bytes.length,sha256:hash};let calls=0;const output=await C.readArtifactChunks(manifest,async args=>{calls++;assert.equal(args.path,manifest.path);assert.equal(args.count,65536);const part=bytes.subarray(args.offset,args.offset+args.count);return{relativePath:'snapshot.json',bytes:bytes.length,sha256:hash,offset:args.offset,count:part.length,nextOffset:args.offset+part.length,complete:args.offset+part.length===bytes.length,encoding:'base64',data:part.toString('base64')};});assert.deepEqual(Buffer.from(output),bytes);assert.ok(calls>1);});
test('artifact transfer rejects oversized, changed, stalled and tampered bytes without retry',async()=>{const {createHash}=require('node:crypto'),bytes=Buffer.from('safe'),hash=createHash('sha256').update(bytes).digest('hex'),manifest={path:'host/snapshot.json',bytes:4,sha256:hash};await assert.rejects(C.readArtifactChunks({...manifest,bytes:33554433},()=>{}),/32 MiB/);for(const mutate of [p=>p.sha256='0'.repeat(64),p=>p.offset=1,p=>p.nextOffset=0,p=>p.data=Buffer.from('evil').toString('base64')]){let calls=0;await assert.rejects(C.readArtifactChunks(manifest,async()=>{calls++;const piece={relativePath:'snapshot.json',bytes:4,sha256:hash,offset:0,count:4,nextOffset:4,complete:true,encoding:'base64',data:bytes.toString('base64')};mutate(piece);return piece;}));assert.equal(calls,1);}});
