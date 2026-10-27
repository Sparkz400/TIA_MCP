"""Independent public wire-contract boundaries; no server or TIA required."""
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT / 'contracts/opcua-runtime-subscription.v1.schema.json').read_text(encoding='utf-8'))


def validator(name):
    return Draft202012Validator({'$ref': '#/$defs/' + name, '$defs': SCHEMA['$defs']},
                               format_checker=Draft202012Validator.FORMAT_CHECKER)


class SubscriptionContractTests(unittest.TestCase):
    def setUp(self):
        self.request = {'attemptId': 'a' * 32,
                        'nodes': [{'namespaceUri':'urn:test:measurements', 'identifierType':'numeric','identifier':'42'}]}

    def test_schema_and_all_five_tool_contracts(self):
        Draft202012Validator.check_schema(SCHEMA)
        self.assertEqual(set(SCHEMA['x-tool-contracts']), {
            'runtime_ua_subscription_' + name for name in ('start','status','stop','page','export')})
        for tool in SCHEMA['x-tool-contracts'].values():
            for direction in ('input','output'):
                self.assertIn(tool[direction]['$ref'].split('/')[-1], SCHEMA['$defs'])

    def test_existing_scalar_contract_is_retained_exactly(self):
        old = json.loads((ROOT / 'contracts/opcua-runtime.v1.schema.json').read_text(encoding='utf-8'))
        for name in ('node','selection','value','arrayValue'):
            self.assertEqual(old['$defs'][name], SCHEMA['$defs'][name])
        self.assertEqual(old['$defs']['status'], SCHEMA['$defs']['sessionStatus'])

    def test_minimal_request_and_explicit_bounds(self):
        validator('request').validate(self.request)
        request = {**self.request, 'publishingIntervalMilliseconds':1000, 'samplingIntervalMilliseconds':0,
            'queueSize':1024, 'discardOldest':False, 'keepAliveCount':10, 'lifetimeCount':30,
            'durationSeconds':3600, 'maximumNotifications':100000, 'maximumBytes':67108864, 'maximumRepublish':64}
        validator('request').validate(request)
        validator('normalizedRequest').validate(request)
        self.assertFalse(validator('normalizedRequest').is_valid(self.request))

    def test_array_wire_envelope_bounds_and_unknown_fields(self):
        value = {'dimensions': [2, 3], 'elementEncoding': 'decimalInteger',
                 'elements': ['-9223372036854775808', '-1', '0', '1', '2', '9223372036854775807']}
        validator('arrayValue').validate(value)
        validator('arrayValue').validate({**value, 'dimensions': [0, 3], 'elements': []})
        for changed in ({'dimensions': [4097]}, {'dimensions': [1] * 33}, {'elements': [None] * 4097},
                        {'elementEncoding': 'guessed'}, {'unexpected': True}, {'elements': [[1]]}):
            with self.subTest(changed=next(iter(changed))):
                self.assertFalse(validator('arrayValue').is_valid({**value, **changed}))

    def test_out_of_profile_values_refuse(self):
        cases = {'publishingIntervalMilliseconds':[99,1001,100.5], 'samplingIntervalMilliseconds':[-1,60001],
                 'queueSize':[0,1025], 'keepAliveCount':[0,11], 'lifetimeCount':[2,1001],
                 'durationSeconds':[0,3601], 'maximumNotifications':[0,100001], 'maximumBytes':[1048575,67108865],
                 'maximumRepublish':[-1,65], 'discardOldest':[0,'true'], 'attemptId':['A'*32,'a'*31]}
        for field, values in cases.items():
            for value in values:
                with self.subTest(field=field, value=value):
                    self.assertFalse(validator('request').is_valid({**self.request, field:value}))

    def test_empty_duplicate_and_excessive_node_sets_refuse(self):
        for nodes in [[], self.request['nodes']*2,
                      [dict(self.request['nodes'][0],identifier=str(i)) for i in range(65)]]:
            self.assertFalse(validator('request').is_valid({**self.request,'nodes':nodes}))

    def test_event_only_and_mixed_selection(self):
        selected = dict(notifier=self.request['nodes'][0], minimumSeverity=500)
        for nodes in ([], self.request['nodes']):
            validator('request').validate({**self.request, 'nodes':nodes, 'events':selected})
        for changed in (dict(minimumSeverity=-1),dict(minimumSeverity=1001),dict(minimumSeverity=1.5),dict(acknowledge=True),dict(notifier=None)):
            self.assertFalse(validator('request').is_valid({**self.request,'events':{**selected,**changed}}))
        self.assertFalse(validator('request').is_valid({**self.request,'nodes':[],'events':selected,'diagnosticBindingId':'a'*64}))
        validator('request').validate({**self.request,'events':None})
        self.assertFalse(validator('request').is_valid({**self.request,'nodes':[],'events':None}))

    def test_unadvertised_control_and_recovery_requests_refuse(self):
        for field in ('reconnect','transfer','write','resume','method','force'):
            self.assertFalse(validator('request').is_valid({**self.request,field:True}))

    def test_bounded_recovery_contracts_agree(self):
        polling = json.loads((ROOT / 'contracts/opcua-runtime-capture.v1.schema.json').read_text(encoding='utf-8'))
        self.assertEqual(polling['$defs']['reconnectEvent'], SCHEMA['$defs']['reconnectEvent'])
        for schema in (polling, SCHEMA):
            request = Draft202012Validator(schema['$defs']['request']['properties']['maximumReconnectAttempts'])
            for value in (0, 1, 3): self.assertTrue(request.is_valid(value))
            for value in (-1, 4, 0.5, True): self.assertFalse(request.is_valid(value))
        event = {'attempt':1, 'state':'starting', 'errorCode':'UaTransportInterrupted'}
        validator('reconnectEvent').validate(event)
        validator('reconnectEvent').validate({**event, 'state':'succeeded', 'errorCode':None})
        validator('reconnectEvent').validate({**event, 'state':'failed', 'errorCode':'UaSessionExpired'})
        for change in ({'attempt':4}, {'errorCode':'UaAuthenticationDenied'}, {'state':'succeeded'}, {'extra':True}):
            self.assertFalse(validator('reconnectEvent').is_valid({**event, **change}))
        succeeded = {**event, 'state':'succeeded', 'errorCode':None}
        validator('reconnectEvent').validate({**succeeded, 'transfer':'transferred'})
        validator('reconnectEvent').validate({**succeeded, 'transfer':'refused', 'transferStatusCode':'0x801f0000'})
        for change in ({'transfer':'refused'}, {'transfer':'transferred', 'transferStatusCode':'0x801f0000'},
                       {'transfer':'refused', 'transferStatusCode':'0x00000000'}, {'transfer':'resumed'}, {'transferStatusCode':'0x801f0000'}):
            self.assertFalse(validator('reconnectEvent').is_valid({**succeeded, **change}), change)
        self.assertFalse(validator('reconnectEvent').is_valid({**event, 'transfer':'transferred'}))

    def test_page_and_export_boundaries(self):
        ref = 'ua-subscription:'+'b'*32+':1:'+'c'*64
        validator('pageInput').validate(dict(snapshotRef=ref,count=200,offset=0))
        for updates in [dict(count=201),dict(count=0),dict(offset=-1),dict(snapshotRef=ref.replace(':1:',':01:'))]:
            self.assertFalse(validator('pageInput').is_valid({'snapshotRef':ref, **updates}))
        for fmt in ('json','csv','html','markdown'): validator('exportInput').validate(dict(snapshotRef=ref,format=fmt))
        self.assertFalse(validator('exportInput').is_valid(dict(snapshotRef=ref,format='binary')))

    def test_event_kind_and_sequence_requirements(self):
        event = dict(kind='publish', subscriptionId=1, sequenceNumber=1, publishTime='2026-10-21T00:00:00Z',
            keepAlive=True, duplicate=False, moreNotifications=False, availableSequenceNumbers=[],
            acknowledgedSequenceNumbers=[], acknowledgementResults=[], notifications=[], statusChanges=[],
            requestedSequenceNumber=None,statusCode=None,created=None,errorCode=None,gapFrom=None,gapTo=None,missingCount=0)
        validator('event').validate(event)
        for field, value in [('sequenceNumber',0),('sequenceNumber',4294967296),('subscriptionId',0),
                             ('publishTime',None),('kind','poll'),('gapFrom',1),('missingCount',1)]:
            self.assertFalse(validator('event').is_valid({**event,field:value}))
        gap = {**event,'kind':'gap','sequenceNumber':None,'publishTime':None,'keepAlive':False,
               'gapFrom':2,'gapTo':5,'missingCount':4,'availableSequenceNumbers':None,
               'acknowledgedSequenceNumbers':None,'acknowledgementResults':None,'notifications':None,'statusChanges':None}
        validator('event').validate(gap)
        self.assertFalse(validator('event').is_valid({**gap,'missingCount':0}))
        self.assertFalse(validator('event').is_valid({**gap,'notifications':[]}))
        self.assertFalse(validator('event').is_valid({**gap,'sequenceNumber':2}))
        self.assertFalse(validator('event').is_valid({**event,'requestedSequenceNumber':1}))

    def test_progress_terminal_reason_is_not_fabricated(self):
        progress = dict(state='running',terminalReason=None,elapsedMilliseconds=0,
            observedUtc='2026-10-21T00:00:00Z',rows=0,notifications=0,duplicateMessages=0,missingSequences=0,
            remoteCleanupConfirmed=False)
        validator('progress').validate(progress)
        self.assertFalse(validator('progress').is_valid({**progress,'terminalReason':'stopped'}))
        self.assertFalse(validator('progress').is_valid({**progress,'state':'ended'}))
        validator('progress').validate({**progress,'state':'ended','terminalReason':'stopped'})

    def test_qualification_and_transfer_claims_cannot_be_promoted(self):
        properties = SCHEMA['$defs']['captureStatus']['properties']
        for name in ('nativeQualified','physicalQualified','programMatchVerified','sourceCompletenessVerified',
                     'atomicAcrossSignals','scanSynchronized'):
            self.assertFalse(Draft202012Validator(properties[name]).is_valid(True),name)
        for name in ('reconnectPolicy','transferPolicy'):
            self.assertFalse(Draft202012Validator(properties[name]).is_valid('automatic'),name)
        self.assertFalse(Draft202012Validator(properties['acquisitionMode']).is_valid('polling'))


if __name__ == '__main__':
    unittest.main()
