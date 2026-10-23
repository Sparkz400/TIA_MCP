// Protocol fixture only; never an engineering server. Real tool envelope and artifact shape.
export function hostFixture(scripts, snapshot) {
  return `<!doctype html><html><body style="margin:0"><iframe id="app" src="mcp-app.html" style="border:0;width:100vw;height:100vh"></iframe><script>${scripts.replace(/<\/script/gi,'<\\/script')}
const fixture=${JSON.stringify(snapshot).replace(/</g,'\\u003c')};
const transport=new ProjectExplorerClient.OfflineTransport(fixture);
let requests=[],selections=[],artifactBytes,artifactManifest;window.tamperArtifact=false;
window.addEventListener('message',async e=>{
  if(e.source!==document.getElementById('app').contentWindow)return;const m=e.data;
  const respond=result=>e.source.postMessage({jsonrpc:'2.0',id:m.id,result},'*');
  if(m.method==='ui/initialize')respond({protocolVersion:'2026-01-26',hostCapabilities:{serverTools:{}},hostInfo:{name:'Fixture host'},hostContext:{theme:'light'}});
  else if(m.method==='ui/notifications/initialized'){const{nodes,edges,...header}=fixture;e.source.postMessage({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{content:[{type:'text',text:JSON.stringify({...header,roots:nodes.filter(n=>!n.parentId)})}]}},'*');}
  else if(m.method==='tools/call'){
    requests.push(m.params);
    try{
      let result;
      if(m.params.name==='artifact_read'){
        const a=m.params.arguments;if(a.path!==artifactManifest.path||a.count!==65536)throw new Error('Invalid artifact request');
        const part=artifactBytes.slice(a.offset,a.offset+a.count);if(window.tamperArtifact&&part.length)part[0]^=1;
        result={relativePath:'snapshot.json',bytes:artifactBytes.length,sha256:artifactManifest.sha256,offset:a.offset,count:part.length,nextOffset:a.offset+part.length,complete:a.offset+part.length===artifactBytes.length,encoding:'base64',data:btoa(Array.from(part,v=>String.fromCharCode(v)).join(''))};
      }else{
        if(m.params.name!=='project_model'||!m.params.arguments.request)throw new Error('Invalid host envelope');
        const a=m.params.arguments.request;if(a.pageSize||a.kinds&&a.action==='search')throw new Error('Invalid query parameter');
        if(a.action==='relationships'&&!['both','in','out'].includes(a.direction))throw new Error('Native relationship direction must be both/in/out');
        const translated={...a,pageSize:a.maxItems,kinds:a.kind?[a.kind]:a.kinds,direction:({in:'incoming',out:'outgoing'})[a.direction]||a.direction};
        if(a.action==='export'){
          artifactBytes=new TextEncoder().encode(JSON.stringify(fixture));
          const hash=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',artifactBytes)),v=>v.toString(16).padStart(2,'0')).join('');
          artifactManifest={path:'D:\\\\host-artifacts\\\\snapshot.json',bytes:artifactBytes.length,sha256:hash,mediaType:'application/json'};
          result={schemaVersion:'project-model.v1',snapshotId:fixture.snapshotId,artifact:artifactManifest};
        }else result=a.action==='verify'?{valid:true,referenceVerified:true,matchesCurrentMetadata:true,reference:a.reference}:a.action==='capture'?fixture:await transport.invoke(a.action,translated);
      }
      respond({content:[{type:'text',text:JSON.stringify(result)}]});
    }catch(error){e.source.postMessage({jsonrpc:'2.0',id:m.id,error:{code:-32000,message:error.message}},'*');}
  }else if(m.method==='ui/message'){selections.push(m.params);respond({});}
});</script></body></html>`;
}
