#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Scalar string attributes only; Comment is a MultilingualText and has no attribute-write route here.
        private static readonly HashSet<string> ProjectWriteAttributes = new HashSet<string>(new[] { "Author", "Copyright", "Family", "Version" }, StringComparer.Ordinal);

        private JObject ProjectProperties(JObject args)
        {
            var project = SelectProject(args);
            int max = Limit(args, 200);
            var truncated = new JObject();
            JArray Bounded<T>(string key, IEnumerable<T> items, Func<T, JToken> map)
            {
                var budget = new ScanBudget(max);
                var rows = new JArray(items.TakeWhile(_ => budget.Take()).Select(map));
                truncated[key] = budget.Truncated;
                return rows;
            }
            var settings = project.LanguageSettings;
            var languages = ProjectLanguageView(settings);
            languages["supported"] = Bounded("supportedLanguages", settings.Languages, l => l.Culture.Name);
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["properties"] = new JObject { ["author"] = project.Author, ["copyright"] = project.Copyright, ["family"] = project.Family, ["version"] = project.Version,
                ["comment"] = Bounded("comment", project.Comment.Items, i => new JObject { ["culture"] = i.Language?.Culture.Name, ["text"] = i.Text }),
                ["creationTime"] = project.CreationTime.ToString("O"), ["lastModified"] = project.LastModified.ToString("O"), ["lastModifiedBy"] = project.LastModifiedBy,
                ["sizeKb"] = project.Size, ["isModified"] = project.IsModified, ["isPrimary"] = project.IsPrimary };
            result["attributeAccess"] = new JObject(project.GetAttributeInfos().Where(i => ProjectWriteAttributes.Contains(i.Name)).Select(i => new JProperty(i.Name, i.AccessMode.ToString())));
            result["usedProducts"] = Bounded("usedProducts", project.UsedProducts, p => new JObject { ["name"] = p.Name, ["version"] = p.Version });
            result["historyEntries"] = Bounded("historyEntries", project.HistoryEntries, h => new JObject { ["dateTime"] = h.DateTime.ToString("O"), ["text"] = h.Text });
            result["languageSettings"] = languages;
            result["textCategories"] = Bounded("textCategories", project.TextCategories, c => new JObject { ["identifier"] = c.Identifier, ["name"] = c.Name });
            result["completeness"] = new JObject { ["complete"] = !truncated.Properties().Any(p => (bool)p.Value), ["truncated"] = truncated, ["maxItems"] = max,
                ["scope"] = "Public V21 ProjectBase metadata, used products, history entries in native order, language settings and text categories of one open project.",
                ["note"] = "attributeAccess reports native access for the project_attributes_set allowlist; Comment is multilingual and read-only here. Point-in-time observation, no revision guarantee." };
            return result;
        }

        private static JObject ProjectLanguageView(LanguageSettings settings) => new JObject {
            ["active"] = new JArray(settings.ActiveLanguages.Select(l => l.Culture.Name).OrderBy(n => n, StringComparer.Ordinal)),
            ["reference"] = settings.ReferenceLanguage?.Culture.Name, ["editing"] = settings.EditingLanguage?.Culture.Name };

        // Validates the payload against the current settings and returns the exact expected end state.
        private static JObject PlanProjectLanguages(Project project, JObject payload, out Language[] add, out Language[] remove, out Language reference, out Language editing)
        {
            var settings = project.LanguageSettings;
            Language Resolve(string culture)
            {
                CultureInfo info;
                try { info = CultureInfo.GetCultureInfo(culture); }
                catch (CultureNotFoundException) { throw new BridgeException("InvalidRequest", "Unknown culture name: " + culture); }
                return settings.Languages.Find(info) ?? throw new BridgeException("UnsupportedLanguage", "The project does not support language " + culture + ".");
            }
            if (payload["addActive"] == null && payload["removeActive"] == null && payload["referenceLanguage"] == null && payload["editingLanguage"] == null)
                throw new BridgeException("InvalidRequest", "Specify addActive, removeActive, referenceLanguage and/or editingLanguage.");
            add = ChangeStringArray(payload, "addActive", 64, 32, true).Select(Resolve).ToArray();
            remove = ChangeStringArray(payload, "removeActive", 64, 32, true).Select(Resolve).ToArray();
            reference = payload["referenceLanguage"] == null ? settings.ReferenceLanguage : Resolve(ChangeText(payload, "referenceLanguage", 32));
            editing = payload["editingLanguage"] == null ? settings.EditingLanguage : Resolve(ChangeText(payload, "editingLanguage", 32));
            var added = new HashSet<string>(add.Select(l => l.Culture.Name), StringComparer.Ordinal);
            var removed = new HashSet<string>(remove.Select(l => l.Culture.Name), StringComparer.Ordinal);
            if (added.Overlaps(removed)) throw new BridgeException("InvalidRequest", "A language cannot be both added and removed.");
            var protectedNames = new[] { settings.ReferenceLanguage?.Culture.Name, settings.EditingLanguage?.Culture.Name, reference?.Culture.Name, editing?.Culture.Name };
            var inUse = removed.FirstOrDefault(protectedNames.Contains);
            if (inUse != null) throw new BridgeException("LanguageInUse", "Removing the reference or editing language is refused: " + inUse);
            var active = new HashSet<string>(settings.ActiveLanguages.Select(l => l.Culture.Name), StringComparer.Ordinal);
            active.UnionWith(added);
            active.ExceptWith(removed);
            if (reference == null || editing == null || !active.Contains(reference.Culture.Name) || !active.Contains(editing.Culture.Name))
                throw new BridgeException("InvalidRequest", "Reference and editing languages must be active after the change; add them explicitly with addActive.");
            return new JObject { ["active"] = new JArray(active.OrderBy(n => n, StringComparer.Ordinal)), ["reference"] = reference.Culture.Name, ["editing"] = editing.Culture.Name };
        }

        private static void ApplyProjectLanguages(EngineeringChange change, Project project)
        {
            var settings = project.LanguageSettings;
            var before = ProjectLanguageView(settings);
            var expected = PlanProjectLanguages(project, change.Payload, out var add, out var remove, out var reference, out var editing);
            // Add first and remove last so the reference/editing language is always active when set.
            foreach (var language in add) if (!settings.ActiveLanguages.Contains(language)) settings.ActiveLanguages.Add(language);
            if (!Equals(settings.ReferenceLanguage, reference)) settings.ReferenceLanguage = reference;
            if (!Equals(settings.EditingLanguage, editing)) settings.EditingLanguage = editing;
            foreach (var language in remove) if (settings.ActiveLanguages.Contains(language)) settings.ActiveLanguages.Remove(language);
            var after = ProjectLanguageView(settings);
            change.Readbacks.Add(new JObject { ["role"] = "project-language-settings", ["before"] = before, ["after"] = after });
            if (!JToken.DeepEquals(after, expected)) throw new BridgeException("MutationVerificationFailed", "Project language settings read-back differs from the request.");
        }
    }
}
