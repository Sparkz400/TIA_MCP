const { fixture, synthetic } = require('./fixture.cjs');
// Independent adjacency specification, intentionally includes cycles, parallel evidence and disconnected nodes.
function oracleFixture() {
  const doc=fixture();
  doc.nodes='root,A,B,C,D,X,Y,Z'.split(',').map(id=>({nodeId:id,parentId:id==='root'?null:'root',name:id,kind:id==='root'?'project':'block',origin:'native',objectPath:'/fixture/'+id}));
  const endpoint=id=>id===null?null:{nodeId:id,objectPath:'/fixture/'+id};
  doc.edges=[['ab','A','B'],['ab-second','A','B'],['bc','B','C'],['ca','C','A'],['self','A','A'],['missing-target','A',null],['missing-source',null,'A'],['disconnected','X','Y'],['read','A','Z','reads'],['incoming','D','A']].map(([edgeId,from,to,kind='calls'])=>({edgeId,source:endpoint(from),target:endpoint(to),kind,evidence:{provider:'independentFixture',origin:'unresolved',resolution:from===null||to===null?'unresolved':'resolved'},unresolved:from===null||to===null?{reason:'Declared missing endpoint'}:null}));
  doc.coverage=[{provider:'independentFixture',scope:'/fixture',status:'complete',visited:8,returned:10,warnings:[],continuationAvailable:false}];
  return doc;
}
function scaleFixture(count, connected=false) {
  const doc=synthetic(count),endpoint=i=>({nodeId:i===0?'root':'n'+i,objectPath:doc.nodes[i].objectPath});
  doc.edges=[];
  function edge(id,from,to){return{edgeId:id,source:endpoint(from),target:to===null?null:endpoint(to),kind:'calls',evidence:{provider:'independentStarFixture',resolution:to===null?'unresolved':'resolved',sourcePath:'fixture'},unresolved:to===null?{reason:'Missing configured endpoint'}:null};}
  for(let i=1;i<=499;i++){doc.edges.push(edge('out-'+String(i).padStart(3,'0'),0,i));doc.edges.push(edge('back-'+String(i).padStart(3,'0'),i,0));}
  doc.edges.push(edge('unresolved-1',0,null),edge('unresolved-2',0,null));
  for(let i=0;i<count-1000;i++)doc.edges.push(connected?edge('wide-'+i,0,500+i):edge('disconnected-'+i,500+i,500+(i+1)%(count-500)));
  return doc;
}
module.exports={oracleFixture,scaleFixture};
