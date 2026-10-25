(function (root, factory) {
  const api = factory(typeof module === 'object' && module.exports ? require('./model.js') : root.ProjectExplorerModel);
  if (typeof module === 'object' && module.exports) module.exports = api; else root.ProjectExplorerClient = api;
})(typeof globalThis === 'object' ? globalThis : this, function (M) {
  'use strict';
  const ACTIONS = new Set(['capture', 'status', 'children', 'search', 'details', 'relationships', 'verify', 'release', 'export']);
  function unwrap(result) {
    if (result?.isError) M.fail('tool_error', result.content?.find(c => c.type === 'text')?.text || 'Project model request failed.');
    let value = result?.structuredContent || result;
    if (result?.content && !result.structuredContent) { const text = result.content.find(c => c.type === 'text')?.text; if (text) { try { value = JSON.parse(text); } catch { M.fail('invalid_response', text); } } }
    if (value?.error && !value.status) M.fail(value.error.code || 'model_error', value.error.message || String(value.error));
    return value?.result || value;
  }
  class ReadOnlyClient {
    constructor(transport) { if (!transport || typeof transport.invoke !== 'function') throw new TypeError('A transport invoke(action,args) function is required.'); this.transport = transport; this.state = transport.offline ? 'offline' : 'connected'; }
    async invoke(action, args = {}) {
      if (!ACTIONS.has(action)) M.fail('read_only', 'The explorer allows project-model read operations only.');
      try { const result = unwrap(await this.transport.invoke(action, args)); return result; }
      catch (error) { if (/session|disconnect|worker|transport|timeout/i.test(error.code + ' ' + error.message)) this.state = 'disconnected'; throw error; }
    }
    async sendSelection(ref) {
      if (!this.transport.sendSelection) M.fail('host_unavailable', 'No verified chat host is connected. Copy the exact reference instead.');
      const verification = await this.invoke('verify', { snapshotId: ref.snapshotId, reference: ref });
      if (verification.valid === false || verification.stale || verification.matchesCurrentMetadata === false || verification.freshness === 'stale' || ['stale', 'expired', 'invalid', 'released', 'sessionLost', 'unavailable'].includes(verification.status)) M.fail('stale_selection', 'Selection verification failed. Recapture and select the exact object again.');
      if (verification.valid !== true && verification.status !== 'valid' && verification.status !== 'current') M.fail('unverified_selection', 'The host did not confirm this exact reference.');
      await this.transport.sendSelection(ref); return verification;
    }
    async readArtifact(manifest, onProgress) {
      if (!this.transport.readArtifact) M.fail('host_unavailable', 'This transport cannot transfer host artifacts.');
      return this.transport.readArtifact(manifest, onProgress);
    }
  }
  async function readArtifactChunks(manifest, read, onProgress = () => {}) {
    if (!manifest || typeof manifest.path !== 'string' || !/^[a-f0-9]{64}$/i.test(manifest.sha256 || '') || !Number.isSafeInteger(manifest.bytes) || manifest.bytes < 0 || manifest.bytes > M.LIMITS.bytes) M.fail('invalid_artifact', 'Artifact must have an exact host path, SHA-256 and size within 32 MiB.');
    if (!globalThis.crypto?.subtle) M.fail('hash_unavailable', 'This browser cannot verify SHA-256. Use the artifact transfer tools.');
    const expectedHash=manifest.sha256.toLowerCase(), data=new Uint8Array(manifest.bytes); let offset=0, relativePath;
    do {
      const piece=unwrap(await read({path:manifest.path,offset,count:65536}));
      if(piece.bytes!==manifest.bytes||String(piece.sha256).toLowerCase()!==expectedHash||piece.offset!==offset||piece.encoding!=='base64'||typeof piece.data!=='string'||piece.data.length>87384) M.fail('artifact_changed','Artifact identity, size, hash or chunk boundary changed during transfer.');
      if(relativePath!==undefined && piece.relativePath!==relativePath)M.fail('artifact_changed','Artifact path changed during transfer.'); relativePath=piece.relativePath;
      let binary; try{binary=atob(piece.data);}catch{M.fail('invalid_artifact','Artifact chunk contains invalid base64.');}
      if(binary.length>65536||piece.count!==binary.length||piece.nextOffset!==offset+binary.length||piece.nextOffset>data.length||piece.complete!==(piece.nextOffset===data.length)||(!binary.length&&!piece.complete)) M.fail('invalid_artifact','Artifact chunk has invalid paging or progress.');
      for(let i=0;i<binary.length;i++)data[offset+i]=binary.charCodeAt(i); offset=piece.nextOffset;onProgress(offset,data.length);
      if(piece.complete)break;
    } while(offset<data.length);
    const actual=[...new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256',data))].map(v=>v.toString(16).padStart(2,'0')).join('');
    if(actual!==expectedHash)M.fail('artifact_hash_mismatch','Transferred artifact failed its SHA-256 integrity check.');
    return data;
  }
  class OfflineTransport {
    constructor(snapshot) { this.index = new M.SnapshotIndex(snapshot); this.snapshot = this.index.snapshot; this.offline = true; this.released = false; this.cursors = new Map(); this.sequence = 0; }
    page(page, identity) {
      const result = { ...page, schemaVersion: M.SCHEMA, snapshotId: this.snapshot.snapshotId, coverage: this.snapshot.coverage, cached: true, offline: true }; delete result.nextOffset;
      if (page.nextOffset != null) { const token = `local:${++this.sequence}:${this.snapshot.snapshotId}`; this.cursors.set(token, { identity, offset: page.nextOffset }); result.nextCursor = token; } else result.nextCursor = null;
      return result;
    }
    offset(cursor, identity) { if (!cursor) return 0; const entry = this.cursors.get(cursor); if (!entry || entry.identity !== identity) M.fail('invalid_cursor', 'Cursor does not belong to this snapshot and query.'); return entry.offset; }
    async invoke(action, args = {}) {
      if (!ACTIONS.has(action)) M.fail('read_only', 'Unsupported read-only action.');
      if (this.released && action !== 'release') M.fail('released', 'Snapshot was released. Open another snapshot.');
      if (args.snapshotId && args.snapshotId !== this.snapshot.snapshotId) M.fail('stale_snapshot', 'Snapshot ID is stale.');
      const ref = args.reference;
      if (ref) M.exactNode(this.snapshot, this.index.nodes, ref);
      switch (action) {
        case 'capture': M.fail('offline_capture', 'An offline artifact cannot recapture TIA. Open a newly exported snapshot.'); break;
        case 'status': return { ...this.snapshot, roots: this.index.childPage(null).nodes, offline: true };
        case 'children': { const parentId = args.parentId || ref?.nodeId || null, key = JSON.stringify(['children', parentId, args.pageSize || 200]); return this.page(this.index.childPage(parentId, this.offset(args.cursor, key), args.pageSize), key); }
        case 'search': { const query = { text: args.text || '', kinds: args.kinds || [], scopePath: args.scopePath || '', pageSize: args.pageSize || 200 }; const key = JSON.stringify(query); return this.page(this.index.search({ ...query, offset: this.offset(args.cursor, key) }), key); }
        case 'details': { const node = ref ? M.exactNode(this.snapshot, this.index.nodes, ref) : this.index.nodes.get(args.nodeId); if (!node) M.fail('missing_node', 'Exact object is absent.'); const observed = node.details || {}; return { schemaVersion: M.SCHEMA, reference: node.origin === 'native' ? M.reference(this.snapshot, node) : null, node, status: observed.status || (node.access?.state === 'protected' ? 'protected' : 'cached'), capturedAtUtc: observed.capturedAtUtc || this.snapshot.captureCompletedAtUtc, metadataRevision: this.snapshot.metadataRevision, contentRevision: observed.contentRevision || null, contentFreshness: 'historicalObservationOnly', facets: node.metadata || {}, details: observed.details, previews: observed.previews || node.previews || node.metadata?.previews || [], warnings: [...(observed.warnings || []), 'Offline historical observations; not live PLC values or current content verification.'] }; }
        case 'relationships': return this.index.relationships(ref?.nodeId || args.nodeId, args);
        case 'verify': return { valid: false, status: 'unavailable', reason: 'Offline snapshots cannot verify the current TIA object or session.', metadataRevision: this.snapshot.metadataRevision };
        case 'release': this.released = true; this.cursors.clear(); return { released: true, snapshotId: this.snapshot.snapshotId };
        case 'export': return { snapshot: this.snapshot, manifest: M.exportManifest(this.snapshot, args.filters || {}, ref) };
      }
    }
  }
  function hostRequest(action, args) {
    const request = { ...args, action };
    if (request.pageSize != null) { request.maxItems = request.pageSize; delete request.pageSize; }
    if (action === 'search' && request.kinds) { request.kind = request.kinds[0] || null; delete request.kinds; }
    if (action === 'search' && request.scopePath?.startsWith('plc:')) { request.plcPath = request.scopePath; delete request.scopePath; }
    if (action === 'relationships') { request.direction = ({ outgoing: 'out', incoming: 'in' })[request.direction] || request.direction; }
    if (action === 'capture' && request.limits) { Object.assign(request, request.limits); delete request.limits; }
    return { request };
  }
  // An initialized official SDK may also be injected by an embedding application.
  // No raw engineering endpoint or arbitrary tool-name input is accepted.
  function mcpAppTransport(app) {
    const capabilities = app?.getHostCapabilities?.();
    if (!capabilities?.serverTools || typeof app.callServerTool !== 'function') M.fail('host_unsupported', 'MCP Apps serverTools support was not negotiated. Use the portable artifact.');
    return {
      offline: false,
      invoke(action, args) { if (!ACTIONS.has(action)) M.fail('read_only', 'Action is not allowed.'); return app.callServerTool({ name: 'project_model', arguments: hostRequest(action, args) }); },
      readArtifact(manifest,onProgress){return readArtifactChunks(manifest,args=>app.callServerTool({name:'artifact_read',arguments:args}),onProgress);},
      sendSelection: typeof app.sendMessage === 'function' ? async ref => { const result = await app.sendMessage({ role: 'user', content: [{ type: 'text', text: 'Inspect this exact project object. Re-resolve the reference before a fresh read or prepared change.\n' + JSON.stringify(ref) }] }); if (result?.isError) M.fail('host_rejected', 'The host did not accept the selected reference.'); } : undefined
    };
  }
  async function connectMcpHost({ onResult = () => {}, onContext = () => {}, onDisconnect = () => {}, windowObject = globalThis, timeoutMs = 10000 } = {}) {
    const win = windowObject, parent = win.parent;
    if (!parent || parent === win) M.fail('host_unsupported', 'MCP Apps requires an embedding host.');
    let sequence = 0, origin = null, initialized = false, closed = false, resizeObserver, resizeTimer, lastSize; const pending = new Map(), notifications = [];
    const prefix = 'tia-explorer-' + Math.random().toString(36).slice(2) + '-';
    const send = message => parent.postMessage(message, origin && origin !== 'null' ? origin : '*');
    function close(reason) { if (closed) return; closed = true; resizeObserver?.disconnect(); clearTimeout(resizeTimer); win.removeEventListener('message', listener); for (const entry of pending.values()) { clearTimeout(entry.timer); entry.reject(Object.assign(new Error(reason), { code: 'session_lost' })); } pending.clear(); onDisconnect(reason); }
    function request(method, params, timeout = 120000) {
      if (closed) return Promise.reject(Object.assign(new Error('MCP host session ended.'), { code: 'session_lost' }));
      if (!['ui/initialize', 'tools/call', 'ui/message'].includes(method)) M.fail('read_only', 'Protocol operation is not allowed.');
      const id = prefix + ++sequence;
      return new Promise((resolve, reject) => { const timer = setTimeout(() => { pending.delete(id); const error = Object.assign(new Error('Host request timed out; native outcome is unknown. Do not automatically replay.'), { code: 'transport_timeout' }); reject(error); if (method !== 'ui/initialize') close(error.message); }, timeout); pending.set(id, { resolve, reject, timer, method }); send({ jsonrpc: '2.0', id, method, params }); });
    }
    function listener(event) {
      // The immediate parent may be the host's sandbox proxy. Never accept sibling windows.
      if (event.source !== parent || origin !== null && event.origin !== origin) return;
      const message = event.data; if (!message || typeof message !== 'object' || message.jsonrpc !== '2.0') return;
      if (message.id != null && !message.method) {
        const entry = pending.get(message.id); if (!entry) return;
        if (origin === null && entry.method !== 'ui/initialize') return;
        if (origin === null) origin = event.origin;
        clearTimeout(entry.timer); pending.delete(message.id);
        if (message.error) entry.reject(Object.assign(new Error(message.error.message || 'Host rejected request.'), { code: 'host_error' })); else entry.resolve(message.result);
        return;
      }
      if (!initialized) return;
      if (message.method === 'ui/notifications/tool-result') { if (!transportReady) notifications.push(message.params); else Promise.resolve(onResult(message.params)).catch(onDisconnect); }
      else if (message.method === 'ui/notifications/host-context-changed') onContext(message.params || {});
      else if (message.method === 'ui/notifications/tool-cancelled') onDisconnect(message.params?.reason || 'Host cancelled the current request.');
      else if (message.method === 'ui/resource-teardown') { send({ jsonrpc: '2.0', id: message.id, result: {} }); close('MCP host session ended.'); }
      else if (message.method === 'ping' && message.id != null) send({ jsonrpc: '2.0', id: message.id, result: {} });
      else if (message.id != null) send({ jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'The explorer exposes no engineering or mutation methods.' } });
    }
    let transportReady = false;
    win.addEventListener('message', listener);
    try {
      const result = await request('ui/initialize', { protocolVersion: '2026-01-26', appInfo: { name: 'tia-project-explorer', version: '1.0.0' }, appCapabilities: { availableDisplayModes: ['inline', 'fullscreen'] } }, timeoutMs);
      if (result?.protocolVersion !== '2026-01-26' || !result.hostCapabilities?.serverTools) M.fail('host_unsupported', 'Host did not negotiate supported MCP Apps serverTools.');
      initialized = true; onContext(result.hostContext || {});
      const transport = { offline: false, hostInfo: result.hostInfo, close,
        invoke(action, args) { if (!ACTIONS.has(action)) M.fail('read_only', 'Action is not allowed.'); return request('tools/call', { name: 'project_model', arguments: hostRequest(action, args) }); },
        readArtifact(manifest,onProgress){return readArtifactChunks(manifest,args=>request('tools/call',{name:'artifact_read',arguments:args}),onProgress);},
        async sendSelection(ref) { const response = await request('ui/message', { role: 'user', content: [{ type: 'text', text: 'Inspect this exact project object. Re-resolve before a fresh read or prepared change.\n' + JSON.stringify(ref) }] }); if (response?.isError) M.fail('host_rejected', 'Host rejected selection.'); }
      };
      send({ jsonrpc: '2.0', method: 'ui/notifications/initialized', params: {} });
      if (win.ResizeObserver && win.document?.body) {
        resizeObserver = new win.ResizeObserver(() => {
          clearTimeout(resizeTimer);
          resizeTimer = setTimeout(() => {
            if (closed) return;
            const width = win.document.documentElement.clientWidth, height = Math.ceil(win.document.body.getBoundingClientRect().height);
            const size = width + ':' + height;
            if (width > 0 && height > 0 && size !== lastSize) {
              lastSize = size;
              send({ jsonrpc: '2.0', method: 'ui/notifications/size-changed', params: { width, height } });
            }
          }, 0);
        });
        resizeObserver.observe(win.document.body);
      }
      // Return before processing tool results so the caller can bind its transport.
      setTimeout(() => { transportReady = true; for (const notification of notifications.splice(0)) Promise.resolve(onResult(notification)).catch(onDisconnect); }, 0);
      return transport;
    } catch (e) { close(e.message); throw e; }
  }
  return { ACTIONS, ReadOnlyClient, OfflineTransport, mcpAppTransport, connectMcpHost, hostRequest, readArtifactChunks, unwrap };
});
