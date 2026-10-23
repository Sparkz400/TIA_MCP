"""Independent numeric call fixture source, without the typed emitter."""
from pathlib import Path
from author_reference_sources import source

ROOT = Path(__file__).resolve().parent


def main():
    for language in ("LAD", "FBD"):
        start = "RUNG wire#powerrail" if language == "LAD" else "RUNG TRUE"
        declarations = "VAR_INPUT\n Value : Int;\nEND_VAR\nVAR_IN_OUT\n Accumulator : Int;\nEND_VAR\nVAR_OUTPUT\n Result : Int;\n Returned : Int;\nEND_VAR"
        call = '"Tgp_NumericFC"( Value := #Value, Accumulator := #Accumulator, Result => #Result, Ret_Val => #Returned )'
        (ROOT / "sources" / ("numeric-fc-" + language.lower() + ".s7dcl")).write_text(
            source("Tgp_NumericFC_" + language, language, declarations, start + "\n " + call + "\nEND_RUNG"), encoding="utf-8")
        declarations = "VAR_INPUT\n Enable : Bool;\n Delta : Int;\n Reset : Bool;\nEND_VAR\nVAR_IN_OUT\n Accumulator : Int;\nEND_VAR\nVAR_OUTPUT\n Total : Int;\nEND_VAR\nVAR\n Child : \"Tgp_NumericFB\";\nEND_VAR"
        call = '#Child( Enable := #Enable, Delta := #Delta, Reset := #Reset, Accumulator := #Accumulator, Total => #Total )'
        (ROOT / "sources" / ("numeric-fb-" + language.lower() + ".s7dcl")).write_text(
            source("Tgp_NumericFB_" + language, language, declarations, start + "\n " + call + "\nEND_RUNG"), encoding="utf-8")


if __name__ == "__main__":
    main()
