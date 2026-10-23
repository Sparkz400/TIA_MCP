"""Exercise the actual MCP host/worker with a core-only configured API directory.

Only installed Base, Step7 and Safety originals are copied to a new ignored local
artifacts directory. No HMI binaries are copied, no TIA process/project is started
or attached, and no project or deployment operation is exposed by this runner.
This tests configured-module absence on a machine that still has HMI installed;
it is not a claim that WinCC was uninstalled or that HMI licenses were tested.
"""
from __future__ import annotations

import argparse
import base64
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT

CORE_ASSEMBLIES = ("Siemens.Engineering.Base.dll", "Siemens.Engineering.Step7.dll", "Siemens.Engineering.Safety.dll")
HMI_ASSEMBLIES = ("Siemens.Engineering.WinCC.dll", "Siemens.Engineering.WinCC.Extension.dll", "Siemens.Engineering.WinCCUnified.dll")


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def verify_detached_response(value):
    require(value.get("attached") is False and value.get("ownsPortalProcess") is False
            and value.get("processId") is None and value.get("projects") == [],
            "The read-only startup probe must remain detached with no owned TIA process or open project.")


def record_host_exit(report, exit_code):
    report["hostExitCode"] = exit_code
    if exit_code != 0:
        report["statusBeforeShutdownFailure"] = report.get("status")
        report["status"] = "failed"
        report["shutdownDiagnostic"] = "Host shutdown did not finish with exit code zero; successful earlier calls cannot qualify the complete probe."


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def powershell_json(code):
    shell = shutil.which("pwsh.exe") or shutil.which("powershell.exe")
    require(shell is not None, "PowerShell metadata reader is unavailable.")
    result = subprocess.run([shell, "-NoProfile", "-NonInteractive", "-Command", code],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
    require(result.returncode == 0, "Metadata/process evidence command failed: " + result.stderr)
    return json.loads(result.stdout.lstrip("\ufeff"))


def metadata(path):
    quoted = ps_literal(path)
    return powershell_json("$ErrorActionPreference='Stop'; $a=[Reflection.AssemblyName]::GetAssemblyName(" + quoted +
        "); $s=Get-AuthenticodeSignature -LiteralPath " + quoted +
        "; [pscustomobject]@{assemblyName=$a.FullName;version=$a.Version.ToString();majorVersion=$a.Version.Major;signatureStatus=$s.Status.ToString();" +
        "signer=$s.SignerCertificate.Subject} | ConvertTo-Json -Compress")


def prepare_core_api(source, directory):
    source = Path(source).resolve()
    directory = Path(directory).resolve()
    require(directory.is_relative_to((ROOT / "artifacts").resolve()), "Local API test copies must stay inside ignored workspace artifacts.")
    require(not directory.exists(), "API test directory must be new; never reuse unknown binary contents.")
    directory.mkdir(parents=True)
    records = []
    for name in CORE_ASSEMBLIES:
        original = source / name
        require(original.is_file(), "Installed core assembly missing: " + str(original))
        evidence = metadata(original)
        require(evidence["majorVersion"] == 21, "Installed core API is not V21: " + name)
        require(evidence["signatureStatus"] == "Valid", "Installed source signature is not verified: " + name)
        destination = directory / name
        shutil.copyfile(original, destination)
        digest = sha256(original)
        require(sha256(destination) == digest, "Local core API copy does not match installed original.")
        records.append({"source": str(original), "localTestCopy": str(destination), "sha256": digest, **evidence})
    require(set(p.name for p in directory.iterdir()) == set(CORE_ASSEMBLIES), "Unexpected file in core-only API directory.")
    return records


def verify_absence_response(value):
    modules = value.get("modules", [])
    require({row.get("family") for row in modules} == {"classic", "unified"}, "Both HMI families require explicit diagnostics.")
    for module in modules:
        require(module.get("installed") is False and module.get("apiAvailability") == "unavailable",
                "Absent HMI module must be unavailable, not installed/qualified.")
        require(module.get("nativeQualification") == "unverified" and module.get("license") == "notProbed",
                "Module absence cannot qualify native operations or infer license availability.")
    require(len(value.get("profiles", [])) == 6, "Do not reduce absent-module coverage to one HMI family/profile.")


def verify_wrong_version_response(value, observed_major):
    modules = value.get("modules", [])
    require({row.get("family") for row in modules} == {"classic", "unified"}, "Both families must reject mismatched module versions independently.")
    for module in modules:
        diagnostic = module.get("diagnostic", {})
        require(module.get("installed") is True and module.get("apiAvailability") == "unavailable", "Present wrong-version module must remain unavailable.")
        require(diagnostic.get("code") == "HmiVersionMismatch" and diagnostic.get("requiredMajorVersion") == 21
                and diagnostic.get("observedMajorVersion") == observed_major, "Actionable exact version mismatch diagnostic required.")
        require(module.get("nativeQualification") == "unverified" and module.get("license") == "notProbed", "A metadata fixture never supplies native or licensing qualification.")


def verify_wrong_identity_response(value, expected_token):
    modules = value.get("modules", [])
    require({row.get("family") for row in modules} == {"classic", "unified"}, "Both families must reject wrong native assembly identity.")
    for module in modules:
        diagnostic = module.get("diagnostic", {})
        require(module.get("installed") is True and module.get("apiAvailability") == "unavailable", "A v21 wrong-identity module must remain unavailable.")
        expected_name = "Siemens.Engineering.WinCC" if module["family"] == "classic" else "Siemens.Engineering.WinCCUnified"
        require(diagnostic.get("code") == "HmiAssemblyIdentityMismatch" and diagnostic.get("observedMajorVersion") == 21,
                "Wrong v21 identity must have a distinct identity diagnostic, not a version mismatch.")
        require(diagnostic.get("requiredAssemblyName") == expected_name
                and diagnostic.get("observedAssemblyName") == "TiaMcp.OptionalStartup.WrongVersionFixture"
                and diagnostic.get("requiredPublicKeyToken") == expected_token
                and diagnostic.get("observedPublicKeyToken") == "", "Expected and observed assembly/key identities must be explicit.")


def core_location_control(worker, output, assemblies, observer=None):
    """Preserve Siemens' installation-relative lookup without changing worker IL.

    .NET Framework codeBase hints bind ONLY the three core assemblies at their
    installed originals. Optional HMI resolution still uses the filtered api-dir.
    This is a controlled configuration fixture, not the shipped worker config.
    """
    directory = output / "worker-core-location-control"
    directory.mkdir()
    for name in (worker.name, worker.name + ".config", "Newtonsoft.Json.dll"):
        shutil.copyfile(worker.parent / name, directory / name)
    config = directory / (worker.name + ".config")
    doc = ET.parse(config)
    runtime = doc.getroot().find("runtime")
    if runtime is None: runtime = ET.SubElement(doc.getroot(), "runtime")
    ns = "urn:schemas-microsoft-com:asm.v1"
    bindings = ET.SubElement(runtime, "{" + ns + "}assemblyBinding")
    for record in assemblies:
        identity = record["assemblyName"].split(", ")
        values = dict(part.split("=", 1) for part in identity[1:])
        dependency = ET.SubElement(bindings, "{" + ns + "}dependentAssembly")
        ET.SubElement(dependency, "{" + ns + "}assemblyIdentity", {
            "name": identity[0], "publicKeyToken": values["PublicKeyToken"], "culture": values["Culture"]})
        ET.SubElement(dependency, "{" + ns + "}codeBase", {"version": record["version"], "href": Path(record["source"]).as_uri()})
    if observer is not None:
        require(observer.name == "TiaMcp.OptionalStartup.Observer.dll" and observer.is_file(), "Explicit test-only managed observer DLL required.")
        shutil.copyfile(observer, directory / observer.name)
        ET.SubElement(runtime, "appDomainManagerAssembly", {"value": "TiaMcp.OptionalStartup.Observer, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"})
        ET.SubElement(runtime, "appDomainManagerType", {"value": "TiaMcp.OptionalStartup.Observer"})
    doc.write(config, encoding="utf-8", xml_declaration=True)
    copied = directory / worker.name
    require(sha256(copied) == sha256(worker), "Controlled worker must retain exact original binary hash.")
    return copied, {"configurationPath": str(config), "configurationSha256": sha256(config),
                    "workerBinaryUnchanged": True, "coreAssemblyOrigins": "installed signed originals via .NET Framework codeBase",
                    "optionalHmiOrigins": "filtered configured api-dir; no HMI codeBase entries"}


def process_evidence(host_pid):
    # This is supplementary independent OS evidence. Failure does not erase the
    # actual host/worker protocol results or pretend module enumeration succeeded.
    return powershell_json("$ErrorActionPreference='Stop'; $items=@(Get-CimInstance Win32_Process -Filter " +
        ps_literal("ParentProcessId=" + str(host_pid)) +
        "); $r=@($items | ForEach-Object { $p=Get-Process -Id $_.ProcessId; [pscustomobject]@{processId=$_.ProcessId;" +
        "parentProcessId=$_.ParentProcessId;executablePath=$_.ExecutablePath;modules=@($p.Modules | Where-Object {$_.ModuleName -like 'Siemens.Engineering*'} | " +
        "ForEach-Object {[pscustomobject]@{name=$_.ModuleName;path=$_.FileName}})}}); ConvertTo-Json -Depth 5 -InputObject $r -Compress")


def run(args):
    require(os.name == "nt", "Actual V21 optional startup qualification requires Windows.")
    output = args.output.resolve()
    require(output.is_relative_to((ROOT / "artifacts").resolve()), "Output must be inside ignored local artifacts.")
    require(not output.exists(), "Output must be a new evidence directory.")
    output.mkdir(parents=True)
    report = {"schemaVersion": 1, "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "status": "unverified", "nativeWorkerExecuted": False, "projectMutations": False,
              "testMode": "wrongIdentityMetadataControl" if args.wrong_identity_fixture else "wrongMajorMetadataControl" if args.wrong_version_fixture else "optionalModuleAbsence",
              "configuredApiAbsenceOnly": args.wrong_version_fixture is None and args.wrong_identity_fixture is None, "physicalUninstallationTested": False,
              "scope": "Actual stdio MCP host and named-pipe V21 worker; bridge_info, discovery and both-family module diagnostics",
              "steps": [], "runtimeVerified": False,
              "wrongVersion": {"status": "unverified", "reason": "No installed incompatible Siemens HMI module is substituted or fabricated by this test."},
              "cleanup": "Local signed API copies retained only inside ignored artifacts; never include this directory in release bundles."}
    client = None
    try:
        for label, path in (("host", args.host), ("worker", args.worker)):
            require(path.resolve().is_file(), "Explicit " + label + " executable required.")
            report[label] = {"path": str(path.resolve()), "sha256": sha256(path)}
        api = output / "api-core-only"
        report["coreAssemblies"] = prepare_core_api(args.api_source, api)
        fixture_major = None
        fixture_record = None
        if args.wrong_version_fixture is not None or args.wrong_identity_fixture is not None:
            fixture = (args.wrong_version_fixture or args.wrong_identity_fixture).resolve()
            fixture_metadata = metadata(fixture)
            require(fixture_metadata["assemblyName"].startswith("TiaMcp.OptionalStartup.WrongVersionFixture,"), "Wrong-version fixture must have an explicit non-Siemens assembly identity.")
            fixture_major = fixture_metadata["majorVersion"]
            require(fixture_major == 21 if args.wrong_identity_fixture else fixture_major != 21,
                    "Fixture major version does not match the intended version/identity diagnostic.")
            for name in (HMI_ASSEMBLIES[0], HMI_ASSEMBLIES[2]): shutil.copyfile(fixture, api / name)
            fixture_record = {"status": "unverified", "syntheticMetadataOnly": True, "nativeWrongVersionInstallationTested": False,
                "fixturePath": str(fixture), "fixtureSha256": sha256(fixture), **fixture_metadata,
                "scope": "Clearly non-Siemens unsigned fixture metadata under test-only candidate filenames; no vendor identity/signature is fabricated and fixture code must not load."}
            report["wrongIdentity" if args.wrong_identity_fixture else "wrongVersion"] = fixture_record
        report["configuredApiDirectory"] = str(api)
        report["optionalAssemblyEntries"] = [{"name": name, "presentInTestApi": (api / name).exists()} for name in HMI_ASSEMBLIES]
        report["workerDirectoryContainsSiemensDlls"] = [str(p) for p in args.worker.parent.glob("Siemens*.dll")]
        require(not report["workerDirectoryContainsSiemensDlls"], "Worker output must not bundle installed Siemens binaries.")
        gac = []
        windows = Path(os.environ.get("WINDIR", "C:/Windows"))
        for base in (windows / "Microsoft.NET/assembly", windows / "assembly"):
            for area in ("GAC_MSIL", "GAC_64", "GAC_32"):
                for name in HMI_ASSEMBLIES:
                    location = base / area / Path(name).stem
                    if location.exists(): gac.append(str(location))
        report["hmiAssemblyCacheLocationsObserved"] = gac
        actual_worker = args.worker.resolve()
        if args.bind_core_at_installed_location:
            actual_worker, control = core_location_control(actual_worker, output, report["coreAssemblies"], args.observer)
            report["coreLocationControl"] = control
        elif args.observer is not None:
            raise AssertionError("A test observer requires the isolated core-location configuration; never edit the original worker config.")
        report["executedWorkerPath"] = str(actual_worker)
        extra = ["--worker", str(actual_worker), "--api-dir", str(api), "--artifacts", str(output / "bridge-artifacts")]
        report["hostArguments"] = extra
        assembly_log = output / "managed-assemblies.tsv"
        old_observer_variable = os.environ.get("TIA_MCP_OPTIONAL_ASSEMBLY_LOG")
        try:
            if args.observer is not None: os.environ["TIA_MCP_OPTIONAL_ASSEMBLY_LOG"] = str(assembly_log)
            client = Client(args.host.resolve(), args.timeout, extra)
        finally:
            if old_observer_variable is None: os.environ.pop("TIA_MCP_OPTIONAL_ASSEMBLY_LOG", None)
            else: os.environ["TIA_MCP_OPTIONAL_ASSEMBLY_LOG"] = old_observer_variable
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "hmi-optional-startup-qualification", "version": "1"}})
        client.notify("notifications/initialized")
        for tool, arguments in (("tia_capabilities", {}), ("tia_discover", {}), ("hmi_capabilities", {"request": {}}), ("tia_session_status", {})):
            try:
                response = client.tool(tool, arguments)
            except Exception as error:
                blocked = "OpennessLocationProvider" in str(error) or "TimeoutOutcomeUnknown" in str(error)
                report["steps"].append({"tool": tool, "status": "blocked" if blocked else "failed", "reason": str(error)})
                print(("BLOCKED " if blocked else "FAIL ") + tool, flush=True)
                continue
            report["steps"].append({"tool": tool, "result": response, "status": "passed"})
            report["nativeWorkerExecuted"] = True
            if tool == "tia_capabilities":
                require(Path(response["apiDirectory"]).resolve() == api, "Worker did not use the intentionally filtered API directory.")
                require(response.get("tiaVersion") == 21, "Native V21 bridge_info response required.")
            elif tool == "tia_discover":
                require(isinstance(response.get("processes"), list), "Native process discovery did not return a bounded process array.")
            elif tool == "hmi_capabilities":
                if fixture_major is None: verify_absence_response(response)
                elif args.wrong_identity_fixture:
                    core = report["coreAssemblies"][0]["assemblyName"]
                    expected_token = dict(part.split("=", 1) for part in core.split(", ")[1:])["PublicKeyToken"]
                    verify_wrong_identity_response(response, expected_token)
                    fixture_record["status"] = "passed-metadata-diagnostic-only"
                else:
                    verify_wrong_version_response(response, fixture_major)
                    fixture_record["status"] = "passed-metadata-diagnostic-only"
            elif tool == "tia_session_status":
                verify_detached_response(response)
            print("PASS " + tool, flush=True)
        try:
            observed = process_evidence(client.process.pid)
            report["nativeProcessEvidence"] = observed
            children = [entry for entry in observed if Path(entry.get("executablePath") or "").resolve() == actual_worker]
            require(len(children) == 1, "Expected one exact worker child process.")
            loaded = children[0]["modules"]
            require(not any(entry["name"] in HMI_ASSEMBLIES for entry in loaded), "HMI module was unexpectedly loaded during PLC-only startup.")
            report["loadedModuleObservation"] = "unverified" if not loaded else "observed"
            report["loadedModuleObservationScope"] = "Win32 Process.Modules is not a complete CLR managed-assembly inventory; empty output proves no managed-load absence."
        except Exception as error:
            report["loadedModuleObservation"] = "blocked"
            report["loadedModuleObservationReason"] = str(error)
        if args.observer is not None:
            require(assembly_log.is_file(), "Managed assembly observer produced no evidence.")
            loaded = []
            for line in assembly_log.read_text(encoding="utf-8-sig").splitlines():
                name, location = line.split("\t")
                loaded.append({"assembly": base64.b64decode(name).decode("utf-8"), "location": base64.b64decode(location).decode("utf-8")})
            require(any(row["assembly"].startswith("Siemens.Engineering.Base,") for row in loaded), "Observer did not see the required actual native Base API load.")
            require(not any(row["assembly"].startswith(("Siemens.Engineering.WinCC,", "Siemens.Engineering.WinCC.Extension,", "Siemens.Engineering.WinCCUnified,")) for row in loaded),
                    "A managed HMI assembly unexpectedly loaded during core-only startup.")
            require(not any(row["assembly"].startswith("TiaMcp.OptionalStartup.WrongVersionFixture,") for row in loaded), "The wrong-version metadata fixture must never load as executable code.")
            report["managedAssemblyObservation"] = {"status": "passed", "observerSha256": sha256(args.observer), "logSha256": sha256(assembly_log), "assemblies": loaded,
                "scope": "Test-only AppDomainManager observes initial and subsequent managed assembly loads without changing resolution or worker IL."}
        statuses = [step["status"] for step in report["steps"]]
        report["status"] = "failed" if "failed" in statuses else "blocked" if "blocked" in statuses else "passed"
    except Exception as error:
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
        report["status"] = "blocked" if not report["nativeWorkerExecuted"] else "failed"
    finally:
        if client is not None:
            try:
                client.close()
            except Exception as error:
                report["shutdownFailure"] = {"type": type(error).__name__, "message": str(error)}
                report["status"] = "failed"
            record_host_exit(report, client.process.returncode)
            report["hostLogs"] = client.errors
            (output / "host.log").write_text("\n".join(client.errors), encoding="utf-8")
        report["completedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Report: " + str(output / "report.json"), flush=True)
    return 0 if report["status"] == "passed" else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--api-source", type=Path, default=Path("C:/Program Files/Siemens/Automation/Portal V21/PublicAPI/V21/net48"))
    parser.add_argument("--timeout", type=int, default=60)
    parser.add_argument("--bind-core-at-installed-location", action="store_true", help="Run unchanged copied worker IL with a test-only config binding core APIs at their installed location; optional HMI lookup remains filtered.")
    parser.add_argument("--observer", type=Path, help="Optional explicit test-only CLR assembly-load observer DLL, copied only into the isolated configuration fixture.")
    mismatch = parser.add_mutually_exclusive_group()
    mismatch.add_argument("--wrong-version-fixture", type=Path, help="Explicit unsigned non-Siemens metadata fixture for the production major-version diagnostic; this is not a native incompatible-installation test.")
    mismatch.add_argument("--wrong-identity-fixture", type=Path, help="Explicit unsigned non-Siemens v21 metadata fixture for the production assembly-identity diagnostic; no fixture code is loaded.")
    return run(parser.parse_args())


if __name__ == "__main__":
    sys.exit(main())
