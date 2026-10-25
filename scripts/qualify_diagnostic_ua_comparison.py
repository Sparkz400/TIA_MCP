"""Independent comparison expectations over retained real Host/UA evidence; no acquisition here."""
import base64
import copy
import hashlib
import json
from decimal import Decimal
from pathlib import Path
from qualify_diagnostic_ua_observation import validator


def qualify(call, check, before_ref, after_ref, output, label):
    def download(receipt):
        raw = bytearray()
        while len(raw) < receipt['artifact']['bytes']:
            part = call('artifact_read', dict(path=receipt['artifact']['path'], offset=len(raw)))
            chunk = base64.b64decode(part['data'], validate=True)
            check(bool(chunk), 'Comparison transfer makes progress')
            raw.extend(chunk)
        check(len(raw) == receipt['artifact']['bytes'] and hashlib.sha256(raw).hexdigest() == receipt['id'], 'Comparison transfer exact hash and length')
        return bytes(raw)

    before = json.loads(download(call('diagnostic_ua_observe', dict(snapshotRef=before_ref))))
    after = json.loads(download(call('diagnostic_ua_observe', dict(snapshotRef=after_ref))))
    request = dict(reason="Compare </pre><script>throw 1</script> ``` # retained observations", beforeAnchorSeconds=0,
                   afterAnchorSeconds=0, windowStartSeconds=0, windowEndSeconds=20,
                   signalMap=[dict(before=m['signalId'], after=m['signalId']) for m in before['binding']['mappings']])
    args = dict(beforeSnapshotRef=before_ref, afterSnapshotRef=after_ref, request=request)
    receipt = call('diagnostic_ua_compare', args)
    validator('diagnostic-ua-comparison', '#/$defs/receipt').validate(receipt)
    raw = download(receipt); doc = json.loads(raw)
    validator('diagnostic-ua-comparison').validate(doc)
    check(doc['before'] == before and doc['after'] == after, 'Both independently composed originals are retained exactly')
    check(doc['verdict'] == 'unresolved' and not doc['bugFixProven'] and not doc['clockSynchronizationVerified'], 'Comparison cannot promote behavioral authority')
    for side, original in [('before', before), ('after', after)]:
        rows = original['captureSource']['rows']
        selected = [r for r in rows if 0 <= r['elapsedMilliseconds'] <= 20000]
        check(doc[side+'Window']['selectedRowSequences'] == [r['sequence'] for r in selected], 'Independent inclusive Host window '+side)
        check(doc[side+'Window']['fullPrefixEvidence'] == original['events'], 'Full-prefix gaps remain visible '+side)
        for comparison in doc['signals']:
            mapping = comparison[side+'Mapping']; values = []; errors = 0; indices = []
            index = next(i for i, m in enumerate(original['binding']['mappings']) if m['signalId'] == mapping['signalId'])
            for row in selected:
                if original['acquisitionMode'] == 'polling':
                    if row['kind'] != 'read' or row['selectionId'] != original['captureSource']['plan']['request']['selectionIds'][index]:
                        continue
                    indices.append(row['sequence'])
                    if row['errorCode'] is not None:
                        errors += 1
                    else:
                        values.append(json.loads(row['readResultJson'])['items'][0])
                elif not row['event']['duplicate']:
                    for n in row['event']['notifications'] or []:
                        if n['value']['node'] == mapping['node']:
                            indices.append(row['sequence']); values.append(n['value'])
            actual = comparison[side]
            good = [v for v in values if v['quality'] == 'Good' and v['outcome'] == 'value' and v['encoding'] != 'null']
            check(actual['selectedRowSequences'] == indices and actual['observations'] == len(values) and actual['hostReadErrors'] == errors, 'Independent exact mapped rows '+side)
            check(actual['goodValues'] == len(good) and actual['trueValues'] == sum(v['encoding'] == 'boolean' and v['value'] is True for v in good), 'Only original Good Boolean observations counted '+side)
            for group in actual['numericRanges']:
                numbers = [v['value'] for v in good if all(v[k] == group['type'][k] for k in ('builtInType','dataType','valueRank','arrayDimensions','encoding'))]
                check(group['count'] == len(numbers) and group['minimum'] == min(numbers,key=Decimal) and group['maximum'] == max(numbers,key=Decimal), 'Independent Decimal range preserves original strings '+side)
            check(actual['nonScalarValues'] == sum(v['encoding'] == 'array' for v in values), 'Arrays retained separately from scalar statistics '+side)
        events = [e for r in selected if not r.get('event',{}).get('duplicate',False) for e in r.get('event',{}).get('events',[])]
        duplicates = [e for r in selected if r.get('event',{}).get('duplicate',False) for e in r['event'].get('events',[])]
        counts = {}
        for e in events:
            key = json.dumps([e['fields'][i] for i in (1,2,3,7)],sort_keys=True)
            counts[key] = counts.get(key,0)+1
        check(doc[side+'Events']['notifications'] == len(events) and doc[side+'Events']['duplicateNotifications'] == len(duplicates), 'Independent event and duplicate envelope counts '+side)
        check({json.dumps(g['fields'],sort_keys=True):g['count'] for g in doc[side+'Events']['groups']} == counts, 'Exact original event field groups '+side)
        check(doc[side+'Events']['repeatedEventIds'] == len(events)-len({e['fields'][0]['text'] for e in events}), 'Repeated IDs do not erase distinct recorded notifications '+side)
    for change,code in [(lambda r:r.update(extra=True),'MeasurementFormatUnsupported'),(lambda r:r.update(windowEndSeconds=0),'InvalidRequest'),(lambda r:r['signalMap'][0].update(after='missing'),'MeasurementLineageMismatch')]:
        bad=copy.deepcopy(args);change(bad['request']);call('diagnostic_ua_compare',bad,code)
    output=Path(output);output.mkdir(parents=True,exist_ok=True)
    for fmt,ext in [('json','json'),('html','html'),('markdown','md')]:
        export=call('diagnostic_ua_report_export',dict(reportId=receipt['id'],format=fmt))
        validator('diagnostic-ua-comparison-export').validate(export)
        data=download(export);(output/(label+'.'+ext)).write_bytes(data)
        check(fmt!='json' or data==raw,'JSON export is byte-identical to comparison')
        check(fmt!='html' or b'<script>' not in data,'HTML comparison escapes evidence')
    for mutate in [lambda d:d['signals'][0]['before'].update(goodValues=99999),lambda d:d.update(bugFixProven=True),lambda d:d['beforeWindow']['fullPrefixEvidence'].update(gapRows=999)]:
        bad=copy.deepcopy(doc);mutate(bad);content=json.dumps(bad).encode();identity=hashlib.sha256(content).hexdigest()
        target=Path(receipt['artifact']['path']).parent/(identity+'.json');target.write_bytes(content)
        call('diagnostic_ua_report_export',dict(reportId=identity,format='json'),'UaDiagnosticReportMismatch')
    return dict(id=receipt['id'],arguments=args)
