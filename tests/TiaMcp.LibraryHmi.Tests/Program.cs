using TiaMcp.Model;
var tests = new (string Name, Action Test)[] {
    ("Classic and Unified cannot cross-bind", () => Reject(() => HmiRecipeRules.RequireSameFamily("classic", "unified"))),
    ("Unknown family rejected", () => Reject(() => HmiRecipeRules.RequireFamily("wincc"))),
    ("Explicit known family accepted", () => HmiRecipeRules.RequireSameFamily("unified", "unified")),
    ("Native name preserves Unicode", () => HmiRecipeRules.RequireName("Mötör状态", "name")),
    ("Empty native name rejected", () => Reject(() => HmiRecipeRules.RequireName(" ", "name"))),
    ("Path masquerading as native name rejected", () => Reject(() => HmiRecipeRules.RequireName("folder/Motor", "name"))),
    ("Backslash name rejected", () => Reject(() => HmiRecipeRules.RequireName("folder\\Motor", "name"))),
    ("Control character name rejected", () => Reject(() => HmiRecipeRules.RequireName("Motor\n", "name"))),
    ("Screen-edge rectangle accepted", () => HmiRecipeRules.RequirePlacement(790, 470, 10, 10, 800, 480)),
    ("Off-screen rectangle rejected", () => Reject(() => HmiRecipeRules.RequirePlacement(790, 470, 11, 10, 800, 480))),
    ("Zero-size rectangle rejected", () => Reject(() => HmiRecipeRules.RequirePlacement(0, 0, 0, 10, 800, 480))),
    ("Negative placement rejected", () => Reject(() => HmiRecipeRules.RequirePlacement(-1, 0, 10, 10, 800, 480))),
    ("Overflow cannot bypass placement bounds", () => Reject(() => HmiRecipeRules.RequirePlacement(long.MaxValue, 0, 10, 10, 800, 480))),
    ("Unavailable native dimensions rejected", () => Reject(() => HmiRecipeRules.RequirePlacement(0, 0, 10, 10, 0, 0))),
    ("Complete named interface mapping accepted", () => HmiRecipeRules.RequireBindings(new[]{"Status","Command"}, new[]{"Command","Status"}, new[]{"Status","Command"})),
    ("Missing required mapping rejected", () => Reject(() => HmiRecipeRules.RequireBindings(new[]{"Status"}, new[]{"Command","Status"}, new[]{"Command"}))),
    ("Unknown property rejected", () => Reject(() => HmiRecipeRules.RequireBindings(new[]{"State"}, new[]{"Status"}, Array.Empty<string>()))),
    ("Duplicate mapping rejected", () => Reject(() => HmiRecipeRules.RequireBindings(new[]{"Status","Status"}, new[]{"Status"}, Array.Empty<string>()))),
    ("Interface names matched ordinally", () => Reject(() => HmiRecipeRules.RequireBindings(new[]{"status"}, new[]{"Status"}, Array.Empty<string>()))),
    ("Native GUID equality required", () => { var guid=Guid.NewGuid(); HmiRecipeRules.RequireExactVersion(guid,guid); Reject(() => HmiRecipeRules.RequireExactVersion(guid,Guid.NewGuid())); }),
    ("Missing GUID never proves version", () => Reject(() => HmiRecipeRules.RequireExactVersion(Guid.Empty,Guid.Empty))),
    ("Exact native tag type accepted", () => HmiRecipeRules.RequireTypeCompatibility("Int","Int")),
    ("Numeric widening not inferred", () => Reject(() => HmiRecipeRules.RequireTypeCompatibility("Int","DInt"))),
    ("Equal labels with incompatible capacity rejected", () => Reject(() => HmiRecipeRules.RequireTypeCompatibility("String","String",20,10))),
    ("Unknown capacity differs from proved capacity", () => Reject(() => HmiRecipeRules.RequireTypeCompatibility("String","String",20,null))),
    ("Explicit native version preserved in reference", () => { if(HmiRecipeRules.ExactContainedType("Motor",new Version("1.2.3"))!="V1.2.3\\Motor") throw new Exception("Version reference changed."); }),
    ("Two-part version rejected", () => Reject(() => HmiRecipeRules.ExactContainedType("Motor",new Version("1.2")))),
    ("Localized internal tag label retained", () => HmiRecipeRules.RequireNewInternalTagDefault("<Interne Variable>","<Interne Variable>","",false)),
    ("Empty internal tag label allowed", () => HmiRecipeRules.RequireNewInternalTagDefault("","","",false)),
    ("Translated internal label not substituted", () => Reject(() => HmiRecipeRules.RequireNewInternalTagDefault("<Interne Variable>","<Internal tag>","",false))),
    ("Configured connection cannot masquerade as internal label", () => Reject(() => HmiRecipeRules.RequireNewInternalTagDefault("<Interne Variable>","<Interne Variable>","",true))),
    ("New internal tag cannot silently acquire PLC binding", () => Reject(() => HmiRecipeRules.RequireNewInternalTagDefault("<Internal tag>","<Internal tag>","Motor.Status",false))),
};
int failures=0;
foreach(var test in tests) { try { test.Test(); Console.WriteLine("PASS "+test.Name); } catch(Exception ex) { failures++; Console.Error.WriteLine("FAIL "+test.Name+": "+ex.Message); } }
Console.WriteLine($"{tests.Length-failures}/{tests.Length} library HMI recipe checks passed; no native qualification is inferred.");
int linkTests = 0;
HmiLibraryLinkTests.Run((name, test) => { linkTests++; try { test(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); } });
HmiInternalTagCreationTests.Run((name, test) => { linkTests++; try { test(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); } });
Console.WriteLine($"{linkTests} HMI library-link cases executed; {failures} total failures; synthetic native providers only.");
return failures==0?0:1;
static void Reject(Action action) { try { action(); } catch(ArgumentException) { return; } throw new Exception("Expected mapping rejection."); }
