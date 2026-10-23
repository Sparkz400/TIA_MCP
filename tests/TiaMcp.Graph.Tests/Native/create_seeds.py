"""Generate independently authored GRAPH import seeds; never native exports."""
from pathlib import Path
from copy import deepcopy
import xml.etree.ElementTree as ET

fixtures = Path(__file__).resolve().parent.parent / 'fixtures'
G = 'http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v6'
I = 'http://www.siemens.com/automation/Openness/SW/Interface/v5'
ET.register_namespace('g', G)
ET.register_namespace('i', I)

def node(name, **attrs):
    return ET.Element('{' + G + '}' + name, {k: str(v) for k, v in attrs.items()})

def local(parent, name):
    return next(e for e in parent.iter() if e.tag.split('}')[-1] == name)

next_uid = 10
def condition(symbol, coil):
    global next_uid
    offset = next_uid - 10
    next_uid += 10
    net = node('FlgNet')
    parts = ET.SubElement(net, '{' + G + '}Parts')
    access = node('Access', Scope='LocalVariable', UId=10)
    symbol_xml = ET.SubElement(access, '{' + G + '}Symbol')
    symbol_xml.append(node('Component', Name=symbol))
    parts.extend([access, node('Part', Name='Contact', UId=11), node('Part', Name=coil, UId=12)])
    wires = ET.SubElement(net, '{' + G + '}Wires')
    for uid, ends in [(13, [node('Powerrail'), node('NameCon', UId=11, Name='in')]),
                      (14, [node('IdentCon', UId=10), node('NameCon', UId=11, Name='operand')]),
                      (15, [node('NameCon', UId=11, Name='out'), node('NameCon', UId=12, Name='in')])]:
        wire = node('Wire', UId=uid)
        wire.extend(ends)
        wires.append(wire)
    for el in net.iter():
        if el.get('UId') is not None: el.set('UId', str(int(el.get('UId')) + offset))
    return net

seed = ET.parse(fixtures / 'native-seed.xml')
root = seed.getroot()
old = local(root, 'Graph')
chart = ET.parse(fixtures / 'sequence-v1.xml').getroot()
for el in chart.iter(): el.tag = '{' + G + '}' + el.tag
local(root, 'NetworkSource').remove(old)
local(root, 'NetworkSource').append(chart)
# Native inputs/outputs declared independently of retained language XML.
sections = local(root, 'Sections')
for section in sections:
    for symbol in {'Input': ['StartCommand', 'ResetCommand', 'GuardClosed', 'SupervisionTrip'], 'Output': ['MotorA', 'MotorB']}.get(section.get('Name'), []):
        ET.SubElement(section, '{' + I + '}Member', Name=symbol, Datatype='Bool')
local(chart, 'PreOperations').clear()
for step in local(chart, 'Steps'):
    if step.get('Init') == '0': step.set('Init', 'false')
    if step.get('Init') == '1': step.set('Init', 'true')
    actions = local(step, 'Actions')
    actions.clear()
    number = step.get('Number')
    if number in ('2', '3'):
        action = node('Action', Event='S1' if number == '2' else '', Qualifier='N', Interlock='true' if number == '2' else 'false')
        action.append(node('Token', Text='#MotorA' if number == '2' else '#MotorB'))
        actions.append(action)
    for kind, symbol, coil in [('Supervision', 'SupervisionTrip', 'SvCoil'), ('Interlock', 'GuardClosed', 'IlCoil')]:
        module = local(step, kind)
        module.set('ProgrammingLanguage', 'LAD')
        module.remove(local(module, 'FlgNet'))
        module.append(condition(symbol, coil))
for transition in local(chart, 'Transitions'):
    transition.set('ProgrammingLanguage', 'LAD')
    transition.remove(local(transition, 'FlgNet'))
    transition.append(condition('StartCommand' if transition.get('Number') == '1' else 'ResetCommand', 'TrCoil'))
ET.indent(seed, '  ')
# TIA's interface importer rejects equivalent prefixed Member elements. Use local default namespaces.
for el in root.iter():
    if el.tag.startswith('{' + G + '}') or el.tag.startswith('{' + I + '}'):
        el.tag = el.tag.split('}', 1)[1]
chart.set('xmlns', G)
sections.set('xmlns', I)
seed.write(fixtures / 'native-rich-seed.xml', encoding='utf-8', xml_declaration=True)
