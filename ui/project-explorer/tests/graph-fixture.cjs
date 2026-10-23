// Independently authored browser report; not a native capture or a parser oracle.
const { createHash } = require('node:crypto');
function graphFixture() {
  const source=Buffer.from('<Graph><Sequence><Steps><Step Number="1" Name="Idle" /></Steps></Sequence></Graph>\r\n');
  const sequence={ordinal:1,path:'/Graph/Sequence',title:'Fixture cycle',steps:[{number:1,name:'Idle',init:true,actions:[],interlocks:[],supervisions:[],content:{xml:'<Step />'}}],transitions:[],branches:[],connections:[]};
  return {format:'tia-graph-inspection.v1',profile:'graph-v6-structure.v2',sourceSha256:createHash('sha256').update(source).digest('hex'),sourceBytes:source.length,
    nativeQualified:false,fullSchemaValidated:false,hasErrors:false,hasUnsupportedContent:true,
    graphs:[{ordinal:1,path:'/Graph',sequences:[sequence,{...sequence,ordinal:2,path:'/Graph/Sequence[2]',title:'Empty auxiliary',steps:[]}],preOperations:[],postOperations:[],alarmSettings:null}],
    diagnostics:[],unsupported:[{path:'/supplied',xml:'<img src=x onerror=alert(1)>',reason:'Supplied opaque fixture'}],originalSourceBase64:source.toString('base64')};
}
module.exports={graphFixture};
