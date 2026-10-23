"""Immutable conformance specifications. Expected vectors are authored independently.

These specifications are input, not native TIA qualification evidence. Generated
sources are compiled by the live runner; behavioral vectors require execution.
"""


def fc(name, inputs, outputs, body, language="SCL", temp=""):
    optimized = "{ S7_Optimized_Access := 'TRUE' }\n"
    if language == "STL":
        body = "NETWORK\nTITLE = Conformance logic\n" + body
    temporary = "VAR_TEMP\n" + temp + "\nEND_VAR\n" if temp else ""
    return (f'FUNCTION "{name}" : Void\n{optimized}VERSION : 0.1\n'
            f"VAR_INPUT\n{inputs}\nEND_VAR\nVAR_OUTPUT\n{outputs}\nEND_VAR\n"
            f"{temporary}BEGIN\n{body}\nEND_FUNCTION\n")


def fb(name, inputs, outputs, statics, body):
    return (f'FUNCTION_BLOCK "{name}"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\n'
            f"VAR_INPUT\n{inputs}\nEND_VAR\nVAR_OUTPUT\n{outputs}\nEND_VAR\n"
            f"VAR\n{statics}\nEND_VAR\nBEGIN\n{body}\nEND_FUNCTION_BLOCK\n")


def specifications(prefix="Tcf"):
    def name(suffix):
        return prefix + "_" + suffix

    def source(identifier, title, names, text, behavior, instructions, language="SCL", **extra):
        return dict(fixtureId=identifier, title=title, language=language, names=names,
                    source=text, expectedBehavior=behavior, instructions=instructions, **extra)

    fixtures = []
    block = name("Boolean")
    fixtures.append(source("scl-boolean", "Boolean truth table and negation", [block],
        fc(block, "A : Bool;\nB : Bool;\nInhibit : Bool;", "Active : Bool;",
           "#Active := (#A OR #B) AND NOT #Inhibit;"),
        {"kind": "vectors", "vectors": [
            {"inputs": {"A": False, "B": False, "Inhibit": False}, "outputs": {"Active": False}},
            {"inputs": {"A": False, "B": True, "Inhibit": False}, "outputs": {"Active": True}},
            {"inputs": {"A": True, "B": False, "Inhibit": False}, "outputs": {"Active": True}},
            {"inputs": {"A": True, "B": True, "Inhibit": False}, "outputs": {"Active": True}},
            {"inputs": {"A": False, "B": False, "Inhibit": True}, "outputs": {"Active": False}},
            {"inputs": {"A": False, "B": True, "Inhibit": True}, "outputs": {"Active": False}},
            {"inputs": {"A": True, "B": False, "Inhibit": True}, "outputs": {"Active": False}},
            {"inputs": {"A": True, "B": True, "Inhibit": True}, "outputs": {"Active": False}}]},
        ["OR", "AND", "NOT"]))

    block = name("Branch")
    fixtures.append(source("scl-branch", "IF/ELSIF boundaries and CASE selection", [block],
        fc(block, "Value : Int;\nMode : USInt;", "Band : Int;\nSelected : Int;",
           "IF #Value < -10 THEN #Band := -1;\nELSIF #Value > 10 THEN #Band := 1;\nELSE #Band := 0; END_IF;\n"
           "CASE #Mode OF\n0: #Selected := 0;\n1: #Selected := #Value;\nELSE #Selected := -999;\nEND_CASE;"),
        {"kind": "vectors", "vectors": [
            {"inputs": {"Value": -11, "Mode": 0}, "outputs": {"Band": -1, "Selected": 0}},
            {"inputs": {"Value": -10, "Mode": 1}, "outputs": {"Band": 0, "Selected": -10}},
            {"inputs": {"Value": 10, "Mode": 1}, "outputs": {"Band": 0, "Selected": 10}},
            {"inputs": {"Value": 11, "Mode": 255}, "outputs": {"Band": 1, "Selected": -999}}]},
        ["IF", "ELSIF", "CASE", "compare"]))

    block = name("Arithmetic")
    fixtures.append(source("scl-arithmetic", "Signed arithmetic and exact widening conversion", [block],
        fc(block, "Left : Int;\nRight : Int;", "Sum : DInt;\nDifference : DInt;\nScaled : Real;",
           "#Sum := INT_TO_DINT(#Left) + INT_TO_DINT(#Right);\n"
           "#Difference := INT_TO_DINT(#Left) - INT_TO_DINT(#Right);\n#Scaled := DINT_TO_REAL(#Sum) * 0.5;"),
        {"kind": "vectors", "vectors": [
            {"inputs": {"Left": 32767, "Right": 32767}, "outputs": {"Sum": 65534, "Difference": 0, "Scaled": 32767.0}},
            {"inputs": {"Left": -32768, "Right": 32767}, "outputs": {"Sum": -1, "Difference": -65535, "Scaled": -0.5}},
            {"inputs": {"Left": -4, "Right": -6}, "outputs": {"Sum": -10, "Difference": 2, "Scaled": -5.0}}]},
        ["add", "subtract", "INT_TO_DINT", "DINT_TO_REAL"]))

    data_type, block = name("SampleType"), name("ArrayData")
    type_source = (f'TYPE "{data_type}"\nVERSION : 0.1\nSTRUCT\n'
                   'Enabled : Bool;\nSamples : Array[-2..2] of Int;\n'
                   'Meta : Struct\nCountValue : DInt;\nLabelText : String[32];\nEND_STRUCT;\nEND_STRUCT;\nEND_TYPE\n')
    data_source = (f'DATA_BLOCK "{block}"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\n'
                   f'VAR\nItem : "{data_type}";\nEND_VAR\nBEGIN\nItem.Enabled := TRUE;\n'
                   "Item.Samples[-2] := -17;\nItem.Samples[2] := 23;\nItem.Meta.CountValue := 5;\nEND_DATA_BLOCK\n")
    fixtures.append(source("scl-udt-array", "Nested UDT, signed array bounds and DB start values", [block], data_source,
        {"kind": "initialValues", "values": {"Item.Enabled": True, "Item.Samples[-2]": -17,
                                              "Item.Samples[2]": 23, "Item.Meta.CountValue": 5}},
        ["UDT", "array", "nested-struct", "start-value"], typeNames=[data_type], typeSource=type_source,
        interfaceTokens=["Samples", "Array[-2..2]", "CountValue"]))

    block = name("UnicodeData")
    fixtures.append(source("scl-unicode-data", "Unicode comments and nested text declarations", [block],
        f'DATA_BLOCK "{block}"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\nVAR\n'
        "// Prüfung – 温度 – Δοκιμή: preserve UTF-8 source bytes.\n"
        "Info : Struct\nCaption : WString[32];\nUnitText : String[8];\nEND_STRUCT;\n"
        "Values : Array[0..3] of Real;\nEND_VAR\nBEGIN\nInfo.UnitText := 'degC';\nValues[0] := -12.5;\nEND_DATA_BLOCK\n",
        {"kind": "initialValues", "values": {"Info.UnitText": "degC", "Values[0]": -12.5},
         "sourceText": "Prüfung – 温度 – Δοκιμή", "nativeUnicodeCommentPreservation": "requires XML observation; source hash alone is insufficient"},
        ["WString", "String", "Unicode-source", "array"], interfaceTokens=["Caption", "WString", "UnitText"]))

    child, block = name("Child"), name("Caller")
    fixtures.append(source("scl-call", "FC call with explicit input/output binding", [child, block],
        fc(child, "Value : Int;", "ResultValue : Int;", "#ResultValue := #Value + 3;") +
        fc(block, "Value : Int;", "ResultValue : Int;", f'"{child}"(Value := #Value, ResultValue => #ResultValue);'),
        {"kind": "vectors", "vectors": [{"inputs": {"Value": -3}, "outputs": {"ResultValue": 0}},
                                              {"inputs": {"Value": 10}, "outputs": {"ResultValue": 13}}]},
        ["FC-call"], callEdge={"caller": block, "callee": child}))

    child, block = name("MemoryCell"), name("MultiInstance")
    fixtures.append(source("scl-multi-instance", "Two FB multi-instances with independent retained state", [child, block],
        fb(child, "Enable : Bool;\nResetValue : Bool;", "CountValue : DInt;", "ReservedValue : Bool;",
           "IF #ResetValue THEN #CountValue := 0;\nELSIF #Enable THEN #CountValue := #CountValue + 1; END_IF;") +
        fb(block, "EnableA : Bool;\nEnableB : Bool;\nResetValue : Bool;", "CountA : DInt;\nCountB : DInt;",
           f'First : "{child}";\nSecond : "{child}";',
           "#First(Enable := #EnableA, ResetValue := #ResetValue, CountValue => #CountA);\n"
           "#Second(Enable := #EnableB, ResetValue := #ResetValue, CountValue => #CountB);"),
        {"kind": "scanSequence", "reset": "Call once with ResetValue=true before the sequence.", "vectors": [
            {"inputs": {"EnableA": True, "EnableB": False, "ResetValue": False}, "outputs": {"CountA": 1, "CountB": 0}},
            {"inputs": {"EnableA": True, "EnableB": True, "ResetValue": False}, "outputs": {"CountA": 2, "CountB": 1}},
            {"inputs": {"EnableA": False, "EnableB": True, "ResetValue": False}, "outputs": {"CountA": 2, "CountB": 2}}]},
        ["FB-call", "multi-instance", "independent-state"], callEdge={"caller": block, "callee": child}))

    block = name("EdgeLatch")
    fixtures.append(source("scl-edge-latch", "Rising edge and reset-dominant latch", [block],
        fb(block, "ClockValue : Bool;\nSetValue : Bool;\nResetValue : Bool;", "Pulse : Bool;\nLatched : Bool;",
           "PreviousClock : Bool;",
           "#Pulse := #ClockValue AND NOT #PreviousClock;\n#PreviousClock := #ClockValue;\n"
           "IF #ResetValue THEN #Latched := FALSE;\nELSIF #SetValue THEN #Latched := TRUE; END_IF;"),
        {"kind": "scanSequence", "reset": "Fresh instance with all Bool values false.", "vectors": [
            {"inputs": {"ClockValue": False, "SetValue": False, "ResetValue": False}, "outputs": {"Pulse": False, "Latched": False}},
            {"inputs": {"ClockValue": True, "SetValue": True, "ResetValue": False}, "outputs": {"Pulse": True, "Latched": True}},
            {"inputs": {"ClockValue": True, "SetValue": False, "ResetValue": False}, "outputs": {"Pulse": False, "Latched": True}},
            {"inputs": {"ClockValue": False, "SetValue": True, "ResetValue": True}, "outputs": {"Pulse": False, "Latched": False}}]},
        ["rising-edge", "reset-dominant-latch"]))

    block = name("OnDelay")
    fixtures.append(source("scl-iec-ton", "Native IEC on-delay multi-instance", [block],
        fb(block, "Enable : Bool;\nPreset : Time;", "Done : Bool;\nElapsed : Time;", "DelayTimer : TON_TIME;",
           "#DelayTimer(IN := #Enable, PT := #Preset, Q => #Done, ET => #Elapsed);"),
        {"kind": "timedSequence", "reset": "Call with Enable=false before timing.", "clock": "PLC monotonic time; allow one scan and sampling jitter",
         "vectors": [{"atMs": 0, "inputs": {"Enable": True, "Preset": "T#200ms"}, "outputs": {"Done": False}},
                     {"atMs": 50, "inputs": {"Enable": True}, "outputs": {"Done": False}},
                     {"atMs": 350, "inputs": {"Enable": True}, "outputs": {"Done": True}},
                     {"atMs": 400, "inputs": {"Enable": False}, "outputs": {"Done": False, "Elapsed": "T#0ms"}}]},
        ["TON_TIME", "IEC-timer"], requires=["native.TON_TIME"]))

    block = name("UpCounter")
    fixtures.append(source("scl-iec-ctu", "Native IEC up-counter edges and reset", [block],
        fb(block, "ClockValue : Bool;\nResetValue : Bool;", "Reached : Bool;\nCountValue : Int;", "EdgeCounter : CTU_INT;",
           "#EdgeCounter(CU := #ClockValue, R := #ResetValue, PV := 2, Q => #Reached, CV => #CountValue);"),
        {"kind": "scanSequence", "reset": "Call with ClockValue=false and ResetValue=true.", "vectors": [
            {"inputs": {"ClockValue": True, "ResetValue": False}, "outputs": {"Reached": False, "CountValue": 1}},
            {"inputs": {"ClockValue": True, "ResetValue": False}, "outputs": {"Reached": False, "CountValue": 1}},
            {"inputs": {"ClockValue": False, "ResetValue": False}, "outputs": {"Reached": False, "CountValue": 1}},
            {"inputs": {"ClockValue": True, "ResetValue": False}, "outputs": {"Reached": True, "CountValue": 2}},
            {"inputs": {"ClockValue": False, "ResetValue": True}, "outputs": {"Reached": False, "CountValue": 0}}]},
        ["CTU_INT", "IEC-counter"], requires=["native.CTU_INT"]))

    block = name("StlBoolean")
    fixtures.append(source("stl-boolean", "AWL/STL Boolean and negated input", [block],
        fc(block, "Enable : Bool;\nInhibit : Bool;", "Active : Bool;", "A #Enable;\nAN #Inhibit;\n= #Active;", "STL"),
        {"kind": "vectors", "vectors": [
            {"inputs": {"Enable": False, "Inhibit": False}, "outputs": {"Active": False}},
            {"inputs": {"Enable": True, "Inhibit": False}, "outputs": {"Active": True}},
            {"inputs": {"Enable": True, "Inhibit": True}, "outputs": {"Active": False}}]},
        ["A", "AN", "assign"], language="STL"))

    block = name("StlMath")
    fixtures.append(source("stl-arithmetic-compare", "AWL/STL integer addition and comparison", [block],
        fc(block, "Left : Int;\nRight : Int;", "Sum : Int;\nGreater : Bool;",
           "L #Left;\nL #Right;\n+I;\nT #Sum;\nL #Left;\nL #Right;\n>I;\n= #Greater;", "STL"),
        {"kind": "vectors", "domain": "Sum remains in INT range; overflow intentionally excluded.", "vectors": [
            {"inputs": {"Left": -7, "Right": 3}, "outputs": {"Sum": -4, "Greater": False}},
            {"inputs": {"Left": 10, "Right": -2}, "outputs": {"Sum": 8, "Greater": True}},
            {"inputs": {"Left": 4, "Right": 4}, "outputs": {"Sum": 8, "Greater": False}}]},
        ["+I", ">I"], language="STL"))

    for language in ("LAD", "FBD"):
        fixtures.append(dict(fixtureId=language.lower() + "-operand", title=language + " baseline and preserved-network operand substitution",
            language=language, names=[name(language + "Operand")], graphical=True,
            instructions=["symbolic-input", "assignment", "existing-operand-substitution"],
            expectedBehavior={"kind": "vectors", "phase": "after operand substitution", "vectors": [
                {"inputs": {"Enable": False, "EnableAlternate": True}, "outputs": {"Active": True}},
                {"inputs": {"Enable": True, "EnableAlternate": False}, "outputs": {"Active": False}}]}))
    return fixtures
