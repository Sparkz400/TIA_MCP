// Synthetic, independently described UI fixture. Never used as native qualification.
const fixture = () => ({
  schemaVersion: 'project-model.v1', snapshotId: 'fixture-snapshot-001', sessionGeneration: 'fixture-session-A',
  project: { name: 'Packaging line · UI fixture', projectPath: 'D:\\Fixture Projects\\Packaging\\Packaging.ap21', host: 'fixture-host' },
  captureStartedAtUtc: '2026-10-08T10:00:00Z', captureCompletedAtUtc: '2026-10-08T10:00:02Z', expiresAtUtc: '2099-10-08T11:00:00Z',
  metadataRevision: { value: 'fixture-metadata-hash', scope: 'exposedMetadata', includesContent: false },
  limits: { maxRows: 50000, maxDepth: 64, maxBytes: 33554432 }, usage: { visited: 14, returned: 14, nativeCaptureMs: 2000 },
  coverage: [
    { provider: 'nativeHierarchy', scope: 'Fixture project', status: 'complete', visited: 14, returned: 14, warnings: [], continuationAvailable: false },
    { provider: 'staticAnalysis', scope: 'PLC_A', status: 'partial', visited: 3, returned: 3, warnings: ['One indirect alias remains unresolved.'], continuationAvailable: false },
    { provider: 'hmi', scope: 'Operator panel', status: 'unsupported', visited: 1, returned: 0, warnings: ['HMI adapter pending native qualification.'], continuationAvailable: false },
    { provider: 'typedLadFbd', scope: 'MotorControl', status: 'unavailable', visited: 0, returned: 0, warnings: ['Typed preview provider unavailable.'], continuationAvailable: false }
  ],
  nodes: [
    { nodeId: 'project', parentId: null, kind: 'project', name: 'Packaging line', origin: 'native', objectPath: 'project:Packaging' },
    { nodeId: 'hardware', parentId: 'project', kind: 'virtualFolder', name: 'Hardware inventory', origin: 'virtual' },
    { nodeId: 'device', parentId: 'hardware', kind: 'device', name: 'Main cabinet', origin: 'native', objectPath: 'device:Main%20cabinet', metadata: { typeIdentifier: 'Fixture S7-1500', physicalTopology: 'unverified' } },
    { nodeId: 'io', parentId: 'device', kind: 'deviceItem', name: 'Digital inputs', origin: 'native', objectPath: 'deviceItem:Main%20cabinet/Digital%20inputs', metadata: { address: '%I0.0', channels: 16 } },
    { nodeId: 'plc-a', parentId: 'project', kind: 'plc', name: 'PLC_A', origin: 'native', objectPath: 'plc:PLC_A', plcPath: 'plc:PLC_A' },
    { nodeId: 'blocks-a', parentId: 'plc-a', kind: 'blockGroup', name: 'Program blocks', origin: 'native', objectPath: 'group:PLC_A/Blocks', plcPath: 'plc:PLC_A' },
    { nodeId: 'motor-a', parentId: 'blocks-a', kind: 'block', name: 'MotorControl', origin: 'native', objectPath: 'block:PLC_A/Blocks/MotorControl', plcPath: 'plc:PLC_A', scopePath: 'plc:PLC_A', access: { state: 'available' }, metadata: { number: 10, language: 'SCL', blockType: 'FB', author: 'Fixture author' } },
    { nodeId: 'main', parentId: 'blocks-a', kind: 'block', name: 'Main', origin: 'native', objectPath: 'block:PLC_A/Blocks/Main', plcPath: 'plc:PLC_A', metadata: { number: 1, language: 'LAD', blockType: 'OB' } },
    { nodeId: 'protected', parentId: 'blocks-a', kind: 'block', name: 'ProtectedLogic', origin: 'native', objectPath: 'block:PLC_A/Blocks/ProtectedLogic', access: { state: 'protected' } },
    { nodeId: 'empty', parentId: 'blocks-a', kind: 'blockGroup', name: 'Empty native group', origin: 'native', objectPath: 'group:PLC_A/Blocks/Empty' },
    { nodeId: 'plc-b', parentId: 'project', kind: 'plc', name: 'PLC_B', origin: 'native', objectPath: 'plc:PLC_B', plcPath: 'plc:PLC_B' },
    { nodeId: 'motor-b', parentId: 'plc-b', kind: 'block', name: 'MotorControl', origin: 'native', objectPath: 'block:PLC_B/Unit%20A/Namespace/MotorControl', plcPath: 'plc:PLC_B', scopePath: 'unit:PLC_B/Unit%20A' },
    { nodeId: 'hmi', parentId: 'project', kind: 'device', name: 'Operator panel', origin: 'native', objectPath: 'device:Operator%20panel', access: { state: 'unsupported' }, metadata: { softwareFamily: 'HMI unspecified', internals: 'unsupported' } },
    { nodeId: 'hostile', parentId: 'blocks-a', kind: 'tag', name: 'ÄÖÜ <script>alert(1)</script> & "$&"', origin: 'native', objectPath: 'tag:PLC_A/Tags/%3Cscript%3E', metadata: { declarationValue: '<img src=x onerror=alert(2)>' } }
  ],
  edges: [
    { edgeId: 'call-1', source: { nodeId: 'main', objectPath: 'block:PLC_A/Blocks/Main' }, target: { nodeId: 'motor-a', objectPath: 'block:PLC_A/Blocks/MotorControl' }, kind: 'calls', evidence: { origin: 'parsedSupported', resolution: 'resolved', sourcePath: 'block:PLC_A/Blocks/Main', location: 'Network 1 / call', capturedAtUtc: '2026-10-08T10:00:01Z', contentRevision: { algorithm: 'sha256', value: 'fixture-content-hash' } }, unresolved: null },
    { edgeId: 'io-1', source: { nodeId: 'motor-a', objectPath: 'block:PLC_A/Blocks/MotorControl' }, target: { nodeId: 'io', objectPath: 'deviceItem:Main%20cabinet/Digital%20inputs' }, kind: 'configuredIoAssociation', evidence: { origin: 'nativeConfirmed', resolution: 'resolved', sourcePath: 'block:PLC_A/Blocks/MotorControl', location: 'Fixture configured association', capturedAtUtc: '2026-10-08T10:00:01Z', contentRevision: null }, unresolved: null },
    { edgeId: 'alias-1', source: { nodeId: 'motor-a', objectPath: 'block:PLC_A/Blocks/MotorControl' }, target: null, kind: 'reads', evidence: { origin: 'parsedSupported', resolution: 'unresolved', sourcePath: 'block:PLC_A/Blocks/MotorControl', location: 'Line 24', capturedAtUtc: '2026-10-08T10:00:01Z', contentRevision: null }, unresolved: { text: 'IndirectAxis', reason: 'Dynamic alias cannot be resolved.' } }
  ], warnings: ['Synthetic UI qualification fixture; not evidence of native Siemens support.']
});
function synthetic(count) {
  const doc = fixture(); doc.snapshotId = `synthetic-${count}`; doc.project.name = `Synthetic ${count.toLocaleString()} objects`; doc.nodes = [{ nodeId: 'root', parentId: null, kind: 'project', name: 'Synthetic project', origin: 'native', objectPath: 'project:Synthetic' }]; doc.edges = []; doc.coverage = [{ provider: 'synthetic', scope: 'performance fixture', status: 'complete', visited: count, returned: count, warnings: [], continuationAvailable: false }];
  for (let i = 1; i < count; i++) doc.nodes.push({ nodeId: 'n' + i, parentId: 'root', kind: i % 100 === 0 ? 'blockGroup' : 'block', name: 'Object ' + String(i).padStart(6, '0'), origin: 'native', objectPath: `block:Synthetic/Scope${i % 2}/Object${i}`, plcPath: `plc:PLC_${i % 2}`, access: { state: 'available' } });
  for (let i = 1; i < Math.min(count - 1, 500); i++) doc.edges.push({ edgeId: 'e' + i, source: { nodeId: i === 1 ? 'root' : 'n' + (i - 1), objectPath: i === 1 ? 'project:Synthetic' : `block:Synthetic/Scope${(i - 1) % 2}/Object${i - 1}` }, target: { nodeId: 'n' + i, objectPath: `block:Synthetic/Scope${i % 2}/Object${i}` }, kind: 'calls', evidence: { origin: 'synthetic', resolution: 'resolved', sourcePath: 'fixture', capturedAtUtc: doc.captureCompletedAtUtc, contentRevision: null } });
  for (let i = doc.edges.length; i < 1000 && count >= 500; i++) { const source=i%499+1, target=(i*17)%499+1; doc.edges.push({edgeId:'extra'+i,source:{nodeId:'n'+source,objectPath:`block:Synthetic/Scope${source%2}/Object${source}`},target:{nodeId:'n'+target,objectPath:`block:Synthetic/Scope${target%2}/Object${target}`},kind:'reads',evidence:{origin:'synthetic',resolution:'resolved',sourcePath:'fixture',capturedAtUtc:doc.captureCompletedAtUtc,contentRevision:null}}); }
  return doc;
}
module.exports = { fixture, synthetic };
