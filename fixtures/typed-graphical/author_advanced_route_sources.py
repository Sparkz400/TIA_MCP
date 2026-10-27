"""Author equivalent independent instruction programs in two explicit routes.

The source programs are the reference corpus, not emitted typed XML. Each route
is imported by TIA independently; no standard-route native profile is copied.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent / "sources"


def envelope(namespace, source):
    return "NAMESPACE " + namespace + "\n" + source + "\nEND_NAMESPACE\n"


def main():
    for route, namespace in (("Unit", "TiaMcp_UnitA"), ("Namespace", "TiaMcp_UnitA.Alternate")):
        prefix = "Tgp" + route + "_"
        rewrite = lambda source: source.replace("Tgp_", prefix)
        def qualify(source):
            return "\n".join(line if re.match(r"\s*(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\s", line) else
                re.sub(r'"(' + prefix + r'[^\"]+)"', lambda m: '"' + namespace + '.' + m.group(1) + '"', line)
                for line in source.splitlines()) + "\n"
        (ROOT / ("route-" + route + "-advanced-callees.scl")).write_text(
            envelope(namespace, rewrite((ROOT / "numeric-callees.scl").read_text())), encoding="utf-8")
        storage = []
        for name, data_type in (("GlobalTimer", "IEC_TIMER"), ("GlobalCounter", "IEC_COUNTER"),
                                ("NumericFB_DB", prefix + "NumericFB"),
                                ("GlobalTemplateFB_LAD", prefix + "TemplateFB_LAD"),
                                ("GlobalTemplateFB_FBD", prefix + "TemplateFB_FBD")):
            storage.append('DATA_BLOCK "' + prefix + name + '"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n"' + data_type + '"\nBEGIN\nEND_DATA_BLOCK\n')
        for language in ("LAD", "FBD"):
            storage.append(rewrite((ROOT / ("multi-template-fb-" + language.lower() + ".scl")).read_text()))
        (ROOT / ("route-" + route + "-qualified-storage.scl")).write_text(
            envelope(namespace, qualify("\n".join(storage))), encoding="utf-8")
        programs = []
        for language in ("lad", "fbd"):
            for family in ("eq", "ne", "lt", "le", "gt", "ge", "ton", "tof", "tp", "ctu", "ctd", "ctud",
                           "ton-global", "tof-global", "tp-global", "ctu-global", "ctd-global", "ctud-global",
                           "numeric-fc", "numeric-fb"):
                programs.append(rewrite((ROOT / (family + "-" + language + ".s7dcl")).read_text()))
                (ROOT / ("route-" + route + "-qualified-" + family + "-" + language + ".s7dcl")).write_text(
                    envelope(namespace, qualify(programs[-1])), encoding="utf-8")
            local = rewrite((ROOT / ("numeric-fb-" + language + ".s7dcl")).read_text())
            global_source = local.replace(prefix + "NumericFB_" + language.upper(), prefix + "NumericFB_Global_" + language.upper())
            global_source = global_source.replace('VAR\n Child : "' + prefix + 'NumericFB";\nEND_VAR\n', "")
            global_source = global_source.replace('#Child(', '"' + prefix + 'NumericFB_DB"(')
            programs.append(global_source)
            (ROOT / ("route-" + route + "-qualified-numeric-fb-global-" + language + ".s7dcl")).write_text(
                envelope(namespace, qualify(global_source)), encoding="utf-8")
        (ROOT / ("route-" + route + "-advanced-native.s7dcl")).write_text(
            envelope(namespace, "\n".join(programs)), encoding="utf-8")
        for phase in ("before", "after"):
            source = rewrite((ROOT / ("instance-effect-" + phase + ".scl")).read_text())
            (ROOT / ("route-" + route + "-instance-effect-" + phase + ".scl")).write_text(
                envelope(namespace, qualify(source)), encoding="utf-8")
    cross_scope()


def cross_scope():
    """Callers of TiaMcp_UnitA's numeric FC from its alternate namespace and from the related unit TiaMcp_UnitB.

    The callee is always named absolutely; captures of these bind the cross-namespace check and the
    crossUnitQualified call profile (route-capture-crossscope-call-packet.v1.json).
    """
    for label, namespace in (("CrossNamespace", "TiaMcp_UnitA.Alternate"), ("CrossUnit", "TiaMcp_UnitB")):
        for language in ("lad", "fbd"):
            source = (ROOT / ("numeric-fc-" + language + ".s7dcl")).read_text()
            source = source.replace('"Tgp_NumericFC_', '"Tgp' + label + '_NumericFC_').replace('"Tgp_NumericFC"', '"TiaMcp_UnitA.TgpUnit_NumericFC"')
            (ROOT / ("route-" + label + "-numeric-fc-" + language + ".s7dcl")).write_text(envelope(namespace, source), encoding="utf-8")


if __name__ == "__main__":
    main()
