"""Separate explicit input-ID profile; strict native output oracle stays unchanged.

Load the existing lifecycle oracle into a private module and replace only its
reviewed input delta check. Never mask or normalize native output IDs.
"""
import copy
import importlib.util
from pathlib import Path

_spec = importlib.util.spec_from_file_location('_watch_reindexed_lifecycle', Path(__file__).with_name('verify_watch_expansion.py'))
_lifecycle = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_lifecycle)
_strict_delta = _lifecycle.exact_delta
PROFILE = 'v21-watch-reference-free-preorder-1'

def __getattr__(name):
    return getattr(_lifecycle, name)

def reference_free(tree):
    need = _lifecycle.need
    table = _lifecycle.base.table(tree)
    prefix = _lifecycle.PREFIX
    def shape(element, attrs, children):
        need(element is not None and set(element.attrib) <= set(attrs) and all(isinstance(c.tag, str) and c.tag in children for c in element if isinstance(c.tag,str)), 'Reindex profile contains unknown native grammar')
    def scalars(element, names):
        shape(element, (), names)
        need(all(not c.attrib and len(c)==0 for c in element if isinstance(c.tag,str)), 'Reindex scalar has opaque payload')
    need(table.attrib == {'ID':'0'}, 'Reindex requires captured table root ID')
    table_nodes = set(table.iter())
    for node in tree.iter():
        if not isinstance(node.tag,str): continue
        need(not ('ref' in node.tag.lower() or node.tag.lower().endswith('id')), 'Reference-like element refused')
        need(not any('ref' in key.lower() or key.lower().endswith('id') and key != 'ID' for key in node.attrib), 'Reference-like attribute refused')
        need('ID' not in node.attrib or node in table_nodes, 'External document ID refused')
    shape(table, ('ID',), ('AttributeList','ObjectList'))
    scalars(table.find('AttributeList'), ('Name',))
    shape(table.find('ObjectList'), (), (prefix+'PlcWatchTableEntry',))
    for row in _lifecycle.row_elements(tree):
        need(row.tag == prefix+'PlcWatchTableEntry' and row.get('CompositionName')=='Entries', 'Only observed watch signal rows admitted')
        shape(row, ('ID','CompositionName'), ('AttributeList','ObjectList'))
        fields = row.find('AttributeList')
        scalars(fields, ('Address','DisplayFormat','ModifyTrigger','ModifyValue','MonitorTrigger','Name'))
        need(fields.findtext('DisplayFormat')=='Bool' and fields.findtext('ModifyValue') in ('FALSE','TRUE') and fields.findtext('ModifyTrigger')=='Permanent' and fields.findtext('MonitorTrigger')=='Permanent' and fields.find('Name') is not None, 'Observed offline Bool fields required')
        objects = row.find('ObjectList');shape(objects, (), ('MultilingualText',))
        texts = objects.findall('MultilingualText')
        need(len(texts)==1 and texts[0].get('CompositionName')=='Comment','One exact multilingual comment required')
        text = texts[0];shape(text, ('ID','CompositionName'), ('ObjectList',))
        items = text.find('ObjectList');shape(items, (), ('MultilingualTextItem',))
        cultures=[]
        for item in items.findall('MultilingualTextItem'):
            shape(item, ('ID','CompositionName'), ('AttributeList',))
            need(item.get('CompositionName')=='Items','Exact native language item required')
            attrs=item.find('AttributeList');scalars(attrs, ('Culture','Text'))
            culture=attrs.findtext('Culture');need(culture and culture.strip() and attrs.find('Text') is not None,'Existing exact culture/text required');cultures.append(culture)
        need(cultures and len(cultures)==len(set(cultures)), 'Distinct captured cultures required')

def exact_delta(before, candidate, edit):
    if 'documentIdReindex' not in edit:
        return _strict_delta(before,candidate,edit)
    need=_lifecycle.need
    kind=edit.get('kind')
    need(kind in ('reorder','delete'), 'ID profile is restricted to reorder/delete')
    need(set(edit)==({'kind','order','documentIdReindex'} if kind=='reorder' else {'kind','ordinal','documentIdReindex'}), 'Exact reviewed fields required')
    declaration=edit['documentIdReindex']
    need(isinstance(declaration,dict) and set(declaration)=={'profile','mapping'} and declaration['profile']==PROFILE, 'Exact explicit document ID profile required')
    old,actual=_lifecycle.document(before),_lifecycle.document(candidate)
    reference_free(old);reference_free(actual)
    need(_lifecycle.external_xml_nodes(before)==_lifecycle.external_xml_nodes(candidate), 'External XML payload changed')
    expected=copy.deepcopy(old);rows=_lifecycle.row_elements(expected)
    objects=_lifecycle.base.table(expected).find('ObjectList')
    if kind=='delete':
        selected=rows[_lifecycle.ordinal(edit['ordinal'],len(rows))]
        _lifecycle.remove_element_preserving_tail(objects,selected)
    else:
        order=edit['order'];need(isinstance(order,list) and all(type(i)is int for i in order) and sorted(order)==list(range(len(rows))), 'Exact row permutation required')
        need(order!=list(range(len(rows))), 'No-op reorder cannot authorize ID changes')
        for previous,replacement in zip(rows,[copy.deepcopy(rows[i]) for i in order]):
            index=list(objects).index(previous);replacement.tail=previous.tail;objects.remove(previous);objects.insert(index,replacement)
    ids=[node for node in _lifecycle.base.table(expected).iter() if 'ID' in node.attrib]
    mapping={node.attrib['ID']:format(index,'X') for index,node in enumerate(ids)}
    need(declaration['mapping']==mapping and len(set(mapping.values()))==len(mapping), 'Reviewed mapping differs from independently calculated ordered bijection')
    for node in ids: node.set('ID',mapping[node.attrib['ID']])
    need(_lifecycle.base.xml_key(expected,False)==_lifecycle.base.xml_key(actual,False), 'Candidate differs from exact edit and explicit ID mapping')
    need(_lifecycle.base.xml_key(old,False)!=_lifecycle.base.xml_key(actual,False), 'Candidate has no effect')
    return {'kind':kind,'beforeRows':len(_lifecycle.row_elements(old)),'candidateRows':len(_lifecycle.row_elements(actual)),
        'unknownPayloadPreserved':True,'nativeIdsPreservedExceptFreshCloneIds':False,'explicitDocumentIdReindex':declaration,
        'nativeOutputNormalization':False}

_lifecycle.exact_delta=exact_delta

def verify(root):
    admission=_lifecycle.load(Path(root)/'admission.json')
    _lifecycle.need(admission.get('reviewedDocumentIdProfile')==PROFILE,'This separate qualification needs explicit ID-profile admission')
    for path in Path(root).glob('host-*-close.json'):
        close=_lifecycle.load(path)['close']
        tree=close.get('tree',{})
        _lifecycle.need(tree.get('completed') is True and close.get('closeInterrupted') is None and
            not tree.get('errors') and not tree.get('unknownReasons') and tree.get('startupOutcomeUnknown') is False,
            'Exact retained startup tree did not complete normally')
        _lifecycle.need(all(e['proof'].get('birthAndImageVerified') is True and e['proof'].get('sameOsHandleRetained') is True and
            e['proof'].get('completed') is True and e['proof'].get('exitCode') == 0 and e['proof'].get('deadlineOrCancellationObserved') is False
            for e in tree['exits']), 'Exact startup tree identity/terminal proof differs')
    return _lifecycle.verify(root)
