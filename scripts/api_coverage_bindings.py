"""Offline AC-01 source bindings. Token scanner, not a C# compiler or qualification engine.

Comments and all string bodies are opaque. Exact declarations/dispatch anchors are
retained; overload/member dispatch and dependency closure stay conservative.
"""
from __future__ import annotations
import argparse, hashlib, json, re
from pathlib import Path
import api_coverage as inventory

ROOT = Path(__file__).resolve().parents[1]
REGISTRY = 'contracts/api-coverage/implementation-bindings.v1.json'
CATALOG = 'contracts/feature-catalog.v1.json'

def digest(value): return hashlib.sha256(value).hexdigest()
def tokens(text):
    """Return (kind,value,start,end), retaining offsets and ignoring comment decoys."""
    result=[]; i=0; size=len(text)
    def string_end(start, quote, verbatim=False, interpolated=False):
        p=start; depth=0
        while p<size:
            if depth:
                if text.startswith('//',p):
                    q=text.find('\n',p); p=size if q<0 else q; continue
                if text.startswith('/*',p):
                    q=text.find('*/',p+2)
                    if q<0: raise ValueError('Unclosed interpolation comment')
                    p=q+2; continue
                if text[p] in '\"\'': p=string_end(p+1,text[p]); continue
                if text[p]=='{': depth+=1
                if text[p]=='}': depth-=1
                p+=1; continue
            if interpolated and text[p]=='{':
                if text.startswith('{{',p): p+=2; continue
                depth=1; p+=1; continue
            if text[p]==quote:
                if verbatim and p+1<size and text[p+1]==quote: p+=2; continue
                return p+1
            if not verbatim and text[p]=='\\': p+=2
            else: p+=1
        raise ValueError('Unclosed string')
    while i<size:
        if text[i].isspace(): i+=1; continue
        if text.startswith('//',i):
            q=text.find('\n',i); i=size if q<0 else q; continue
        if text.startswith('/*',i):
            q=text.find('*/',i+2)
            if q<0: raise ValueError('Unclosed comment')
            i=q+2; continue
        start=i; prefix=re.match(r'(?:\$+@?|@\$?)?("{3,}|"|\')',text[i:])
        if prefix:
            spelling=prefix[0]; quote=prefix[1]
            if len(quote)>=3:
                q=text.find(quote,i+len(spelling))
                if q<0: raise ValueError('Unclosed raw string')
                i=q+len(quote); value=None
            else:
                i=string_end(i+len(spelling),quote,'@' in spelling,'$' in spelling)
                raw=text[start+len(spelling):i-1]
                value=None if '$' in spelling or quote=="'" else raw.replace('""','"') if '@' in spelling else raw if '\\' not in raw else None
            result.append(('str',value,start,i)); continue
        m=re.match(r'[A-Za-z_][A-Za-z_0-9]*',text[i:])
        if m: i+=len(m[0]); result.append(('id',m[0],start,i)); continue
        op=next((x for x in ('=>','==','!=','?.','??','::') if text.startswith(x,i)),text[i])
        i+=len(op); result.append(('punct',op,start,i))
    return result

def pairs(ts):
    stack=[]; out={}; opening={'(':')','[':']','{':'}'}
    for i,t in enumerate(ts):
        if t[0]=='str': continue
        if t[1] in opening: stack.append(i)
        elif t[1] in opening.values():
            if not stack or opening[ts[stack[-1]][1]]!=t[1]: raise ValueError('Unbalanced code delimiters')
            j=stack.pop(); out[j]=i
    if stack: raise ValueError('Unclosed code delimiter')
    return out

def methods(text,path):
    ts=tokens(text); ps=pairs(ts); rows=[]; classes=[]
    for k,t in enumerate(ts[:-1]):
        if t[0]=='id' and t[1] in ('class','struct','record') and ts[k+1][0]=='id':
            j=k+2
            while j<len(ts) and ts[j][1] not in ('{',';'): j=ps[j]+1 if j in ps else j+1
            if j in ps: classes.append((j,ps[j],ts[k+1][1]))
    for i,t in enumerate(ts):
        if t[1]!='(' or i==0 or ts[i-1][0]!='id': continue
        close=ps[i]; after=close+1
        if after>=len(ts) or ts[after][1] not in ('{','=>'): continue
        start=i-1
        while start>0 and ts[start-1][1] not in (';','{','}',']'): start-=1
        prefix=[x[1] for x in ts[start:i-1]]
        if not any(x in prefix for x in ('public','private','internal','protected')): continue
        if any(x in prefix for x in ('class','record','new','=')): continue
        if ts[after][1]=='{': end=ps[after]
        else:
            end=after+1
            while end<len(ts) and ts[end][1]!=';':
                end=ps[end]+1 if end in ps else end+1
            if end>=len(ts): raise ValueError('Unclosed expression method')
        a=ts[start][2]; b=ts[end][3]
        rows.append({'name':ts[i-1][1],'path':path,'line':text.count('\n',0,a)+1,'endLine':text.count('\n',0,b)+1,
                     'sha256':digest(text[a:b].encode()),'_start':a,'_end':b,'_tokens':ts[after+1:end],
                     '_owner':next((name for lo,hi,name in reversed(classes) if lo<i<hi),None)})
    return rows,ts

def anchor(row): return {k:row[k] for k in ('path','name','line','endLine','sha256')}
def calls(ts):
    return sorted({t[1] for i,t in enumerate(ts[:-1]) if t[0]=='id' and ts[i+1][1]=='(' and t[1] not in ('if','for','foreach','while','switch','catch','typeof','nameof','sizeof')})
def literal_calls(ts):
    return sorted({ts[i+2][1] for i,t in enumerate(ts[:-2]) if t[1] in ('Call','CallAsync') and ts[i+1][1]=='(' and ts[i+2][0]=='str' and ts[i+2][1] is not None})
def unqualified_calls(ts):
    return {t[1] for i,t in enumerate(ts[:-1]) if t[0]=='id' and ts[i+1][1]=='(' and (i==0 or ts[i-1][1] not in ('.','?.','new','::'))}
def case_rows(method):
    ts=method['_tokens']; found=[]
    starts=[i for i,t in enumerate(ts[:-2]) if t[1]=='case' and ts[i+1][0]=='str' and ts[i+2][1]==':']
    for n,i in enumerate(starts):
        last=n
        while last+1<len(starts) and starts[last+1]==starts[last]+3: last+=1
        end=starts[last+1] if last+1<len(starts) else len(ts)
        found.append((ts[i+1][1],ts[i:end]))
    return found

PACKAGE_VERSION=re.compile(rb'(<PackageReference\b[^>]*?\bVersion=")[^"]*(")')

def source_bytes(path,data):
    # Dependency bumps change only PackageReference versions; they must not stale implementation bindings.
    return PACKAGE_VERSION.sub(rb'\1\2',data) if path.endswith(('.csproj','.props')) else data

def collect(repo):
    # Reuse the inventory's bounded file reader, path guard, hashing and catalog semantics.
    source_rows,texts=inventory.source_inventory(repo)
    source_rows=[r for r in source_rows if r['path']!=REGISTRY]
    for r in source_rows:
        if r['path'].endswith(('.csproj','.props')):
            data=source_bytes(r['path'],inventory.read_bytes(inventory.within(repo,r['path']))); r['sha256']=digest(data); r['bytes']=len(data)
    catalog=inventory.json_read(repo/CATALOG); features=catalog['features']
    ids=[f['featureId'] for f in features]
    if len(ids)!=len(set(ids)): raise ValueError('Duplicate feature')
    for relative in ('scripts/api_coverage_bindings.py','scripts/test_api_coverage_bindings.py'):
        if (repo/relative).is_file():
            data=inventory.read_bytes(repo/relative); source_rows.append({'path':relative,'sha256':digest(data),'bytes':len(data)})
    source_rows.sort(key=lambda r:r['path'])
    # Git's repository policy writes these source inputs with LF on checkout.
    unstable=[r['path'] for r in source_rows if b'\r\n' in inventory.read_bytes(repo/r['path'])]
    if unstable: raise ValueError('Source bindings require checkout-stable LF bytes; convert these source files before collecting: '+','.join(unstable))
    all_methods=[]; tool_rows=[]; token_cache={}
    for path,text in texts.items():
        ms,ts=methods(text,path); all_methods.extend(ms); token_cache[path]=ts
        if not path.startswith('src/TiaMcp.Host/'): continue
        for i,t in enumerate(ts):
            if t[0]!='id' or t[1]!='McpServerTool' or i+5>=len(ts): continue
            if [x[1] for x in ts[i+1:i+4]]!=['(','Name','=']: continue
            name=ts[i+4][1]
            if ts[i+4][0]!='str' or not name: raise ValueError('Nonliteral tool registration')
            following=[m for m in ms if m['_start']>t[2]]
            if not following: raise ValueError('Tool method declaration missing: '+name)
            method=min(following,key=lambda m:m['_start'])
            tool_rows.append({'tool':name,'host':anchor(method),'bridgeOperations':literal_calls(method['_tokens']),
                              'localCallNames':calls(method['_tokens']),'_method':method})
    names=[t['tool'] for t in tool_rows]
    if len(names)!=len(set(names)): raise ValueError('Duplicate MCP tool registration')
    expected={t for f in features for t in f['mcpTools']}
    if set(names)!=expected: raise ValueError('Catalog/tool set mismatch: '+str(sorted(set(names)^expected)))
    byname={}
    for m in all_methods: byname.setdefault(m['name'],[]).append(m)
    worker_paths={'src/TiaMcp.Openness.V21/OpennessSession.cs','src/TiaMcp.Simulator.V8/SimulatorSession.cs'}
    dispatch={}
    for m in all_methods:
        if m['path'] in worker_paths and m['name']=='Execute':
            for operation,body in case_rows(m):
                handlers=[h for name in calls(body) for h in byname.get(name,[]) if h['path'].startswith(m['path'].rsplit('/',1)[0]+'/') and ('OpennessSession' in h['path'] if 'Openness' in m['path'] else 'SimulatorSession' in h['path'])]
                family='simulator' if 'Simulator' in m['path'] else 'engineering'
                dispatch[(family,operation)]={'operation':operation,'dispatcher':anchor(m),'handlers':[anchor(h) for h in handlers],
                    'reviewState':'static_dispatch_bound' if handlers else 'unresolved','gaps':['Overload and runtime control flow are not compiler-resolved.']}
    for t in tool_rows:
        # Follow uniquely named Host helpers only. Ambiguous receivers remain explicit gaps.
        seen=set(); pending=[t['_method']]; host_chain=[]; ops=set(t['bridgeOperations'])
        while pending:
            helper=pending.pop(); key=(helper['path'],helper['_start'])
            if key in seen: continue
            seen.add(key); host_chain.append(anchor(helper)); ops.update(literal_calls(helper['_tokens']))
            for name in sorted(unqualified_calls(helper['_tokens'])):
                candidates=[h for h in byname.get(name,[]) if h['path'].startswith('src/TiaMcp.Host/') and h['_owner']==helper['_owner']]
                if len(candidates)==1: pending.append(candidates[0])
        t['bridgeOperations']=sorted(ops); t['hostDelegationAnchors']=host_chain
        family='simulator' if t['host']['path'].endswith('/SimulatorTools.cs') else 'engineering'
        t['workerRoutes']=[dispatch.get((family,op),{'operation':op,'reviewState':'unresolved','gaps':['No matching native dispatcher.'],'handlers':[]}) for op in t['bridgeOperations']]
        t['localHandlers']=[anchor(h) for name in t['localCallNames'] for h in byname.get(name,[]) if h['path'].startswith(('src/TiaMcp.Host/','src/TiaMcp.Core/'))]
        t['reviewState']='static_entrypoint_bound'
        t['gaps']=['Local method-name matches may include overloads or unrelated receiver types; transitive dependencies are conservative.']
        if not t['bridgeOperations']: t['gaps'].append('No literal bridge operation in entrypoint; local/delegated route needs semantic receiver review.')
    mutations=[]
    for f in features:
        kind=f.get('changeKind')
        if not kind: continue
        library=kind.startswith('library_')
        method_names=['PrepareLibraryAction','ApplyLibraryAction'] if library else ['PrepareEngineeringMutation','ApplyEngineeringMutation']
        branches=[]
        for name in method_names:
            for m in byname.get(name,[]):
                ts=m['_tokens']; cases=[body for value,body in case_rows(m) if value==kind]
                direct=any(t[0]=='str' and t[1]==kind and i>=4 and [x[1] for x in ts[i-4:i]]==['change','.','Kind','=='] for i,t in enumerate(ts)) and not cases
                if cases or direct:
                    body=ts if direct else cases[0]
                    branches.append({'phase':name,'routeKind':'direct_early_route' if direct else 'switch_case','anchor':anchor(m),
                       'handlers':[anchor(h) for call in calls(body) for h in byname.get(call,[]) if h['path'].startswith('src/TiaMcp.Openness.V21/OpennessSession')]})
        mutations.append({'changeKind':kind,'featureId':f['featureId'],'dispatcherFamily':'library_delegation' if library else 'engineering',
                          'branches':branches,'reviewState':'static_branches_bound' if len(branches)==2 else 'partial',
                          'gaps':['Shared lifecycle, dynamic dispatch and native effects require separate semantic review.']})
    if len({m['changeKind'] for m in mutations})!=len(mutations): raise ValueError('Duplicate change kind')
    evidence={e['id']:e for e in catalog['evidence']}
    if len(evidence)!=len(catalog['evidence']): raise ValueError('Duplicate evidence ID')
    evidence_inputs=[]
    for relative in sorted({p for e in evidence.values() for p in e.get('artifacts',[])}):
        path=inventory.within(repo,relative)
        if path.is_file():
            try: data=inventory.read_bytes(path); evidence_inputs.append({'path':relative,'state':'observed','sha256':digest(data)})
            except ValueError: evidence_inputs.append({'path':relative,'state':'unavailable_bounded_read','sha256':None})
        else: evidence_inputs.append({'path':relative,'state':'directory' if path.is_dir() else 'missing','sha256':None})
    features_out=[]; tools={t['tool']:t for t in tool_rows}; mutation_map={m['changeKind']:m for m in mutations}
    # Full source inventory is the conservative fallback for unresolved dynamic/member dispatch.
    for f in sorted(features,key=lambda f:f['featureId']):
        refs=f['qualification']['evidenceIds']
        if not set(refs)<=set(evidence): raise ValueError('Unknown evidence/profile reference')
        direct={tools[t]['host']['path'] for t in f['mcpTools']}
        anchors=[a for t in f['mcpTools'] for a in tools[t]['hostDelegationAnchors']]
        for t in f['mcpTools']:
            for route in tools[t]['workerRoutes']:
                if 'dispatcher' in route: direct.add(route['dispatcher']['path'])
                direct.update(h['path'] for h in route['handlers'])
                anchors.extend(route['handlers'])
            direct.update(h['path'] for h in tools[t]['localHandlers'])
            anchors.extend(tools[t]['localHandlers'])
        if f.get('changeKind'):
            for b in mutation_map[f['changeKind']]['branches']:
                direct.add(b['anchor']['path']); direct.update(h['path'] for h in b['handlers'])
                anchors.extend(b['handlers'])
        direct.update(a['path'] for a in anchors)
        schemas=[]
        # Exact schemaVersion literal + existing matching contract: binding only, not conformance proof.
        for a in {(a['path'],a['sha256']):a for a in anchors}.values():
            path=a['path']
            selected=[m for m in all_methods if m['path']==path and m['sha256']==a['sha256']]
            for tok in selected[0]['_tokens'] if selected else []:
                value=tok[1] if tok[0]=='str' else None
                if not value or '/' in value or '\\' in value: continue
                relative='contracts/'+value+'.schema.json'
                if (repo/relative).is_file() and re.fullmatch(r'[a-z0-9-]+\.v[0-9]+',value):
                    schemas.append({'path':relative,'source':path,'sourceAnchor':a,'literal':value,'reviewState':'literal_contract_reference','conformanceProven':False})
        schemas=list({(s['path'],s['source']):s for s in schemas}.values())
        features_out.append({'featureId':f['featureId'],'tools':f['mcpTools'],'changeKind':f.get('changeKind'),
            'sourceDependencies':sorted(direct),'schemaBindings':schemas,'schemaReview':'partial' if schemas else 'unresolved',
            'evidenceRefs':[{'id':e,'profileSha256':digest(inventory.canonical(evidence[e].get('profile'))),'artifacts':evidence[e].get('artifacts',[])} for e in refs],
            'reviewState':'partial','gaps':['Transitive receiver/overload resolution is not proven; unknown source/schema changes invalidate all feature bindings.']+([] if schemas else ['No exact source-to-schema binding established.']),
            'releaseAccepted':False,'qualificationPromoted':False})
    for t in tool_rows: del t['_method']
    return {'schemaVersion':'api-coverage-bindings.v1','scannerVersion':1,'nativeExecuted':False,'releaseAccepted':False,
        'scope':'Static anchored entrypoints and mutation branches; conservative unresolved dependencies, not compiled C# call graph.',
        'counts':{'tools':len(tool_rows),'mutations':len(mutations),'features':len(features_out)},'sourceInputs':source_rows,
        'tools':sorted(tool_rows,key=lambda t:t['tool']),'mutations':sorted(mutations,key=lambda m:m['changeKind']),
        'features':features_out,'evidenceInputs':evidence_inputs,'evidenceProfiles':[{'id':k,'profileSha256':digest(inventory.canonical(v.get('profile')))} for k,v in sorted(evidence.items())]}

def validate(binding, repo=None, *, allow_missing_evidence=False):
    if binding.get('schemaVersion')!='api-coverage-bindings.v1' or binding.get('nativeExecuted') is not False or binding.get('releaseAccepted') is not False: raise ValueError('Invalid binding envelope')
    for key,field,count in [('tools','tool','tools'),('mutations','changeKind','mutations'),('features','featureId','features')]:
        values=[r[field] for r in binding[key]]
        if len(values)!=len(set(values)) or len(values)!=binding['counts'][count]: raise ValueError('Duplicate/missing '+key)
    known_tools={t['tool'] for t in binding['tools']}; known_profiles={e['id']:e['profileSha256'] for e in binding['evidenceProfiles']}
    sources={s['path']:s['sha256'] for s in binding['sourceInputs']}
    if len(sources)!=len(binding['sourceInputs']) or len(known_profiles)!=len(binding['evidenceProfiles']): raise ValueError('Duplicate dependency/profile')
    if {t for f in binding['features'] for t in f['tools']}!=known_tools: raise ValueError('Unreferenced/missing tool')
    expected_mutations={(f['changeKind'],f['featureId']) for f in binding['features'] if f['changeKind']}
    if expected_mutations!={(m['changeKind'],m['featureId']) for m in binding['mutations']}: raise ValueError('Mutation/feature mismatch')
    for f in binding['features']:
        if f['releaseAccepted'] is not False or f['qualificationPromoted'] is not False: raise ValueError('Qualification promotion forbidden')
        if f['reviewState']!='partial' or f['schemaReview'] not in ('partial','unresolved') or not f['gaps']: raise ValueError('Unreviewed completeness assertion')
        if not set(f['tools'])<=known_tools: raise ValueError('Unknown tool')
        if not set(f['sourceDependencies'])<=set(sources): raise ValueError('Unknown source dependency')
        for e in f['evidenceRefs']:
            if known_profiles.get(e['id'])!=e['profileSha256']: raise ValueError('Unknown/stale evidence profile')
        for s in f['schemaBindings']:
            if s['path'] not in sources or not s['path'].endswith('.schema.json') or s['source'] not in sources: raise ValueError('Unknown schema/source reference')
    unavailable=[]
    if repo is not None:
        stale=[path for path,h in sources.items() if not inventory.within(repo,path).is_file() or digest(source_bytes(path,inventory.read_bytes(inventory.within(repo,path))))!=h]
        if stale: raise ValueError('Stale source bindings: '+','.join(stale))
        actual=collect(repo)
        if allow_missing_evidence:
            # Clean source checkouts omit ignored native evidence. Only absence
            # may be tolerated; a present changed artifact still invalidates the
            # binding. The retained artifact inventory itself stays unchanged.
            retained={e['path']:e for e in binding.get('evidenceInputs',[])}
            if len(retained)!=len(binding.get('evidenceInputs',[])):
                raise ValueError('Duplicate evidence input')
            for index,current in enumerate(actual['evidenceInputs']):
                previous=retained.get(current['path'])
                if current['state']=='missing' and previous is not None:
                    unavailable.append(current['path'])
                    actual['evidenceInputs'][index]=previous
        if inventory.canonical(binding)!=inventory.canonical(actual): raise ValueError('Binding content differs from current anchored inventory')
    return {'valid':True,'counts':binding['counts'],'releaseAccepted':False,'unavailableEvidence':unavailable}

def impact(binding,changed_paths=(),evidence_ids=()):
    validate(binding); paths=set(changed_paths); ids=set(evidence_ids); affected=[]; direct=[]
    all_ids={f['featureId'] for f in binding['features']}
    known_profiles={e['id'] for e in binding['evidenceProfiles']}
    for f in binding['features']:
        deps=set(f['sourceDependencies'])|{s['path'] for s in f['schemaBindings']}|{p for e in f['evidenceRefs'] for p in e['artifacts']}
        if paths&deps or ids&{e['id'] for e in f['evidenceRefs']}: direct.append(f['featureId'])
    # Partial call graph cannot justify excluding a feature for any source/schema change.
    evidence_paths={e['path'] for e in binding.get('evidenceInputs',[])}
    fallback=bool(paths-evidence_paths or ids-known_profiles)
    affected=sorted(all_ids if fallback else set(direct))
    return {'affectedFeatureIds':affected,'directlyBoundFeatureIds':sorted(direct),'conservativeFallback':fallback,
            'reason':'Partial transitive bindings; changed paths conservatively invalidate all features.' if fallback else 'Exact evidence profile references.', 'qualificationPromoted':False}

def diff(before,after):
    validate(before); validate(after)
    a={s['path']:s['sha256'] for s in before['sourceInputs']}; b={s['path']:s['sha256'] for s in after['sourceInputs']}
    changed=sorted(p for p in set(a)|set(b) if a.get(p)!=b.get(p))
    ea={e['path']:e for e in before.get('evidenceInputs',[])}; eb={e['path']:e for e in after.get('evidenceInputs',[])}
    evidence_changed=sorted(p for p in set(ea)|set(eb) if ea.get(p)!=eb.get(p))
    pa={e['id']:e['profileSha256'] for e in before['evidenceProfiles']}; pb={e['id']:e['profileSha256'] for e in after['evidenceProfiles']}
    profile_changed=sorted(p for p in set(pa)|set(pb) if pa.get(p)!=pb.get(p))
    old={f['featureId'] for f in before['features']}; new={f['featureId'] for f in after['features']}
    result=impact(before,changed+evidence_changed,profile_changed)
    result.update({'changedPaths':changed,'changedEvidencePaths':evidence_changed,'changedProfileIds':profile_changed,'addedFeatures':sorted(new-old),'removedFeatures':sorted(old-new)})
    if changed or old!=new: result['affectedFeatureIds']=sorted(old|new)
    return result

def main():
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--repo',type=Path,default=ROOT)
    sub=parser.add_subparsers(dest='command',required=True)
    p=sub.add_parser('collect'); p.add_argument('--out',type=Path,required=True)
    p=sub.add_parser('validate'); p.add_argument('binding',type=Path); p.add_argument('--allow-missing-evidence',action='store_true')
    p=sub.add_parser('impact'); p.add_argument('binding',type=Path); p.add_argument('--path',action='append',default=[]); p.add_argument('--evidence',action='append',default=[])
    p=sub.add_parser('diff'); p.add_argument('before',type=Path); p.add_argument('after',type=Path)
    args=parser.parse_args()
    if args.command=='collect':
        value=collect(args.repo); validate(value); inventory.write_new(args.out,value); print(json.dumps(value['counts']))
    elif args.command=='validate': print(json.dumps(validate(inventory.json_read(args.binding),args.repo,allow_missing_evidence=args.allow_missing_evidence)))
    elif args.command=='impact': print(json.dumps(impact(inventory.json_read(args.binding),args.path,args.evidence)))
    else: print(json.dumps(diff(inventory.json_read(args.before),inventory.json_read(args.after))))
if __name__=='__main__': main()
