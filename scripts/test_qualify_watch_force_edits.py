"""Offline checks of the independent force-edit review in qualify_watch_force_edits.py; nothing here starts TIA.

The authored table is a test variant built on the genuine empty Update 3 force export, not a native capture.
"""
import copy
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

import qualify_watch_force_edits as q
import verify_watch_workflow as base

EMPTY = Path(__file__).resolve().parents[1] / 'fixtures/watch-tables/native-authoring-v21-update3/baseline/force-0.xml'


def raw(tree):
    return ET.tostring(tree, encoding='utf-8', xml_declaration=True)


def authored():
    tree = base.xml(EMPTY.read_bytes()); objects = ET.SubElement(q.force_table(tree), 'ObjectList'); ids = iter(range(1, 64))
    for address, display, value, text in q.ROWS + ((None, None, None, q.COMMENT_LINE),):
        row = ET.SubElement(objects, q.FORCE if address else q.COMMENT, {'ID': format(next(ids), 'X'), 'CompositionName': 'Entries'})
        if address:
            fields = ET.SubElement(row, 'AttributeList')
            for name, field in (('Address', address), ('DisplayFormat', display), ('ForceValue', value), ('MonitorTrigger', 'Permanent'), ('Name', '')):
                ET.SubElement(fields, name).text = field or None
        comment = ET.SubElement(ET.SubElement(row, 'ObjectList'), 'MultilingualText', {'ID': format(next(ids), 'X'), 'CompositionName': 'Comment'})
        items = ET.SubElement(comment, 'ObjectList')
        for culture, value in (('de-DE', text), ('en-GB', '')):
            item = ET.SubElement(ET.SubElement(items, 'MultilingualTextItem', {'ID': format(next(ids), 'X'), 'CompositionName': 'Items'}), 'AttributeList')
            ET.SubElement(item, 'Culture').text = culture; ET.SubElement(item, 'Text').text = value or None
    return tree


def typed(tree):
    rows = []
    for row in q.entries(tree):
        value = {'kind': row.tag[len(base.PREFIX):]}
        if row.tag == q.FORCE:
            value.update({f[0].lower() + f[1:]: row.findtext('AttributeList/' + f) or '' for f in q.FIELDS}, forceIntention=False)
        rows.append(value)
    return {'target': {'kind': 'force', 'isConsistent': True, 'rows': rows}}


def edited(change):
    tree = authored(); change(tree, q.entries(tree), q.force_table(tree).find('ObjectList')); return raw(tree)


def clone_to_end(tree, rows, objects, ordinal, first=None):
    clone = copy.deepcopy(rows[ordinal]); next_id = first or 1 + max(int(n.attrib['ID'], 16) for n in tree.iter() if 'ID' in n.attrib)
    for node in clone.iter():
        if 'ID' in node.attrib: node.attrib['ID'] = format(next_id, 'X'); next_id += 1
    objects.append(clone)


def reorder(objects, rows, order):
    for previous, replacement in zip(rows, [copy.deepcopy(rows[i]) for i in order]):
        index = list(objects).index(previous); objects.remove(previous); objects.insert(index, replacement)


class ForceEditReviewTests(unittest.TestCase):
    def test_capture_matches_typed_rows_and_plans_every_kind(self):
        tree = authored(); rows = q.captured(typed(tree), raw(tree))
        self.assertEqual(['PlcForceTableEntry'] * 3 + ['PlcTableCommentEntry'], [r['kind'] for r in rows])
        labels = [label for label, _ in q.plan(rows)]
        self.assertEqual(['field-forcevalue', 'field-address', 'field-displayformat', 'comment', 'clone-comment-line', 'delete-last', 'reorder', 'clone-signal'], labels)
        self.assertEqual('de-DE', dict(q.plan(rows))['comment']['culture'])

    def test_capture_refuses_selection_and_typed_drift(self):
        tree = authored(); snapshot = typed(tree)
        snapshot['target']['rows'][1]['forceIntention'] = True
        with self.assertRaises(RuntimeError): q.captured(snapshot, raw(tree))
        snapshot = typed(tree); snapshot['target']['rows'][0]['address'] = '%I0.1:P'
        with self.assertRaises(RuntimeError): q.captured(snapshot, raw(tree))
        selected = edited(lambda t, rows, o: ET.SubElement(rows[0].find('AttributeList'), 'ForceIntention').__setattr__('text', 'true'))
        with self.assertRaises(RuntimeError): q.captured(typed(base.xml(selected)), selected)

    def test_plan_refuses_a_table_that_differs_from_the_instructions(self):
        rows = q.describe(edited(lambda t, rows, o: rows[1].find('AttributeList/Address').__setattr__('text', '%Q0.3:P')))
        with self.assertRaises(RuntimeError): q.plan(rows)
        rows = q.describe(edited(lambda t, rows, o: o.remove(rows[3])))
        with self.assertRaises(RuntimeError): q.plan(rows)

    def test_delta_accepts_exactly_each_reviewed_edit(self):
        before = raw(authored())
        cases = [
            ({'kind': 'field', 'ordinal': 0, 'field': 'ForceValue', 'before': 'TRUE', 'after': 'FALSE'},
             lambda t, rows, o: rows[0].find('AttributeList/ForceValue').__setattr__('text', 'FALSE')),
            ({'kind': 'field', 'ordinal': 2, 'field': 'DisplayFormat', 'before': 'Hex', 'after': 'DEC_unsigned'},
             lambda t, rows, o: rows[2].find('AttributeList/DisplayFormat').__setattr__('text', 'DEC_unsigned')),
            ({'kind': 'comment', 'ordinal': 0, 'culture': 'de-DE', 'text': 'Force row A reviewed'},
             lambda t, rows, o: rows[0].find('.//MultilingualTextItem/AttributeList/Text').__setattr__('text', 'Force row A reviewed')),
            ({'kind': 'clone', 'ordinal': 3, 'insertAt': 4}, lambda t, rows, o: clone_to_end(t, rows, o, 3)),
            ({'kind': 'delete', 'ordinal': 3}, lambda t, rows, o: o.remove(rows[3])),
            ({'kind': 'reorder', 'order': [1, 0, 2, 3]}, lambda t, rows, o: reorder(o, rows, [1, 0, 2, 3]))]
        for edit, change in cases:
            with self.subTest(edit['kind']):
                q.force_delta(before, edited(change), edit)

    def test_delta_refuses_extra_changes_reused_ids_and_absent_fields(self):
        before = raw(authored())
        def both(t, rows, o):
            rows[0].find('AttributeList/ForceValue').text = 'FALSE'; rows[1].find('AttributeList/ForceValue').text = 'TRUE'
        with self.assertRaises(RuntimeError):
            q.force_delta(before, edited(both), {'kind': 'field', 'ordinal': 0, 'field': 'ForceValue', 'before': 'TRUE', 'after': 'FALSE'})
        with self.assertRaises(RuntimeError):
            q.force_delta(before, edited(lambda t, rows, o: clone_to_end(t, rows, o, 3, first=1)), {'kind': 'clone', 'ordinal': 3, 'insertAt': 4})
        with self.assertRaises(RuntimeError):
            q.force_delta(before, before, {'kind': 'field', 'ordinal': 3, 'field': 'ForceValue', 'before': None, 'after': 'TRUE'})

    def test_refused_readbacks_are_classified_by_document_ids(self):
        candidate = edited(lambda t, rows, o: reorder(o, rows, [1, 0, 2, 3]))
        renumbered = base.xml(candidate)
        for number, node in enumerate(n for n in renumbered.iter() if 'ID' in n.attrib): node.attrib['ID'] = format(number, 'X')
        self.assertTrue(q.ids_only_difference(candidate, raw(renumbered)))
        self.assertFalse(q.ids_only_difference(candidate, candidate))
        renumbered.find('.//Address').text = '%Q0.4:P'
        self.assertFalse(q.ids_only_difference(candidate, raw(renumbered)))

    def test_confirmation_needs_every_answer(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'operator-confirmation.json'
            answers = {key: True for key, _ in q.QUESTIONS}
            path.write_text(json.dumps({'answers': answers, 'confirmedAtUtc': q.utc()}), encoding='utf-8')
            self.assertTrue(all(q.read_confirmation(path)['answers'].values()))
            del answers['offlineOnly']
            path.write_text(json.dumps({'answers': answers, 'confirmedAtUtc': q.utc()}), encoding='utf-8')
            with self.assertRaises(RuntimeError): q.read_confirmation(path)

    def test_instructions_name_every_row(self):
        text = q.INSTRUCTIONS.format(root='R')
        self.assertTrue(all(address in text and comment in text for address, _, _, comment in q.ROWS) and q.COMMENT_LINE in text and '"R"' in text)


if __name__ == '__main__':
    unittest.main()
