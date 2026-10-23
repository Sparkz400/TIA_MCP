"""Safety harness integration with the existing retained process controller."""
from component_exact_lifetime import ComponentOwnedScratch
from encoder_lifecycle import encoder_client as retained_client, sync_steps, finish_session, verify_lifecycle
from block_test_runtime import NativeToolError
from engineering_preservation import require


POLICY = 'safety-exact-retained-terminal-v1'


def new_session(host, worker, artifacts, report, persist, directory, timeout=300):
    report['lifetimePolicy'] = POLICY
    return ComponentOwnedScratch(host, worker, artifacts, report, persist,
        lifecycle_directory=directory, timeout=timeout, client_factory=retained_client,
        extra_tools={'safety_inspect', 'project_inventory', 'hardware_catalog_search', 'project_retrieve', 'device_inventory'})


def call(session, name, arguments=None, allow_error=False):
    require(session.session is not None and all(row.get('status') == 'passed' or row.get('expectedSafetyBoundary') is True
            for row in session.session['calls']), 'Uncertain safety request prohibits further native dispatch')
    try:
        value = session.call(name, arguments)
    except NativeToolError as error:
        row = session.session['calls'][-1]
        # Only this explicit negative probe is an expected refusal, never a lost reply.
        if not (allow_error and name == 'change_prepare' and (arguments or {}).get('kind') == 'source_upsert'
                and row.get('result') == error.result and error.result.get('error', {}).get('code') == 'SafetyBoundary'):
            raise
        row['expectedSafetyBoundary'] = True
        session.persist()
        return error.result
    return value


def finish(session, report, persist):
    finish_session(session, report, persist, policy=POLICY)


def verify(report):
    verify_lifecycle(report, policy=POLICY)
