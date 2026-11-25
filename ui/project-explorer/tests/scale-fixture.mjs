// Synthetic stand-in for the native 1,000-function acceptance report (scripts/qualify_project_explorer_functions.py):
// the same report and project-model.v1 shape browser-scale.mjs checks, built in code. Never native qualification evidence.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');

export function scaleSnapshot(count = 1000) {
  const plcPath = '/projects/SyntheticScale/devices/PLC_1/software';
  const captured = '2026-11-25T10:00:00Z';
  const nodes = [
    { nodeId: 'project', parentId: null, kind: 'project', name: 'SyntheticScale', origin: 'native', objectPath: '/projects/SyntheticScale' },
    { nodeId: 'plc', parentId: 'project', kind: 'plc', name: 'PLC_1', origin: 'native', objectPath: plcPath, plcPath },
    { nodeId: 'blocks', parentId: 'plc', kind: 'blockGroup', name: 'Program blocks', origin: 'native', objectPath: `${plcPath}/blocks`, plcPath }
  ];
  const block = (nodeId, name, blockType, number, language, details) => ({ nodeId, parentId: 'blocks', kind: 'block', name, origin: 'native',
    objectPath: `${plcPath}/blocks/${name}`, plcPath, scopePath: plcPath, access: { state: 'available' }, metadata: { blockType, number, language }, ...(details ? { details } : {}) });
  for (let i = 0; i < count; i++) {
    const name = `TiaMcp_Index_Explorer_${String(i).padStart(4, '0')}`;
    nodes.push(block('fc-' + i, name, 'FC', 1000 + i, 'SCL', { status: 'cached', capturedAtUtc: captured, details: {
      sections: [{ name: 'Input', members: [{ name: 'Enable', dataType: 'Bool' }] }, { name: 'Return', members: [{ name: 'Ret_Val', dataType: 'Int', startValue: String(i) }] }],
      artifact: { mediaType: 'application/xml', path: `C:\\synthetic\\exports\\${name}.xml`, bytes: 2048, sha256: hash(name) },
      completeness: { scope: 'Synthetic block interface', truncated: false } } }));
  }
  nodes.push(block('main', 'Main', 'OB', 1, 'LAD'), block('gate', 'TiaMcp_EnableGate', 'FB', 10, 'SCL'), block('enabled', 'TiaMcp_IsEnabled', 'FC', 11, 'SCL'),
    block('gate-db', 'TiaMcp_EnableGate_DB', 'InstanceDB', 10, 'DB'));
  const at = id => ({ nodeId: id, objectPath: nodes.find(n => n.nodeId === id).objectPath });
  const evidence = (source, location, origin = 'parsedSupported', resolution = 'resolved') => ({ origin, resolution, sourcePath: at(source).objectPath, location, capturedAtUtc: captured,
    contentRevision: { algorithm: 'sha256', value: hash(source + location) } });
  const edges = [
    { edgeId: 'calls-main-gate', kind: 'calls', source: at('main'), target: at('gate'), evidence: evidence('main', 'Network 1 / call TiaMcp_EnableGate'), unresolved: null },
    { edgeId: 'calls-gate-enabled', kind: 'calls', source: at('gate'), target: at('enabled'), evidence: evidence('gate', 'Line 3 / TiaMcp_IsEnabled()'), unresolved: null },
    { edgeId: 'instance-gate-db', kind: 'instanceOf', source: at('gate-db'), target: at('gate'), evidence: evidence('gate-db', 'Instance DB of', 'nativeConfirmed'), unresolved: null },
    { edgeId: 'reads-gate-enable', kind: 'reads', source: at('gate'), target: null, evidence: evidence('gate', 'Line 2 / #Enable', 'parsedSupported', 'unresolved'),
      unresolved: { target: { name: 'Enable', scope: 'interface' }, reason: 'Interface member; members are not snapshot nodes.' } },
    { edgeId: 'writes-gate-active', kind: 'writes', source: at('gate'), target: null, evidence: evidence('gate', 'Line 4 / #Active :=', 'parsedSupported', 'unresolved'),
      unresolved: { target: { name: 'Active', scope: 'interface' }, reason: 'Interface member; members are not snapshot nodes.' } }
  ];
  const snapshot = { schemaVersion: 'project-model.v1', snapshotId: `synthetic-scale-${count}`, sessionGeneration: 'synthetic-session',
    project: { name: 'SyntheticScale', projectPath: 'C:\\synthetic\\SyntheticScale\\SyntheticScale.ap21', host: 'synthetic-host' },
    captureStartedAtUtc: captured, captureCompletedAtUtc: captured, expiresAtUtc: '2099-01-01T00:00:00Z',
    metadataRevision: { value: hash('synthetic-scale-metadata'), scope: 'exposedMetadata', includesContent: false },
    limits: { maxRows: 50000, maxDepth: 64, maxBytes: 33554432 }, usage: { visited: nodes.length, returned: nodes.length, nativeCaptureMs: 0 },
    coverage: [{ provider: 'synthetic', scope: 'browser-scale fixture', status: 'complete', visited: nodes.length, returned: nodes.length, warnings: [], continuationAvailable: false }],
    nodes, edges, warnings: ['Synthetic browser-scale fixture; not evidence of native Siemens support.'] };
  return { snapshot, plcPath };
}

/** Writes the synthetic snapshot and a report in the native acceptance shape into a new directory; returns the report path. */
export async function writeScaleReport(directory, count = 1000) {
  const { snapshot, plcPath } = scaleSnapshot(count);
  await fs.mkdir(path.dirname(directory), { recursive: true });
  await fs.mkdir(directory);
  const snapshotPath = path.join(directory, 'synthetic-scale.project-model.json'), bytes = Buffer.from(JSON.stringify(snapshot));
  await fs.writeFile(snapshotPath, bytes, { flag: 'wx' });
  const report = { schemaVersion: 1, synthetic: true, completed: true, generatedFunctionCount: count, plcPath,
    exports: { json: { path: snapshotPath, sha256: hash(bytes) } } };
  const reportPath = path.join(directory, 'synthetic-native-report.json');
  await fs.writeFile(reportPath, JSON.stringify(report, null, 2), { flag: 'wx' });
  return reportPath;
}
