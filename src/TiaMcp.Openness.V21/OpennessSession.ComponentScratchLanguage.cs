using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // The component fixtures and captured storage references were recorded in
        // de-DE-only projects. A component-qualification Host makes each new
        // scratch exactly de-DE before any content exists, whatever the TIA UI language.
        private void ComponentScratchLanguage(Project project, JObject result)
        {
            if (!_componentQualificationEnabled) return;
            var settings = project.LanguageSettings;
            var german = settings.Languages.Find(CultureInfo.GetCultureInfo("de-DE"))
                ?? throw new BridgeException("ProjectLanguageUnavailable", "TIA offers no de-DE project language; the component fixtures cannot be reproduced.");
            if (!settings.ActiveLanguages.Contains(german)) settings.ActiveLanguages.Add(german);
            settings.ReferenceLanguage = german;
            settings.EditingLanguage = german;
            foreach (var other in settings.ActiveLanguages.Where(language => language.Culture.Name != "de-DE").ToArray()) settings.ActiveLanguages.Remove(other);
            if (settings.ActiveLanguages.Count != 1 || settings.ReferenceLanguage.Culture.Name != "de-DE" || settings.EditingLanguage.Culture.Name != "de-DE")
                throw new BridgeException("ProjectLanguageUnavailable", "Component scratch languages are not exactly de-DE.");
            result["componentQualificationLanguages"] = new JObject { ["active"] = new JArray("de-DE"), ["reference"] = "de-DE", ["editing"] = "de-DE" };
        }
    }
}
