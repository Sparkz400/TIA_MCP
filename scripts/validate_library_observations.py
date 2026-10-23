"""Validate original library observation bytes locally, without following embedded paths."""
from __future__ import annotations

import argparse
from datetime import datetime
from decimal import Decimal, InvalidOperation
from functools import lru_cache
import hashlib
import json
import math
from pathlib import Path
import re

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource
from library_operation_observations import check_operation
from library_hmi_query_observations import check_hmi_query
from library_compilation_observations import check_compilation, check_compiler_context
from library_update_observations import check_update
from library_synchronization_observations import check_synchronization
from library_hmi_preview_observations import check_hmi_preview
from library_component_preview_observations import check_component_preview
from library_preview_effect_observations import check_preview_effects

ROOT = Path(__file__).resolve().parents[1]
MAX_BYTES = 4 * 1024 * 1024
KINDS = {"identity": "library-identity.v1.schema.json", "lock": "library-lock.v1.schema.json",
         "plan": "library-plan.v1.schema.json", "change": "library-change-observation.v1.schema.json",
         "inspection": "library-inspection.v1.schema.json",
         "export": "library-export.v1.schema.json", "export-manifest": "library-export-manifest.v1.schema.json",
         "operation-plan": "library-plan.v1.schema.json", "operation-change": "library-change-observation.v1.schema.json",
         "hmi-capabilities": "library-hmi-query.v1.schema.json", "hmi-targets": "library-hmi-query.v1.schema.json",
         "hmi-inventory": "library-hmi-query.v1.schema.json", "hmi-request": "library-hmi-query.v1.schema.json",
         "compiler": "library-compilation.v1.schema.json", "compiler-change": "library-change-observation.v1.schema.json",
         "compiler-lock": "library-lock.v1.schema.json",
         "update-plan": "library-plan.v1.schema.json", "update-change": "library-change-observation.v1.schema.json",
         "sync-plan": "library-plan.v1.schema.json", "sync-change": "library-change-observation.v1.schema.json",
         "hmi-plan": "library-plan.v1.schema.json", "hmi-change": "library-change-observation.v1.schema.json",
         "component-plan": "library-plan.v1.schema.json", "component-change": "library-change-observation.v1.schema.json",
         "preview-plan": "library-plan.v1.schema.json", "preview-change": "library-change-observation.v1.schema.json"}
OPERATION_MODES = frozenset(('operation-plan', 'operation-change', 'compiler-change', 'update-plan', 'update-change', 'sync-plan', 'sync-change', 'hmi-plan', 'hmi-change', 'component-plan', 'component-change', 'preview-plan', 'preview-change'))
TIMESTAMP = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:[Zz]|[+-](?:[01][0-9]|2[0-3]):[0-5][0-9])")


def _timestamp(value):
    if not isinstance(value, str) or not TIMESTAMP.fullmatch(value):
        return False
    try:
        datetime.fromisoformat(value[:-1] + "+00:00" if value[-1:] in ("Z", "z") else value)
        return True
    except ValueError:
        return False


def _pairs(values):
    result = {}
    for key, value in values:
        if key in result:
            raise ValueError("Duplicate JSON object key")
        result[key] = value
    return result


def _constant(_):
    raise ValueError("Non-finite JSON number")


def _float(text):
    if not math.isfinite(float(text)):
        raise ValueError("JSON number exceeds the finite float range")
    try:
        return Decimal(text)
    except InvalidOperation as failure:
        raise ValueError("JSON decimal is outside the supported range") from failure


def _same(left, right):
    # Preserve exact decimal values and distinguish bool/int/decimal. Comparing
    # float-rounded serializations can silently equate altered observations.
    if type(left) is not type(right):
        return False
    if isinstance(left, dict):
        return left.keys() == right.keys() and all(_same(value, right[key]) for key, value in left.items())
    if isinstance(left, list):
        return len(left) == len(right) and all(_same(a, b) for a, b in zip(left, right))
    if isinstance(left, Decimal) and left.is_zero() and right.is_zero():
        return left.is_signed() == right.is_signed()
    return left == right


def _require_same(left, right, field):
    if not _same(left, right):
        raise ValueError("Inconsistent observation field: " + field)


def _plan_coherence(plan):
    if "documentSource" in plan:
        _require_same(plan["documentsHash"], plan["documentSource"]["sha256"], "documentsHash")
    if "compileTargetPaths" in plan and len(plan["compileScope"]) != len(plan["compileTargetPaths"]):
        raise ValueError("Inconsistent observation field: compileTargetPaths count")


def _coherence(value, kind):
    if kind in ("hmi-capabilities", "hmi-targets", "hmi-inventory", "hmi-request"):
        check_hmi_query(value, kind, _require_same)
    elif kind in ("export", "export-manifest"):
        paths, relative_paths = set(), set()
        for item in value["files"]:
            relative = item["relativePath"]
            if any(part in ("", ".", "..") for part in relative.split("/")) or any(c in relative for c in "\\:\0"):
                raise ValueError("Invalid observation field: relativePath")
            if item["path"] in paths or relative in relative_paths:
                raise ValueError("Duplicate observation file path")
            paths.add(item["path"])
            relative_paths.add(relative)
            expected_media = "application/xml" if relative.lower().endswith(".xml") else "application/octet-stream"
            _require_same(item["mediaType"], expected_media, "mediaType")
        if kind == "export":
            _require_same(value["directory"], value["documentDirectory"], "documentDirectory")
            if value["filesTruncated"] and len(value["files"]) != 200:
                raise ValueError("Inconsistent observation field: filesTruncated")
            if value["manifest"]["path"] in paths:
                raise ValueError("Export manifest cannot be listed as an exported document")
    elif kind == "inspection":
        completeness = value["completeness"]
        if completeness["scopeComplete"] == completeness["truncated"]:
            raise ValueError("Inconsistent observation field: completeness flags")
        count, total, following = len(value["items"]), value["totalCaptured"], value["nextOffset"]
        if count > total:
            raise ValueError("Inconsistent observation field: totalCaptured")
        if following is not None and (count == 0 or following < count or following >= total):
            raise ValueError("Inconsistent observation field: nextOffset")
        if value["query"] == "versions" and total == 10001 and completeness["scopeComplete"]:
            raise ValueError("Inconsistent observation field: version capture exceeds complete scope")
    elif kind == "plan":
        _plan_coherence(value)
    elif kind == "change":
        journal = value["journal"]
        preview = journal["preview"]
        _plan_coherence(preview)
        _require_same(value["changeId"], journal["changeId"], "changeId")
        for field in ("kind", "sessionGeneration"):
            _require_same(journal[field], preview[field], field)
        if value.get("recoveredFromDisk") is True:
            attempts = journal.get("recoveryAttempts", [])
            if attempts:
                expected = "restored_to_new_locations" if attempts[-1]["state"] == "completed" else "recovery_outcome_unknown"
            else:
                expected = journal["state"] if journal["state"] in ("prepared", "applied") else "outcome_unknown"
            _require_same(value["state"], expected, "reconciled state")
        else:
            for field in ("state", "previewHash", "preview"):
                _require_same(value[field], journal[field], field)


@lru_cache(maxsize=1)
def _schema_registry():
    # Packaged schema resources are immutable for the lifetime of this process.
    schemas = {key: json.loads((ROOT / "contracts" / name).read_bytes()) for key, name in KINDS.items()}
    schemas["journal"] = json.loads((ROOT / "contracts/library-change-journal.schema.json").read_bytes())
    schemas["operation"] = json.loads((ROOT / "contracts/library-operation.v1.schema.json").read_bytes())
    schemas["update-analysis"] = json.loads((ROOT / "contracts/library-update-analysis.v1.schema.json").read_bytes())
    schemas["synchronization"] = json.loads((ROOT / "contracts/library-synchronization.v1.schema.json").read_bytes())
    schemas["hmi-preview"] = json.loads((ROOT / "contracts/library-hmi-preview.v1.schema.json").read_bytes())
    schemas["component-preview"] = json.loads((ROOT / "contracts/library-component-preview.v1.schema.json").read_bytes())
    schemas["preview-effects"] = json.loads((ROOT / "contracts/library-preview-effects.v1.schema.json").read_bytes())
    schemas["recipe"] = json.loads((ROOT / "contracts/plc-library-integration-recipe.v1.schema.json").read_bytes())
    for schema in schemas.values():
        Draft202012Validator.check_schema(schema)
    registry = Registry().with_resources((schema["$id"], Resource.from_contents(schema)) for schema in schemas.values())
    checker = FormatChecker()
    # jsonschema's optional RFC3339 dependency is not installed in every bundle.
    # Validate native DateTime observations explicitly; never silently skip dates.
    checker.checks("date-time")(_timestamp)
    return schemas, registry, checker


def validator(kind):
    if kind not in KINDS and kind not in ("operation", "update-analysis", "synchronization", "hmi-preview", "component-preview", "preview-effects"):
        raise ValueError("Unknown observation kind")
    schemas, registry, checker = _schema_registry()
    schema = schemas[kind]
    if kind in ("hmi-capabilities", "hmi-targets", "hmi-inventory", "hmi-request"):
        schema = {"$ref": schema["$id"] + "#/$defs/" + kind.removeprefix("hmi-")}
    return Draft202012Validator(schema, registry=registry, format_checker=checker)


def validate_bytes(raw, kind, expected_sha256=None):
    if kind not in KINDS:
        raise ValueError("Unknown observation kind")
    if not isinstance(raw, bytes) or len(raw) > MAX_BYTES:
        raise ValueError("Observation exceeds the 4 MiB byte bound")
    digest = hashlib.sha256(raw).hexdigest()
    if expected_sha256 is not None:
        if not isinstance(expected_sha256, str) or not re.fullmatch(r"[a-f0-9]{64}", expected_sha256) or expected_sha256 != digest:
            raise ValueError("Original SHA-256 pin does not match")
    compiler_report = {}
    def validate_compilation(value):
        failure = next(validator("compiler").iter_errors(value), None)
        if failure is not None:
            raise ValueError("Compiler schema violation: " + str(failure.validator))
        return check_compilation(value, _require_same)
    try:
        value = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=_pairs, parse_constant=_constant, parse_float=_float)
        error = next(validator(kind).iter_errors(value), None)
        if error is None:
            base_kind = {"compiler-change": "change", "compiler-lock": "lock", "update-plan": "plan", "update-change": "change", "sync-plan": "plan", "sync-change": "change", "hmi-plan": "plan", "hmi-change": "change", "component-plan": "plan", "component-change": "change", "preview-plan": "plan", "preview-change": "change"}.get(kind, kind.removeprefix("operation-"))
            _coherence(value, base_kind)
            if kind in OPERATION_MODES:
                journal = value["journal"] if base_kind == "change" else None
                plan = journal["preview"] if journal is not None else value
                operation = {"kind": plan["kind"], "payload": plan["payload"]}
                if journal is not None and "result" in journal:
                    operation["result"] = journal["result"]
                error = next(validator("operation").iter_errors(operation), None)
                if error is None:
                    if journal is not None and journal["state"] == "applied" and "result" not in journal:
                        raise ValueError("Applied operation observation requires its returned result")
                    check_operation(plan, operation.get("result"), "result" in operation, _require_same)
            detailed_preview = None
            if error is None and kind in ("preview-plan", "preview-change"):
                fields = ("effects", "sourceObject", "existingRecoveryCheckpoint", "componentContentPins")
                projection = {k: plan[k] for k in fields if k in plan}
                error = next(validator("preview-effects").iter_errors(projection), None)
                if error is None:
                    compiler_report.update(check_preview_effects(plan, _require_same))
                    detailed_preview = compiler_report['detailedPreviewKind']
            if error is None and (kind in ("update-plan", "update-change") or detailed_preview == "update"):
                projection = {k: plan[k] for k in ("updateImpact", "updateBaseline", "updateCleanupGuard") if k in plan}
                error = next(validator("update-analysis").iter_errors(projection), None)
                if error is None:
                    compiler_report.update(check_update(plan, _require_same))
            if error is None and (kind in ("sync-plan", "sync-change") or detailed_preview == "synchronization"):
                projection = {k: plan[k] for k in ("typeSynchronization", "effects") if k in plan}
                error = next(validator("synchronization").iter_errors(projection), None)
                if error is None:
                    compiler_report.update(check_synchronization(plan, operation.get("result"), _require_same))
            if error is None and (kind in ("hmi-plan", "hmi-change") or detailed_preview == "hmi"):
                projection = {k: plan[k] for k in ("targetPreview",) if k in plan}
                error = next(validator("hmi-preview").iter_errors(projection), None)
                if error is None:
                    compiler_report.update(check_hmi_preview(plan, operation.get("result"), _require_same))
            if error is None and (kind in ("component-plan", "component-change") or detailed_preview == "component"):
                projection = {k: plan[k] for k in ("integrationPlan",) if k in plan}
                error = next(validator("component-preview").iter_errors(projection), None)
                if error is None:
                    compiler_report.update(check_component_preview(plan, _require_same))
            if error is None:
                if kind == "compiler":
                    compiler_report = check_compilation(value, _require_same)
                elif kind in ("compiler-change", "compiler-lock"):
                    compiler_report = check_compiler_context(value, kind, validate_compilation, _require_same)
                elif kind == "preview-change":
                    compiler_report.update(check_compiler_context(value, "compiler-change", validate_compilation, _require_same))
    except (UnicodeError, RecursionError, json.JSONDecodeError) as failure:
        raise ValueError("Invalid UTF-8/JSON observation") from failure
    if error is not None:
        location = "/" + "/".join(str(x).replace("~", "~0").replace("/", "~1") for x in error.absolute_path)
        raise ValueError("Schema violation at " + location + " (" + str(error.validator) + ")")
    result = {"valid": True, "kind": kind, "bytes": len(raw), "sha256": digest,
            "referencedFilesRead": False, "nativeExecuted": False,
            "previewHashRecomputed": False,
            "qualificationGranted": False}
    if kind in OPERATION_MODES:
        result.update(operationPayloadValidated=True, operationResultValidated="result" in operation)
    result.update(compiler_report)
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kind", choices=KINDS, required=True)
    parser.add_argument("--path", type=Path, required=True)
    parser.add_argument("--expected-sha256")
    args = parser.parse_args(argv)
    try:
        with args.path.open("rb") as source:
            raw = source.read(MAX_BYTES + 1)
        result = validate_bytes(raw, args.kind, args.expected_sha256)
    except (OSError, ValueError) as failure:
        print(json.dumps({"valid": False, "kind": args.kind, "error": str(failure)}))
        return 1
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
