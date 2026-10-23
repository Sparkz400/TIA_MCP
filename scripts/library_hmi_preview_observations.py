"""Retained HMI preview consistency checks; no native/file access."""
from urllib.parse import quote
from library_operation_observations import _link, _version
from library_hmi_query_observations import check_hmi_query, target as check_target


def require(condition, field):
    if not condition:raise ValueError('Inconsistent HMI preview: '+field)


def text(value):
    require(bool(value.strip()) and len(value.encode('utf-16-le'))//2<=256
        and not any(ord(c)<32 or 127<=ord(c)<=159 for c in value), 'HMI text')


def name(value):
    text(value)
    require(value not in ('.','..') and not any(c in '/\\' for c in value), 'native name')


def no_truncation(value):
    if isinstance(value,dict):
        for key,item in value.items():
            require(not(key.lower().endswith('truncated') and item is True), 'truncated before observation')
            no_truncation(item)
    elif isinstance(value,list):
        for item in value:no_truncation(item)


def selected_version(plan):
    selected=plan['selection']
    require(len(selected)==1 and 'masterCopyPath' not in selected[0], 'one selected version')
    rows=[row for closure in plan['dependencies'] for row in closure.get('versions',[])
          if row['versionGuid'].lower()==selected[0]['versionGuid'].lower()]
    require(bool(rows),'selected version metadata')
    first=rows[0]
    require(all(row==first for row in rows),'conflicting selected version metadata')
    require(first['typeGuid'].lower()==selected[0]['typeGuid'].lower() and first['state']=='Committed','selected type/version')
    _version(first['version'])
    require(first['typeGuid'].replace('-','')!='0'*32 and first['versionGuid'].replace('-','')!='0'*32,'nonempty native GUID')
    return first


def dependencies(plan,same):
    selected=[row for row in plan['selection'] if 'masterCopyPath' not in row]
    masters=[row for row in plan['selection'] if 'masterCopyPath' in row]
    roots={}; captured={}
    for closure in plan['dependencies']:
        require('versions' in closure,'unavailable consumption dependencies')
        rows={}
        for row in closure['versions']:
            key=row['versionGuid'].lower()
            require(key not in rows,'duplicate dependency version'); rows[key]=row
            require(row['state']=='Committed' and not row['doNotUse'],'committed usable dependency')
            if key in captured:same(captured[key],row,'repeated dependency version')
            captured[key]=row
        root=closure['rootVersionGuid'].lower(); pending=[root]; seen=set()
        while pending:
            key=pending.pop()
            if key in seen:continue
            require(key in rows,'missing required dependency'); seen.add(key)
            pending.extend(g.lower() for g in rows[key]['requiredVersionGuids'])
        require(seen==set(rows),'extraneous dependency version')
        if 'masterCopy' in closure:
            require(bool(masters),'unselected master dependencies')
        else:
            require(root not in roots,'duplicate selected dependency root'); roots[root]=rows[root]
    require(set(roots)=={row['versionGuid'].lower() for row in selected},'selected dependency roots')
    for row in selected:
        same(roots[row['versionGuid'].lower()]['typeGuid'].lower(),row['typeGuid'].lower(),'selected dependency type')


def check_hmi_preview(plan,result,same):
    require(plan['kind'] in ('library_instantiate','library_integrate'),'operation kind')
    dependencies(plan,same)
    preview=plan['targetPreview']; payload=plan['payload']
    require(isinstance(payload.get('target'),dict) and payload['target'].get('family') in ('classic','unified'),'explicit HMI target')
    selected=payload['target']
    target=preview['target']; action=preview['hmiAction']; effect=preview['effect']; before=preview['before']
    check_target(target,same)
    for field in ('family','hmiPath'):same(target[field],selected[field],'selected HMI '+field)
    require(len(target['hmiPath'].encode('utf-16-le'))//2<=4096,'HMI path bound')
    require(isinstance(plan.get('projectPath'),str) and bool(plan['projectPath'].strip()),'project path')
    same(action,payload.get('hmiAction','instantiate'),'HMI action')
    no_truncation(before)
    if action!='faceplate_bind':
        check_hmi_query(before,'hmi-inventory',same)
        same(before['target'],target,'before target')
    if target['family']=='classic':
        same(effect['composition'],selected['composition'],'Classic composition')
        name(effect['name']); masters=[r for r in plan['selection'] if 'masterCopyPath' in r]
        if masters:
            require(len(plan['selection'])==1 and effect['nativeVersionGuid'] is None,'master source')
            allowed={'screens':'Screen.Screen','screenTemplates':'Screen.ScreenTemplate','screenPopups':'Screen.ScreenPopup',
                     'tagTables':'Tag.TagTable','tags':'Tag.Tag','scripts':'RuntimeScripting.VBScript'}
            same(effect['sourceNativeType'],'Siemens.Engineering.Hmi.'+allowed[effect['composition']],'master native composition')
        else:
            version=selected_version(plan)
            allowed={'screens':'Siemens.Engineering.Hmi.Screen.ScreenLibraryTypeVersion',
                     'scripts':'Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryTypeVersion'}
            require(effect['composition'] in allowed,'Classic version composition')
            same(effect['sourceNativeType'],allowed[effect['composition']],'Classic native type')
            same(version['engineeringType'],effect['sourceNativeType'],'source version class')
            same(effect['nativeVersionGuid'].lower(),version['versionGuid'].lower(),'Classic selected version')
            same(effect['name'],version['typeName'],'Classic source name')
        if effect['composition'] in ('screens','tagTables'):
            exact='/'+effect['composition']+'/'+quote(effect['name'],safe='')
            require(all(row['path']!=exact for row in before['items']),'existing Classic root object')
        elif effect['composition']=='tags':
            table=selected['tagTable']; text(table)
            tables=[r for r in before['items'] if r['kind']=='tagTable' and r['path']=='/tagTables/'+quote(table,safe='')]
            require(len(tables)==1,'Classic root tag table')
            require(all(r['name']!=effect['name'] for r in tables[0]['tags']),'existing Classic tag')
    elif action=='tag_create':
        for field in ('name','dataType'):same(effect[field],payload[field],'internal tag '+field)
        same(effect['tagTable'],selected['tagTable'],'internal tag table')
        name(effect['name']); text(effect['tagTable'])
        require(all(row['name']!=effect['name'] for row in before['tags']),'existing Unified tag')
    else:
        version=selected_version(plan)
        require(not version['engineeringType'].startswith(('Siemens.Engineering.SW.','Siemens.Engineering.Hmi.')),'Unified source class')
        for field in ('screen','container'):
            same(effect[field],selected[field],'faceplate '+field); name(effect[field])
        same(effect['versionGuid'].lower(),version['versionGuid'].lower(),'faceplate version')
        same(effect['bindings'],payload.get('bindings',[]),'faceplate bindings')
        if action=='faceplate_create':
            name(version['typeName']); require(len(_version(version['version']))>=3,'concrete faceplate version')
            same(effect['containedType'],'V'+version['version']+'\\'+version['typeName'],'exact contained type')
            same(effect['placement'],payload['placement'],'faceplate placement')
            screens=[s for s in before['screens'] if s['name']==selected['screen']]
            require(len(screens)==1,'existing Unified screen')
            screen=screens[0]; rectangle=effect['placement']
            require(screen['width']>0 and screen['height']>0 and rectangle['left']+rectangle['width']<=screen['width']
                    and rectangle['top']+rectangle['height']<=screen['height'],'screen rectangle')
            require(all(f['name']!=selected['container'] for f in screen['faceplates']),'existing faceplate container')
        else:
            _link(before,plan['selection'][0]); same(before['version'],version['version'],'before linked version')
            same(before['name'],selected['container'],'before container')
            interface=[r['property'] for r in before['interface']]
            require(len(interface)==len(set(interface)),'duplicate interface property')
            bindings=effect['bindings']
            for row in bindings:name(row['property'])
            require({r['property'] for r in bindings}==set(interface),'complete interface binding')
            tags=before['observedTags']; require(len(tags)==len(bindings),'observed binding tags')
            for binding,tag in zip(bindings,tags):
                same(binding['tag'],tag['name'],'ordered binding tag'); same(binding['dataType'],tag['dataType'],'tag data type')
    binding_complete=layout_matches=result_scope=None
    if result is not None and target['family']=='unified' and action!='tag_create':
        same(result['version'],version['version'],'returned selected version number')
        if action=='faceplate_create':
            result_scope='selected-version-contained-type-and-placement'
            same(result['containedType'],effect['containedType'],'returned contained type')
        else:
            result_scope='selected-version-and-captured-requested-bindings'
            observed={row['property']:row['value'] for row in result['interface']}
            binding_complete=not result['interfaceTruncated'] and all(row['property'] in observed for row in effect['bindings'])
            layout_matches=all(result[field]==before[field] for field in ('containedType','left','top','width','height'))
    elif result is not None and target['family']=='classic' and not masters:
        result_scope='selected-version-link'
        same(result['version'],version['version'],'returned Classic selected version number')
    elif result is not None:
        result_scope='master-link-metadata-only' if target['family']=='classic' else 'internal-tag-name-and-type'
    return dict(hmiPreviewValidated=True,hmiResultBindingScope=result_scope,
                hmiBindingReadbackComplete=binding_complete,hmiRetainedLayoutMatches=layout_matches,
                targetFingerprintRecomputed=False,
                nativeSelectorVerified=False,nativeInterfaceTypesVerified=False,
                collisionAbsenceVerified=False,fullPreservationVerified=False)
