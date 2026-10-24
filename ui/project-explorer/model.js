/* Plain-data project-model.v1 reader. No native proxies or engineering transport. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.ProjectExplorerModel = api;
})(typeof globalThis === 'object' ? globalThis : this, function () {
  'use strict';
  const SCHEMA = 'project-model.v1';
  const LIMITS = Object.freeze({ nodes: 50000, snapshotEdges: 50000, depth: 64, page: 200, graphNodes: 500, graphEdges: 1000, graphDepth: 5, graphBytes: 524288, bytes: 33554432 });
  const escape = value => String(value == null ? '' : value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const bytes = value => new TextEncoder().encode(JSON.stringify(value)).length;
  const clamp = (value, min, max, fallback) => Number.isFinite(Number(value)) ? Math.max(min, Math.min(max, Math.floor(Number(value)))) : fallback;
  function fail(code, message) { const error = new Error(message); error.code = code; throw error; }
  function validateSnapshot(input) {
    const doc = input && (input.snapshot || input);
    if (!doc || doc.schemaVersion !== SCHEMA) fail('unsupported_schema', 'Expected a project-model.v1 snapshot.');
    if (!doc.snapshotId || doc.sessionGeneration == null || !doc.project || !doc.project.projectPath) fail('invalid_snapshot', 'Missing snapshot, session or project-copy identity.');
    if (!Array.isArray(doc.nodes) || doc.nodes.length > LIMITS.nodes) fail('invalid_snapshot', 'Snapshot nodes must be an array of at most 50,000 objects.');
    if (!Array.isArray(doc.edges) || doc.edges.length > LIMITS.snapshotEdges) fail('invalid_snapshot', 'Snapshot edges must be an array of at most 50,000 relationships.');
    if (doc.metadataRevision && (doc.metadataRevision.includesContent !== false || doc.metadataRevision.scope !== 'exposedMetadata')) fail('invalid_revision', 'Metadata revision must explicitly exclude content.');
    const map = new Map();
    for (const node of doc.nodes) {
      if (!node.nodeId || map.has(node.nodeId) || typeof node.name !== 'string' || !node.kind) fail('invalid_node', 'Missing or duplicate node identity.');
      if (!['native', 'virtual'].includes(node.origin)) fail('invalid_origin', 'Node origin must distinguish native objects from virtual folders.');
      if (node.origin === 'native' && !node.objectPath) fail('invalid_reference', 'Native object is missing its canonical path.');
      if (node.origin === 'virtual' && node.objectPath) fail('invalid_reference', 'Virtual folders cannot carry engineering object paths.');
      map.set(node.nodeId, node);
    }
    const depth = new Map();
    for (const node of doc.nodes) {
      const visited = new Set(); let current = node; const chain = [];
      while (current && !depth.has(current.nodeId)) {
        if (visited.has(current.nodeId)) fail('invalid_hierarchy', 'Snapshot hierarchy contains a cycle.');
        visited.add(current.nodeId); chain.push(current);
        if (current.parentId && !map.has(current.parentId)) fail('invalid_hierarchy', 'Snapshot hierarchy contains an absent parent.');
        current = current.parentId ? map.get(current.parentId) : null;
      }
      let d = current ? depth.get(current.nodeId) : -1;
      for (let i = chain.length - 1; i >= 0; i--) { if (++d > LIMITS.depth) fail('invalid_hierarchy', 'Snapshot exceeds the depth limit.'); depth.set(chain[i].nodeId, d); }
    }
    const edgeIds = new Set();
    for (const edge of doc.edges) {
      if (!edge || typeof edge.edgeId !== 'string' || !edge.edgeId || edgeIds.has(edge.edgeId) || !edge.kind || !edge.evidence || typeof edge.evidence !== 'object' || Array.isArray(edge.evidence)) fail('invalid_edge', 'Relationships require unique edge IDs, a kind and evidence.');
      edgeIds.add(edge.edgeId);
      for (const endpoint of [edge.source, edge.target]) {
        if (endpoint == null) { if (!edge.unresolved || typeof edge.unresolved !== 'object' || Array.isArray(edge.unresolved)) fail('invalid_edge', 'Missing endpoints require explicit unresolved evidence.'); }
        else if (!map.has(endpoint.nodeId) || (endpoint.objectPath ?? null) !== (map.get(endpoint.nodeId).objectPath ?? null)) fail('invalid_edge', 'Relationship endpoint must match its exact captured node and path.');
      }
    }
    if (bytes(doc) > LIMITS.bytes) fail('snapshot_too_large', 'Snapshot exceeds the 32 MiB reader limit.');
    return doc;
  }
  function reference(snapshot, node) {
    if (!node || node.origin !== 'native' || !node.objectPath) fail('virtual_selection', 'Display folders are not engineering objects.');
    return { schemaVersion: SCHEMA, snapshotId: snapshot.snapshotId, sessionGeneration: snapshot.sessionGeneration, projectPath: snapshot.project.projectPath, nodeId: node.nodeId, objectPath: node.objectPath };
  }
  function exactNode(snapshot, map, ref) {
    if (!ref || ref.schemaVersion !== SCHEMA || ref.snapshotId !== snapshot.snapshotId || ref.sessionGeneration !== snapshot.sessionGeneration || ref.projectPath !== snapshot.project.projectPath) fail('stale_selection', 'Selection belongs to another snapshot, session or project copy. Select the object again.');
    const node = map.get(ref.nodeId);
    if (!node || node.origin !== 'native' || node.objectPath !== ref.objectPath) fail('stale_selection', 'The exact selected object is absent or changed. Same-named objects are never substituted.');
    return node;
  }
  class SnapshotIndex {
    constructor(input) {
      this.snapshot = validateSnapshot(input); this.nodes = new Map(); this.children = new Map(); this.searchRows = [];
      for (const node of this.snapshot.nodes) {
        this.nodes.set(node.nodeId, node);
        const key = node.parentId || ''; if (!this.children.has(key)) this.children.set(key, []); this.children.get(key).push(node);
        this.searchRows.push([node, [node.name, node.kind, node.objectPath, node.plcPath, node.scopePath].filter(Boolean).join('\n').toLocaleLowerCase()]);
      }
      const compare = (a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : a.nodeId < b.nodeId ? -1 : 1;
      for (const list of this.children.values()) list.sort(compare);
      this.adjacent = new Map();
      for (const edge of this.snapshot.edges || []) {
        const source=edge.source?.nodeId, target=edge.target?.nodeId;
        if(source) { let list=this.adjacent.get(source); if(!list)this.adjacent.set(source,list=[]); list.push(edge); }
        if(target && target!==source) { let list=this.adjacent.get(target); if(!list)this.adjacent.set(target,list=[]); list.push(edge); }
      }
      for (const edges of this.adjacent.values()) if(edges.length>1)edges.sort((a,b) => a.edgeId < b.edgeId ? -1 : a.edgeId > b.edgeId ? 1 : 0);
    }
    ancestors(nodeId) { const result = []; let node = this.nodes.get(nodeId); for (let i = 0; node && i <= LIMITS.depth; i++) { result.unshift(node); node = this.nodes.get(node.parentId); } return result; }
    childPage(parentId, offset = 0, pageSize = 200) { const list = this.children.get(parentId || '') || []; const size = clamp(pageSize, 1, LIMITS.page, 200); return { nodes: list.slice(offset, offset + size), totalMatches: list.length, nextOffset: offset + size < list.length ? offset + size : null }; }
    search({ text = '', kinds = [], scopePath = '', offset = 0, pageSize = 200 } = {}) {
      const query = text.toLocaleLowerCase(), hits = [], kindSet = new Set(kinds); let count = 0; const size = clamp(pageSize, 1, LIMITS.page, 200);
      for (const [node, searchable] of this.searchRows) {
        if ((query && !searchable.includes(query)) || (kindSet.size && !kindSet.has(node.kind)) || (scopePath && node.scopePath !== scopePath && node.plcPath !== scopePath)) continue;
        if (count >= offset && hits.length < size) hits.push(node); count++;
      }
      return { nodes: hits, totalMatches: count, nextOffset: offset + size < count ? offset + size : null, ancestors: hits.map(n => this.ancestors(n.nodeId).slice(0, -1)) };
    }
    flatten(expanded = new Set()) { const result = []; const stack = (this.children.get('') || []).slice().reverse().map(node => ({ node, depth: 0 })); while (stack.length) { const item = stack.pop(); result.push(item); if (expanded.has(item.node.nodeId)) for (const node of (this.children.get(item.node.nodeId) || []).slice().reverse()) stack.push({ node, depth: item.depth + 1 }); } return result; }
    relationships(nodeId, { kinds = [], direction = 'both', depth = 1, maxNodes = 100, maxEdges = 200, maxBytes = LIMITS.graphBytes } = {}) {
      if (!this.nodes.has(nodeId)) fail('selection_not_found', 'Graph seed is absent from this snapshot.');
      direction = ({ out: 'outgoing', in: 'incoming' })[direction] || direction;
      if (!['both','outgoing','incoming'].includes(direction)) fail('invalid_request', 'Unknown relationship direction.');
      if (!Array.isArray(kinds) || kinds.length > 32 || kinds.some(k => typeof k !== 'string' || k.length > 80)) fail('invalid_request', 'Invalid relationship kind filter.');
      const dmax = clamp(depth, 1, LIMITS.graphDepth, 1), nmax = clamp(maxNodes, 1, LIMITS.graphNodes, 100), emax = clamp(maxEdges, 1, LIMITS.graphEdges, 200), bmax = clamp(maxBytes, 1024, LIMITS.graphBytes, LIMITS.graphBytes);
      const found = new Set([nodeId]), edgeIds = new Set(), queue = [[nodeId,0]], allowed = new Set(kinds), reasons = new Set();
      const identity = id => { const {details,metadata,previews,...value} = this.nodes.get(id); return value; };
      const matches = (edge,id) => (!allowed.size || allowed.has(edge.kind)) && (direction === 'both' || direction === 'outgoing' && edge.source?.nodeId === id || direction === 'incoming' && edge.target?.nodeId === id);
      const result = { seedNodeId:nodeId, nodes:[identity(nodeId)], edges:[], unresolved:[], truncated:false, continuationAvailable:false,
        limits:{depth:dmax,maxNodes:nmax,maxEdges:emax,maxBytes:bmax}, coverage:this.snapshot.coverage || [], capturedAtUtc:this.snapshot.captureCompletedAtUtc, metadataOmitted:true,
        relationshipScope:{scope:'retainedSnapshotEdges', status:'complete', projectCompleteness:'notEstablished', retainedEdges:this.snapshot.edges.length, returnedEdges:0, direction, kinds:[...kinds], stopReasons:[]}, payloadBytes:0 };
      // Reserve final stop reasons and explanation before admitting evidence. Never drop the seed or rewrite source edges.
      const reserve = 512;
      let accounted = bytes(result) + reserve;
      if (accounted > bmax) { result.coverage=[]; reasons.add('coverageBytes'); accounted=bytes(result)+reserve; }
      if (accounted > bmax) fail('graph_capacity', 'Graph seed and scope exceed the byte budget; increase maxBytes.');
      for (let i=0; i<queue.length; i++) {
        const [id,distance] = queue[i];
        for (const edge of this.adjacent.get(id) || []) {
          if (!matches(edge,id) || edgeIds.has(edge.edgeId)) continue;
          if (distance >= dmax) { reasons.add('depth'); continue; }
          const extra = [...new Set([edge.source?.nodeId,edge.target?.nodeId].filter(n => n && !found.has(n)))];
          const unresolved=!!edge.unresolved, cost=bytes(edge)*(unresolved?2:1)+extra.reduce((total,n)=>total+bytes(identity(n))+1,0)+4;
          if (result.edges.length >= emax) { reasons.add('edges'); continue; }
          if (found.size+extra.length > nmax) { reasons.add('nodes'); continue; }
          if (accounted+cost > bmax) { reasons.add('bytes'); continue; }
          result.edges.push(edge); edgeIds.add(edge.edgeId); if(unresolved)result.unresolved.push(edge); accounted+=cost;
          for(const other of extra) { found.add(other); result.nodes.push(identity(other)); queue.push([other,distance+1]); }
        }
      }
      // A frontier edge may have been included later from another node at a shallower depth.
      reasons.delete('depth');
      if(queue.some(([id,distance])=>distance===dmax && (this.adjacent.get(id)||[]).some(edge=>matches(edge,id)&&!edgeIds.has(edge.edgeId))))reasons.add('depth');
      result.truncated=reasons.size>0; result.continuationAvailable=result.truncated;
      result.relationshipScope.status=result.truncated?'truncated':'complete'; result.relationshipScope.returnedEdges=result.edges.length; result.relationshipScope.stopReasons=[...reasons].sort();
      if(result.truncated)result.narrowScopeSuggestion='Select an adjacent node and expand, narrow relationship kinds, or increase the indicated budget within its ceiling.';
      while(result.payloadBytes!==bytes(result))result.payloadBytes=bytes(result);
      if(result.payloadBytes>bmax)fail('graph_capacity','Graph response exceeds its serialized byte budget.');
      return result;
    }
  }
  function exportManifest(snapshot, filters, selected) { return { schemaVersion: SCHEMA, exportedAtUtc: new Date().toISOString(), snapshotId: snapshot.snapshotId, sessionGeneration: snapshot.sessionGeneration, project: snapshot.project, captureStartedAtUtc: snapshot.captureStartedAtUtc, captureCompletedAtUtc: snapshot.captureCompletedAtUtc, metadataRevision: snapshot.metadataRevision, contentFreshness: 'Only individual evidence/content revisions apply; metadata is not a content revision.', filters, selection: selected || null, coverage: snapshot.coverage, warnings: snapshot.warnings || [] }; }
  function graphSvg(graph, manifest, selectedId) {
    const nodes = (graph.nodes || []).slice(0, LIMITS.graphNodes), edges = (graph.edges || []).slice(0, LIMITS.graphEdges);
    const columns = Math.max(1, Math.ceil(Math.sqrt(nodes.length || 1))), width = Math.max(720, columns * 228 + 50), height = Math.max(260, Math.ceil(nodes.length / columns) * 104 + 140);
    const positions = new Map(nodes.map((n, i) => [n.nodeId, { x: 28 + i % columns * 228, y: 110 + Math.floor(i / columns) * 104 }]));
    const links = edges.map(edge => {
      const a = positions.get(edge.source?.nodeId), b = positions.get(edge.target?.nodeId); if (!a || !b) return '';
      const dx = b.x - a.x, dy = b.y - a.y, ratio = Math.max(Math.abs(dx) / 95, Math.abs(dy) / 30);
      const d = ratio ? `M${a.x + 95 + dx / ratio},${a.y + 30 + dy / ratio} L${b.x + 95 - dx / ratio * 1.08},${b.y + 30 - dy / ratio * 1.08}` : `M${a.x + 150},${a.y} C${a.x + 220},${a.y - 42} ${a.x - 30},${a.y - 42} ${a.x + 35},${a.y - 2}`;
      return `<path d="${d}" stroke="${edge.evidence?.resolution === 'unresolved' ? '#805207' : '#53534d'}" stroke-width="2" fill="none" marker-end="url(#arrow)"><title>${escape(edge.kind)} · ${escape(edge.evidence?.sourcePath || 'Evidence unavailable')}</title></path>`;
    }).join('');
    const boxes = nodes.map(n => { const p = positions.get(n.nodeId); return `<g data-node-id="${escape(n.nodeId)}" tabindex="0" role="button" aria-label="${escape(n.name)}"><rect x="${p.x}" y="${p.y}" width="190" height="60" rx="0" stroke-width="2" fill="${n.nodeId === selectedId ? '#dfff00' : '#faf9f4'}" stroke="${n.nodeId === selectedId ? '#171717' : '#53534d'}"/><text x="${p.x + 12}" y="${p.y + 23}" font-size="13" fill="#171717">${escape(n.name.slice(0, 23))}</text><text x="${p.x + 12}" y="${p.y + 43}" font-size="11" fill="#53534d">${escape(n.kind)}</text><title>${escape(n.objectPath || n.name)}</title></g>`; }).join('');
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="Bounded relationship graph"><metadata>${escape(JSON.stringify(manifest))}</metadata><defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="#53534d"/></marker></defs><rect width="100%" height="100%" fill="#f2f0e8"/><text x="28" y="34" font-family="Segoe UI,sans-serif" font-size="18" fill="#171717">${escape(manifest.project?.name || 'Project')} · Relationships</text><text x="28" y="59" font-family="Segoe UI,sans-serif" font-size="11" fill="#53534d">${escape(manifest.captureCompletedAtUtc || '')} · ${nodes.length} nodes / ${edges.length} edges${graph.truncated ? ' · bounded / truncated' : ''}</text><g font-family="Segoe UI,sans-serif">${links}${boxes}</g></svg>`;
  }
  function pngManifest(pngBytes, manifest) {
    // Add an uncompressed UTF-8 iTXt chunk before IEND; retains a single portable PNG.
    const input = new Uint8Array(pngBytes), payload = new TextEncoder().encode('project-model-manifest\0\0\0\0\0' + JSON.stringify(manifest));
    const type = new TextEncoder().encode('iTXt'), chunk = new Uint8Array(payload.length + 12), view = new DataView(chunk.buffer); view.setUint32(0, payload.length); chunk.set(type, 4); chunk.set(payload, 8);
    let crc = 0xffffffff; for (let i = 4; i < chunk.length - 4; i++) { crc ^= chunk[i]; for (let j = 0; j < 8; j++) crc = crc >>> 1 ^ (crc & 1 ? 0xedb88320 : 0); } view.setUint32(chunk.length - 4, (crc ^ 0xffffffff) >>> 0);
    if (input.length < 20 || input[0] !== 137 || String.fromCharCode(...input.slice(-8, -4)) !== 'IEND') fail('invalid_png', 'PNG export is missing its terminal chunk.');
    const output = new Uint8Array(input.length + chunk.length); output.set(input.slice(0, -12)); output.set(chunk, input.length - 12); output.set(input.slice(-12), input.length - 12 + chunk.length); return output;
  }
  function networkSvg(content) {
    const nodes=(content.nodes||[]).slice(0,500),connections=(content.connections||[]).slice(0,1000),columns=Math.max(1,Math.min(5,Math.ceil(Math.sqrt(nodes.length)))),width=Math.max(650,columns*205+40),height=Math.max(230,Math.ceil(nodes.length/columns)*110+65);
    const positions=new Map(nodes.map((n,i)=>[n.key||n.id||String(i),{x:25+i%columns*205,y:60+Math.floor(i/columns)*110}]));
    const wires=connections.map(c=>{const endpoints=(c.endpoints||[c.from,c.to]).filter(Boolean).map(e=>({endpoint:e,position:positions.get(e.node||e.key)})).filter(e=>e.position);if(endpoints.length<2)return'';const x=endpoints.reduce((a,e)=>a+e.position.x+85,0)/endpoints.length,y=endpoints.reduce((a,e)=>a+e.position.y+35,0)/endpoints.length;return endpoints.map(e=>`<path d="M${x},${y} L${e.position.x+85},${e.position.y+35}" fill="none" stroke="${c.resolved===false?'#b98012':'#53534d'}"${c.resolved===false?' stroke-dasharray="5 3"':''}/><title>${escape(c.nativeUid||'Connection')} · ${escape(e.endpoint.node)}.${escape(e.endpoint.port||'unresolved port')}</title>`).join('');}).join('');
    const symbols={contact:'─| |─',coil:'─( )─',assign:'=',powerrail:'│',operand:'◇'};
    const boxes=nodes.map((n,i)=>{const p=positions.get(n.key||n.id||String(i));return `<g><rect x="${p.x}" y="${p.y}" width="170" height="74" rx="0" stroke-width="2" fill="${n.supported===false?'#fff5db':'#faf9f4'}" stroke="${n.supported===false?'#b98012':'#53534d'}"/><text x="${p.x+10}" y="${p.y+21}" font-size="12" fill="#171717">${escape((n.nativeName||n.name||n.kind||n.key||'Instruction').slice(0,22))}</text><text x="${p.x+10}" y="${p.y+43}" font-size="18" fill="#171717">${escape(symbols[n.kind]||'□')}</text><text x="${p.x+10}" y="${p.y+61}" font-size="9" fill="#53534d">${escape(n.key||n.id||'')} · ${n.supported===false?'opaque':'reader supported'}</text><title>${escape(JSON.stringify(n))}</title></g>`;}).join('');
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="Schematic ${escape(content.language||'graphical')} network"><rect width="100%" height="100%" fill="#f2f0e8"/><text x="25" y="28" fill="#53534d" font-family="Segoe UI,sans-serif" font-size="11">Schematic ${content.language==='GRAPH'?'document':'native'} connections · no inferred execution order or native coordinates</text><g font-family="Segoe UI,sans-serif">${wires}${boxes}</g></svg>`;
  }
  async function verifyGraphReport(report) {
    if (!report || report.format !== 'tia-graph-inspection.v1' || report.profile !== 'graph-v6-structure.v2' || report.nativeQualified !== false || report.fullSchemaValidated !== false ||
        !Array.isArray(report.graphs) || !Array.isArray(report.diagnostics) || !Array.isArray(report.unsupported) || typeof report.hasErrors !== 'boolean' || typeof report.hasUnsupportedContent !== 'boolean' ||
        !/^[a-f0-9]{64}$/.test(report.sourceSha256 || '') || !Number.isSafeInteger(report.sourceBytes) || report.sourceBytes < 1 || report.sourceBytes > 8 * 1024 * 1024 ||
        typeof report.originalSourceBase64 !== 'string' || report.originalSourceBase64.length > 11184812 || bytes(report) > 12 * 1024 * 1024)
      fail('invalid_graph_report', 'Use a bounded offline GRAPH v6 inspection report with retained source and false qualification flags.');
    let rows = report.graphs.length + report.diagnostics.length + report.unsupported.length, sequences = 0;
    for (const graph of report.graphs) {
      if (!graph || !Array.isArray(graph.sequences)) fail('invalid_graph_report', 'Missing sequence inventory.');
      sequences += graph.sequences.length;
      for (const sequence of graph.sequences) {
        if (!sequence || typeof sequence.path !== 'string') fail('invalid_graph_report', 'Missing exact sequence path.');
        for (const key of ['steps','transitions','branches','connections']) {
          if (!Array.isArray(sequence[key]) || sequence[key].some(row => !row || typeof row !== 'object' || Array.isArray(row))) fail('invalid_graph_report', 'Invalid sequence row inventory.');
          rows += sequence[key].length;
        }
      }
    }
    if (rows > 100000 || sequences > 2000) fail('graph_capacity', 'Report exceeds Explorer navigation limits; use the standalone HTML or paged Host report.');
    let binary; try { binary = atob(report.originalSourceBase64); } catch { fail('invalid_graph_report', 'Retained source is not valid base64.'); }
    if (binary.length !== report.sourceBytes) fail('graph_source_changed', 'Retained source length differs from its report.');
    const source = Uint8Array.from(binary, c => c.charCodeAt(0));
    if (!globalThis.crypto?.subtle) fail('hash_unavailable', 'SHA-256 verification is required for imported GRAPH reports.');
    const hash = [...new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', source))].map(n => n.toString(16).padStart(2,'0')).join('');
    if (hash !== report.sourceSha256) fail('graph_source_changed', 'Retained source SHA-256 differs from its report.');
    return source;
  }
  function graphSequenceNetwork(sequence, chartSequences = [sequence]) {
    const all = ['steps','transitions','branches'].flatMap((key,i) => sequence[key].map((row,index) => ({ row, key, index, kind:['Step','Transition','Branch'][i] })));
    const frequencies = new Map();
    for (const s of chartSequences) for (const [collection,kind] of [['steps','Step'],['transitions','Transition'],['branches','Branch']]) {
      if (kind==='Branch' && s!==sequence) continue;
      for (const row of s[collection]) { const key=kind+':'+row.number; frequencies.set(key,(frequencies.get(key)||0)+1); }
    }
    const nodes = all.slice(0,500).map(n => ({ ...n, collection:n.key, key:n.kind+':'+n.index, nativeName:n.kind+' '+(n.row.number ?? '?')+' '+String(n.row.name ?? ''),
      supported:Number.isSafeInteger(n.row.number)&&n.row.isMissing!==true&&frequencies.get(n.kind+':'+n.row.number)===1 }));
    const known = new Map(nodes.filter(n=>n.supported).map(n=>[n.kind+':'+n.row.number,n.key]));
    const connections = sequence.connections.slice(0,1000).map(c => {
      const resolved=known.has(c.from?.kind+':'+c.from?.number)&&known.has(c.to?.kind+':'+c.to?.number);
      return { nativeUid:String(c.ordinal), resolved, endpoints:resolved?[c.from,c.to].map(e=>({node:known.get(e.kind+':'+e.number),port:e.in ?? e.out ?? '?'})):[] };
    });
    return {language:'GRAPH',nodes,connections,totalNodes:all.length,totalConnections:sequence.connections.length};
  }
  function withGraphAttachments(snapshot, attachments) {
    const copy = structuredClone(snapshot), map = new Map(copy.nodes.map(n=>[n.nodeId,n]));
    for (const attachment of attachments) {
      const node = exactNode(copy,map,attachment.source);
      node.previews = [...(node.previews || []).filter(a=>a.kind!=='graphSequence'||a.reportFileSha256!==attachment.reportFileSha256), structuredClone(attachment)];
    }
    validateSnapshot(copy); return copy;
  }
  async function verifyUaObservation(doc) {
    const bad = () => fail('invalid_ua_observation', 'Invalid UA observation report, original chain, binding or derived summary.');
    const same = (a,b,depth=0) => {
      if(a===b)return true;
      if(depth>48 || !a || !b || typeof a!=='object' || typeof b!=='object' || Array.isArray(a)!==Array.isArray(b))return false;
      const keys=Object.keys(a);return keys.length===Object.keys(b).length && keys.every(k=>Object.hasOwn(b,k)&&same(a[k],b[k],depth+1));
    };
    const flags=['atomicAcrossSignals','scanSynchronized','programMatchVerified','addressMatchVerified','datatypeMatchVerified','sourceAuthenticityVerified','physicalQualified','clockSynchronizationVerified','assertionsAdmitted','causationProven','bugFixProven','nativeQualification','nativeAccessAttempted','sourceCompletenessVerified'];
    const checkClaims=(value,depth=0)=>{
      if(depth>64)bad();
      if(!value||typeof value!=='object')return;
      for(const [key,child] of Object.entries(value)){
        if((flags.includes(key)||key==='nativeExecutable'||key==='onlineVerified'||key==='liveVerified')&&child!==false)bad();
        checkClaims(child,depth+1);
      }
    };
    const hash=async data=>Array.from(new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256',data)),b=>b.toString(16).padStart(2,'0')).join('');
    if(!globalThis.crypto?.subtle)fail('hash_unavailable','SHA-256 verification is required for UA reports.');
    if(bytes(doc)>8*1024*1024 || doc?.schemaVersion!=='diagnostic-ua-observation.v1' || !flags.every(k=>doc[k]===false) || doc.completeCommittedPrefixRetained!==true || doc.sourceEvidenceRevalidated!==true || typeof doc.limits!=='string')bad();
    checkClaims(doc);
    const source=doc.captureSource,binding=doc.binding,mode=doc.acquisitionMode;
    if(!['polling','subscription'].includes(mode)||source?.acquisitionMode!==mode||source.snapshotRef!==doc.snapshotRef||source.rowsTruncated!==false||source.currentHeadConsulted!==false||!Array.isArray(source.rows)||source.rows.length>10000||!Array.isArray(source.originalFiles))bad();
    const ref=doc.snapshotRef.match(/^(capture|ua-subscription):([a-f0-9]{32}):(0|[1-9][0-9]*):([a-f0-9]{64})$/);
    if(!ref || ref[1] !== (mode==='polling'?'capture':'ua-subscription') || Number(ref[3])!==source.segmentCount || source.segmentCount>10001 || source.originalFiles.length!==source.segmentCount+1)bad();
    async function original(file,maximum) {
      if(!file || typeof file.base64!=='string' || file.base64.length>Math.ceil(maximum/3)*4 || !Number.isSafeInteger(file.bytes)||file.bytes<1||file.bytes>maximum||!/^[a-f0-9]{64}$/.test(file.sha256))bad();
      let binary;try{binary=atob(file.base64);}catch{bad();}
      if(binary.length!==file.bytes || btoa(binary)!==file.base64)bad();
      const raw=Uint8Array.from(binary,c=>c.charCodeAt(0));
      if(await hash(raw)!==file.sha256)bad();
      try{return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(raw));}catch{bad();}
    }
    const directory=mode==='polling'?'runtime-ua-acquisition/runtime-captures':'runtime-ua-subscriptions';
    let previous=null,total=0;const rawRows=[];let lastProgress;
    for(let i=0;i<source.originalFiles.length;i++){
      const file=source.originalFiles[i];total+=file.bytes;
      if(total>3*1024*1024||file.relativePath!==`${directory}/${ref[2]}/${i===0?'plan.json':String(i).padStart(6,'0')+'.segment'}`)bad();
      const value=await original(file,i===0?128*1024:1024*1024);
      if(i===0){if(!same(value,source.plan)||source.planSha256!==file.sha256)bad();}
      else {if(value.index!==i||value.previousSha256!==previous||!Array.isArray(value.rows))bad();rawRows.push(...value.rows);lastProgress=value.progress;if(rawRows.length>10000)bad();}
      previous=file.sha256;
    }
    if(previous!==ref[4]||source.chainSha256!==ref[4]||source.originalByteCount!==total||!same(rawRows,source.rows)||source.recordedProgress?.rows!==rawRows.length||source.segmentCount>0&&!same(lastProgress,source.recordedProgress))bad();
    if(source.segmentCount===0){
      const initial={state:'running',terminalReason:null,elapsedMilliseconds:0,observedUtc:source.plan.createdUtc,rows:0,
        ...(mode==='polling'?{readAttempts:0,skippedPolls:0,droppedReadResults:0}:{notifications:0,duplicateMessages:0,missingSequences:0,remoteCleanupConfirmed:false})};
      if(!same(initial,source.recordedProgress))bad();
    }
    if(!['running','ended'].includes(source.recordedProgress.state)||source.uncommittedDataUnknown!==(source.recordedProgress.state==='running'))bad();
    const request=mode==='polling'?source.plan.uaContext?.request:source.plan.request;
    if(!request||request.diagnosticBindingId!==doc.bindingId||binding?.schemaVersion!=='diagnostic-ua-binding.v1'||binding.experimentId!==doc.experimentId||binding.attemptId!==request.attemptId||binding.request?.attemptId!==request.attemptId||binding.associationKind!=='callerSelectedObservationIntent')bad();
    if(!['programMatchVerified','addressMatchVerified','datatypeMatchVerified','sourceAuthenticityVerified','physicalQualified','clockSynchronizationVerified','assertionsAdmitted','nativeAccessAttempted'].every(k=>binding[k]===false)||!same(await original(doc.bindingArtifact,8*1024*1024),binding)||doc.bindingArtifact.sha256!==doc.bindingId)bad();
    const mappings=binding.mappings,nodes=request.nodes;
    if(!Array.isArray(mappings)||mappings.length<1||mappings.length>64||!Array.isArray(nodes)||nodes.length!==mappings.length||!Array.isArray(binding.request.mappings)||binding.request.mappings.length!==mappings.length||!Array.isArray(doc.signals)||doc.signals.length!==mappings.length)bad();
    if(new Set(mappings.map(m=>m.signalId)).size!==mappings.length||new Set(nodes.map(n=>JSON.stringify([n.namespaceUri,n.identifierType,n.identifier]))).size!==nodes.length)bad();
    const declarations=binding.experiment?.diagnosticCase?.signals;
    if(!['nativeExecutable','nativeAccessAttempted','sourceAuthenticityVerified'].every(k=>binding.experiment?.[k]===false)||binding.experiment?.diagnosticCase?.sourceRevision?.onlineVerified!==false)bad();
    if(!Array.isArray(declarations)||!Array.isArray(binding.experiment.diagnosticCase.hypotheses))bad();
    for(let i=0;i<mappings.length;i++){
      const m=mappings[i],r=binding.request.mappings[i],d=declarations.filter(d=>d.id===m.signalId);
      if(!same(m.node,nodes[i])||!same(r.node,nodes[i])||m.signalId!==r.signalId||m.unit!==r.unit||m.reason!==r.reason||typeof m.unit!=='string'||!m.unit.trim()||d.length!==1||!same(d[0],m.declaration)||m.declaration.resolution!=='capturedDeclaration'||m.declaration.evidence?.liveVerified!==false)bad();
    }
    const observations=nodes.map(()=>[]),errors=nodes.map(()=>0);
    const events={gapRows:0,duplicateMessages:0,keepalives:0,failureEvents:0,overflowNotifications:0};
    const creation=mode==='subscription'?rawRows.find(r=>r.event?.kind==='created')?.event.created:null;
    const item=(r,v,started=null,completed=null)=>({rowSequence:r.sequence,elapsedMilliseconds:r.elapsedMilliseconds,observedUtc:r.observedUtc,readStartedAt:started,readCompletedAt:completed,value:v});
    for(let i=0;i<rawRows.length;i++){
      const r=rawRows[i];if(r.sequence!==i||!Number.isFinite(r.elapsedMilliseconds)||r.elapsedMilliseconds<0||i>0&&r.elapsedMilliseconds<rawRows[i-1].elapsedMilliseconds)bad();
      if(mode==='polling'){
        if(r.kind==='gap'){events.gapRows++;continue;}
        const index=source.plan.request.selectionIds.indexOf(r.selectionId);
        if(index<0||index>=nodes.length||r.kind!=='read')bad();
        if(r.errorCode!==null){if(typeof r.errorCode!=='string'||r.readResultJson!==null)bad();errors[index]++;continue;}
        const read=JSON.parse(r.readResultJson);
        checkClaims(read);
        if(!Array.isArray(read.items)||read.items.length!==1||read.attemptId!==request.attemptId||!same(read.items[0].node,nodes[index]))bad();
        observations[index].push(item(r,read.items[0],read.startedAt,read.completedAt));
      }else{
        const e=r.event;if(!e||!['created','publish','republish','gap','deleted','failure'].includes(e.kind)||typeof e.duplicate!=='boolean'||typeof e.keepAlive!=='boolean')bad();
        if(e.kind==='gap')events.gapRows++;
        if(e.kind==='failure')events.failureEvents++;
        if(e.duplicate){events.duplicateMessages++;continue;}
        if(e.keepAlive){events.keepalives++;continue;}
        for(const n of e.notifications||[]){
          const index=n.clientHandle-1;
          if(!Number.isInteger(index)||index<0||index>=nodes.length||!same(n.value?.node,nodes[index])||typeof n.overflow!=='boolean')bad();
          if(n.overflow)events.overflowNotifications++;
          observations[index].push({...item(r,n.value),messageSequenceNumber:e.sequenceNumber,publishTime:e.publishTime,eventKind:e.kind,overflow:n.overflow});
        }
      }
    }
    for(let i=0;i<nodes.length;i++){
      const list=observations[i],count=f=>list.filter(x=>f(x.value)).length;
      for(const {value:v} of list){
        if(!/^0x[0-9a-f]{8}$/.test(v.statusCode)||!['value','itemError','metadataError','unsupported'].includes(v.outcome))bad();
        const status=parseInt(v.statusCode.slice(2),16),quality=(status&0x80000000)?'Bad':(status&0x40000000)?'Uncertain':'Good';
        if(quality!==v.quality||quality==='Bad'&&(v.outcome!=='itemError'||v.value!==null)||v.outcome!=='value'&&(v.value!==null||v.encoding!==null))bad();
        if(v.encoding==='null'&&v.value!==null)bad();
        if(v.outcome==='value'&&v.encoding!=='null'&&['Int64','UInt64'].includes(v.builtInType)){
          if(v.encoding!=='decimalInteger'||typeof v.value!=='string'||! /^-?(0|[1-9][0-9]*)$/.test(v.value)||v.value==='-0')bad();
          const n=BigInt(v.value);if(n<(v.builtInType==='UInt64'?0n:-9223372036854775808n)||n>(v.builtInType==='UInt64'?18446744073709551615n:9223372036854775807n))bad();
        }
      }
      const expected={signalId:mappings[i].signalId,node:nodes[i],unit:mappings[i].unit,savedDatatype:mappings[i].declaration.datatype,
        monitoredItem:creation?.items?.[i]||null,observationCount:list.length,hostReadErrors:errors[i],
        goodValues:count(v=>v.quality==='Good'&&v.outcome==='value'&&v.encoding!=='null'),uncertain:count(v=>v.quality==='Uncertain'),bad:count(v=>v.quality==='Bad'),
        unsupported:count(v=>v.outcome==='unsupported'),metadataErrors:count(v=>v.outcome==='metadataError'),nullValues:count(v=>v.encoding==='null'),
        missingSourceTimestamps:count(v=>v.sourceTimestamp===null),missingServerTimestamps:count(v=>v.serverTimestamp===null),
        observedBuiltInTypes:[...new Set(list.map(x=>x.value.builtInType).filter(t=>typeof t==='string'&&t.length))].sort(),
        firstObservation:list[0]||null,lastObservation:list.at(-1)||null,coverage:list.length?'recordedObservationsOnly':'noSelectedObservations'};
      if(!same(expected,doc.signals[i]))bad();
    }
    const expectedHypotheses=binding.experiment.diagnosticCase.hypotheses;
    if(!same(events,doc.events)||!same(declarations.map(s=>s.id).filter(id=>!mappings.some(m=>m.signalId===id)),doc.unselectedSignalIds)||!Array.isArray(doc.hypotheses)||doc.hypotheses.length!==expectedHypotheses.length||doc.hypotheses.some((h,i)=>h.verdict!=='unresolved'||h.id!==expectedHypotheses[i].id||h.statement!==expectedHypotheses[i].statement))bad();
    return doc;
  }
  return { SCHEMA, LIMITS, escape, bytes, clamp, fail, validateSnapshot, reference, exactNode, SnapshotIndex, exportManifest, graphSvg, pngManifest, networkSvg, verifyUaObservation,
    verifyGraphReport, graphSequenceNetwork, withGraphAttachments };
});
