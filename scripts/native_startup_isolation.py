"""Exact 21 October text-list startup failure; never generic ticket recovery.

Pinned originals reviewed in docs/verification/native-resume-20261023.md.
A later full boot permits a separate owned workspace, not historical cleanup.
"""
from pathlib import Path
import native_dispatch_guard as guard

STARTUP_INTENT_SHA256 = "bcaed8a412b945c867ac5b3fca53a36e12297f5e8468c7050bb68fc7cad1d681"
# Paths relative to the pinned admission. Cross-artifact inputs remain immutable.
STARTUP_FILES = {'admission.json': 'b293f265d222c51c44c1e3d156140a37d5b5e32f1be3419ebb16c5084b7858a0',
 'native/result.json': '6fbfc759ba5209e5305da7501a86aa083cbc8f9eb642b8bd9a5e532bc8499eb6',
 'native/start-intent.json': '36cb935ab76059a58da958edacd229a4f075909939fdccc1eaa66e4ed9c51aab',
 'cleanup-live.json': '5828b87ec575f094cb7cd770676d2406216d12afdce1c17c6ef3431915e0848e',
 'fixture-tree-hold.json': 'd2eb9e38ead568eb47ef03596726fdc14fabddba905b1355e1a9c335e5cb4e74',
 'startup-failure-observation-01.json': '35595ad6e36ddd397ac29723f5eb6b0f689742e6a5da4d9e78416eeaef28138c',
 'window-intent.json': '067e7c7ed2e43783cbbd523bb4c691ea4e4d4f2df14c03d9e6d20c66d6cb624b',
 '../w6uni01/result.json': '92ce7ad1385780d7d4c1f0512e06e7220de19ccfba576721c808287d2e511c14',
 '../w6uni01/native/configured.zap21': '90849fb7a9dadb286564b6e8d48fa42214d01e5ead78f03aeca67f9398117170',
 '../w6-text-list-prep/admission-02/authoring/author-intent.json': '458f7c136248ccb5928282c1cdce7dd9960f82af0cfd76bf054a03c4308b6381',
 '../w6-text-list-prep/admission-02/authoring/author-receipt.json': '2b95bbeaa5cb33fff0a33bc6fcf02ba095b30956a977bd2536b95a26f6ee5444',
 '../w6-text-list-prep/admission-02/authoring/pair/QualificationTextLists.hmi.yml': '1e34313f0bd201b3de5858e5420626132316db3187e9d2055d3daa157a50a24f',
 '../w6-text-list-prep/admission-02/authoring/pair/QualificationTextLists.TextLibrary.hmi.yml': 'b92de92f93ad3732ddc07d9bf1cb1313dff9955e9102016bec7be452e4e8942b',
 '../w6-text-list-final02-build/bin/HmiTextListQualification/release/HmiTextListQualification.exe': '921bcc64b0b71937940d31544faae9a353f8317938af27203b57fdb0f8f190a9',
 'process-44652-1792568291018201-start.json': '5e17875ff0a293ef017795a71650cd3134fe1fc952830f885c69d0f071a1d0bd',
 'process-45984-1792568291043126-start.json': 'a7475ff0ab69d55c97721032f79c86299e2105206703bb097bc54301ae4f9d0e',
 'process-46124-1792568291618952-start.json': 'd8e90425bceaf780655280ce0a0d5552bf7d817cb2cba50c24b1ea5802d0ebe5',
 'process-48692-1792569344562954-start.json': '4d6f68eabfbe0f4ab423f984fbd55093ffd21510b2792c6e026f820ad3f132ea',
 'process-54072-1792568292512063-start.json': 'd083c962c30cff8e9185c2447bf6e45bb005c4988f1379387dbbcd68a03c7872'}


def startup_evidence(intent_path, workspace):
    from native_dispatch_isolation import canonical, overlaps
    intent_ref = guard.reference(intent_path)
    guard.require(intent_ref['sha256'] == STARTUP_INTENT_SHA256,
                  'Startup intent is not the exact reviewed prior-boot attempt')
    intent = guard.read(intent_path)
    source = intent['source']
    old_root = canonical(Path(source['path']).parent)
    guard.require(source['sha256'] == STARTUP_FILES['admission.json']
                  and source == guard.reference(old_root / 'admission.json'), 'Startup admission changed')
    references = [intent_ref]
    for relative, digest in STARTUP_FILES.items():
        path = canonical(old_root / relative)
        guard.require(not overlaps(workspace, path.parent), 'Isolated workspace overlaps historical artifacts')
        ref = guard.reference(path)
        guard.require(ref['sha256'] == digest, 'Pinned startup evidence changed: ' + relative)
        references.append(ref)
    admission = guard.read(source['path'])
    # The pinned admission binds every candidate, source archive and prior terminal.
    for ref in [admission[k] for k in ('configuredResult', 'configuredTerminal', 'archive',
                'storeWindowIntent', 'authorIntent', 'authorReceipt')] + admission['candidateFiles']:
        path = canonical(ref['path'])
        guard.require(not overlaps(workspace, path.parent), 'Isolated workspace overlaps historical inputs')
        guard.require(ref == guard.reference(path), 'Startup input changed')
        if ref not in references:
            references.append(ref)
    guard.require(canonical(admission['artifactRoot']) == old_root
                  and canonical(admission['outputDirectory']) == old_root / 'native', 'Startup artifact scope changed')
    result = guard.read(old_root / 'native/result.json')
    guard.require(result.get('completed') is False and result.get('stages') == []
                  and result.get('productionMutationEnabled') is False
                  and result.get('failure', {}).get('type') == 'Siemens.Engineering.EngineeringSecurityException',
                  'Startup failure boundary changed')
    hold = guard.read(old_root / 'fixture-tree-hold.json')
    cleanup = guard.read(old_root / 'cleanup-live.json')
    observation = guard.read(old_root / 'startup-failure-observation-01.json')
    guard.require(hold.get('dependentDispatchProhibited') is True
                  and cleanup.get('automaticTermination') is False
                  and observation.get('terminalProof') is False, 'Historical cleanup must remain unknown')
    helper = guard.reference(canonical(hold['anchor']['exe']))
    guard.require(helper in references and helper['sha256'] == admission['helperSha256'], 'Startup helper changed')
    births = [hold['anchor']['created'], cleanup['coordinator']['created']]
    births.extend(row['identity']['created'] for row in cleanup['retained'])
    return source, references, births
