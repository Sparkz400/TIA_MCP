"""Materialize hand-authored native SD probes; independent of the typed emitter.

These are reference source programs, not validation or qualification results.
Native capture records rejection as well as accepted exports for each profile.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def source(name, language, declarations, body):
    return ('{ S7_Optimized := "TRUE"; S7_PreferredLanguage := "' + language + '"; S7_Version := "0.1" }\n'
            'FUNCTION_BLOCK "' + name + '"\n' + declarations + '\n'
            '{ S7_Language := "' + language + '" }\nNETWORK\n' + body +
            '\nEND_NETWORK\nEND_FUNCTION_BLOCK\n')


def main():
    for language in ("LAD", "FBD"):
        scalar_types = ('Bool', 'SInt', 'Int', 'DInt', 'LInt', 'USInt', 'UInt', 'UDInt', 'ULInt', 'Real', 'LReal',
                        'Byte', 'Word', 'DWord', 'LWord', 'Time', 'LTime')
        for block_type in ('FC', 'FB'):
            sections = [('VAR_INPUT', 'Input'), ('VAR_OUTPUT', 'Output'), ('VAR_IN_OUT', 'InOut'), ('VAR_TEMP', 'Temp')]
            if block_type == 'FB':
                sections.append(('VAR', 'Static'))
            declarations = '\n'.join(keyword + '\n' + '\n'.join('  ' + prefix + data_type + ' : ' + data_type + ';' for data_type in scalar_types) + '\nEND_VAR'
                                     for keyword, prefix in sections)
            body = ('RUNG wire#powerrail\n Contact( #InputBool )' if language == 'LAD' else 'RUNG\n #InputBool') + '\n Coil( #OutputBool )\nEND_RUNG'
            text = source('Tgp_Template' + block_type + '_' + language, language, declarations, body)
            if block_type == 'FC':
                text = text.replace('FUNCTION_BLOCK "Tgp_TemplateFC_' + language + '"', 'FUNCTION "Tgp_TemplateFC_' + language + '" : Void').replace('END_FUNCTION_BLOCK', 'END_FUNCTION')
            (ROOT / 'sources' / ('template-' + block_type.lower() + '-' + language.lower() + '.s7dcl')).write_text(text, encoding='utf-8')
        start = 'RUNG wire#powerrail\n  Contact( #Enable )' if language == 'LAD' else 'RUNG\n  #Enable'
        for instruction in ('TON', 'TOF', 'TP'):
            declarations = ('VAR_INPUT\n  Enable : Bool;\n  Preset : Time;\nEND_VAR\n'
                            'VAR_OUTPUT\n  Done : Bool;\n  Elapsed : Time;\nEND_VAR\n'
                            'VAR\n  Storage : ' + instruction + '_TIME;\nEND_VAR')
            body = (start + '\n  { S7_Templates := "time_type := Time" }\n  #Storage.' + instruction +
                    '( pt := #Preset, et => #Elapsed )\n  Coil( #Done )\nEND_RUNG')
            (ROOT / 'sources' / (instruction.lower() + '-' + language.lower() + '.s7dcl')).write_text(
                source('Tgp_' + instruction + '_' + language, language, declarations, body), encoding='utf-8')
            global_declarations = declarations.replace('\nVAR\n  Storage : ' + instruction + '_TIME;\nEND_VAR', '')
            global_body = body.replace('#Storage.', '"Tgp_GlobalTimer".')
            (ROOT / 'sources' / (instruction.lower() + '-global-' + language.lower() + '.s7dcl')).write_text(
                source('Tgp_' + instruction + '_Global_' + language, language, global_declarations, global_body), encoding='utf-8')
        for instruction in ('CTU', 'CTD', 'CTUD'):
            declarations = ('VAR_INPUT\n  Enable : Bool;\n  Down : Bool;\n  Reset : Bool;\n  Load : Bool;\n  Preset : Int;\nEND_VAR\n'
                            'VAR_OUTPUT\n  Done : Bool;\n  Lower : Bool;\n  Count : Int;\nEND_VAR\n'
                            'VAR\n  Storage : ' + instruction + '_INT;\nEND_VAR')
            pins = {'CTU': 'r := #Reset, pv := #Preset, cv => #Count',
                    'CTD': 'ld := #Load, pv := #Preset, cv => #Count',
                    'CTUD': 'cd := #Down, r := #Reset, ld := #Load, pv := #Preset, qd => #Lower, cv => #Count'}[instruction]
            body = (start + '\n  { S7_Templates := "value_type := Int" }\n  #Storage.' + instruction +
                    '( ' + pins + ' )\n  Coil( #Done )\nEND_RUNG')
            (ROOT / 'sources' / (instruction.lower() + '-' + language.lower() + '.s7dcl')).write_text(
                source('Tgp_' + instruction + '_' + language, language, declarations, body), encoding='utf-8')
            global_declarations = declarations.replace('\nVAR\n  Storage : ' + instruction + '_INT;\nEND_VAR', '')
            global_body = body.replace('#Storage.', '"Tgp_GlobalCounter".')
            (ROOT / 'sources' / (instruction.lower() + '-global-' + language.lower() + '.s7dcl')).write_text(
                source('Tgp_' + instruction + '_Global_' + language, language, global_declarations, global_body), encoding='utf-8')
        for instruction in ('Eq', 'Ne', 'Lt', 'Le', 'Gt', 'Ge'):
            declarations = ('VAR_INPUT\n  Left : Int;\n  Right : Int;\nEND_VAR\n'
                            'VAR_OUTPUT\n  Result : Bool;\nEND_VAR')
            body = ('RUNG wire#powerrail' if language == 'LAD' else 'RUNG') + '\n  { S7_Templates := "SrcType := Int" }\n  ' + instruction + '( in1 := #Left, in2 := #Right )\n  Coil( #Result )\nEND_RUNG'
            (ROOT / 'sources' / (instruction.lower() + '-' + language.lower() + '.s7dcl')).write_text(
                source('Tgp_' + instruction + '_' + language, language, declarations, body), encoding='utf-8')
        declarations = ('VAR_INPUT\n  Enable : Bool;\nEND_VAR\nVAR_OUTPUT\n  Active : Bool;\nEND_VAR')
        body = ('RUNG wire#powerrail' if language == 'LAD' else 'RUNG TRUE') + '\n  "TiaMcp_IsEnabled"( Enable := #Enable, Ret_Val => #Active )\nEND_RUNG'
        (ROOT / 'sources' / ('fc-' + language.lower() + '.s7dcl')).write_text(
            source('Tgp_FC_' + language, language, declarations, body), encoding='utf-8')
        for kind, target in [('global', '"TiaMcp_EnableGate_DB"'), ('multi', '#Child')]:
            body = ('RUNG wire#powerrail' if language == 'LAD' else 'RUNG TRUE') + '\n  ' + target + '( Enable := #Enable, Active => #Active )\nEND_RUNG'
            extra = '\nVAR\n  Child : "TiaMcp_EnableGate";\nEND_VAR' if kind == 'multi' else ''
            (ROOT / 'sources' / ('fb-' + kind + '-' + language.lower() + '.s7dcl')).write_text(
                source('Tgp_FB_' + kind + '_' + language, language, declarations + extra, body), encoding='utf-8')


if __name__ == '__main__':
    main()
