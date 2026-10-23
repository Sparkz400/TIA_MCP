"""Source-only caller boundaries; native caller compilation remains a separate gate."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / 'src/TiaMcp.Openness.V21'


class HmiLibraryLinkWiringTests(unittest.TestCase):
    def test_unified_tag_creation_uses_the_shared_preservation_boundary(self):
        source = (NATIVE / 'OpennessSession.LibraryHmi.cs').read_text(encoding='utf-8')
        start = source.index('private static JObject ApplyUnifiedHmi(')
        body = source[start:source.index('var screen = hmi.Screens.Find(', start)]
        self.assertIn('return HmiInternalTagCreation.Create(', body)
        self.assertIn('name => tags.Create(name)', body)
        self.assertIn('tag => tag.Connection, tag => tag.PlcTag', body)
        self.assertIn('connection => hmi.Connections.Find(connection) != null', body)
        self.assertNotIn('IsNullOrEmpty(tag.Connection)', body)

    def test_all_readback_callers_share_the_observation(self):
        source = (NATIVE / 'OpennessSession.LibraryHmi.cs').read_text(encoding='utf-8')
        start = source.index('private static JObject HmiReadback(')
        body = source[start:source.index('private sealed class HmiTargetSelection', start)]
        self.assertIn('return HmiLibraryLink.Read(item, expected);', body)
        self.assertNotIn('LibraryTypeInstanceInfo', body)
        self.assertIn('HmiReadback(created, version)', source)
        self.assertIn('HmiReadback(item, expected)', source)
        self.assertEqual(2, source.count('UnifiedFaceplateView(container, version)'))
        self.assertIn('HmiReadback(container)', (NATIVE / 'OpennessSession.Hmi.cs').read_text(encoding='utf-8'))

    def test_unified_prepare_and_apply_retain_the_verified_view(self):
        source = (NATIVE / 'OpennessSession.LibraryHmi.cs').read_text(encoding='utf-8')
        self.assertIn('var before = UnifiedFaceplateView(container, version);', source)
        self.assertIn('return UnifiedFaceplateView(container, version);', source)
        self.assertNotIn('HmiReadback(container, version);', source)
        self.assertNotIn('["nativeLinkVerified"] = true', source)
        start = source.index('private static JObject PrepareUnifiedHmi(')
        end = source.index('private static long HmiInteger(', start)
        prepare = source[start:end]
        self.assertLess(prepare.index('var before = UnifiedFaceplateView(container, version);'), prepare.index('var available = container.Interface'))
        self.assertIn('before["observedTags"] = observedTags;', prepare)

    def test_both_classic_phases_refuse_unknown_links_before_io(self):
        source = (NATIVE / 'OpennessSession.HmiEngineering.cs').read_text(encoding='utf-8')
        self.assertNotIn('GetService<LibraryTypeInstanceInfo>', source)
        for declaration, io in [('private JObject PrepareClassicEngineeringBody(', 'screen.Export('), ('private static void ApplyClassicEngineeringBody(', '.Screens.Import(')]:
            start = source.index(declaration)
            end = source.index('[MethodImpl(MethodImplOptions.NoInlining)]', start)
            body = source[start:end]
            self.assertEqual(1, body.count('HmiLibraryLink.RequireNoLink(screen);'))
            self.assertLess(body.index('HmiLibraryLink.RequireNoLink(screen);'), body.index(io))

    def test_shared_helper_has_no_optional_hmi_module_types(self):
        source = (NATIVE / 'HmiLibraryLink.cs').read_text(encoding='utf-8')
        self.assertNotIn('Siemens.Engineering.Hmi', source)
        self.assertNotIn('Siemens.Engineering.WinCC', source)
        project = (ROOT / 'tests/TiaMcp.LibraryHmi.Tests/TiaMcp.LibraryHmi.Tests.csproj').read_text(encoding='utf-8')
        self.assertIn('../../src/TiaMcp.Openness.V21/HmiLibraryLink.cs', project)


if __name__ == '__main__':
    unittest.main()
