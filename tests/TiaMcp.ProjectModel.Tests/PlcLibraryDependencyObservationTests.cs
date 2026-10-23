#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;
using TiaMcp.Openness.V21;
using static TiaMcp.Tests.PlcLibraryInstanceCaptureTests;

namespace TiaMcp.Tests
{
    internal static class PlcLibraryDependencyObservationTests
    {
        internal static void Run(Action<string, Action> test)
        {
            test("direct dependency observation retains exact detached ordered GUIDs", () => {
                var f=new Fixture();var children=new[]{new LibraryTypeVersion(),new LibraryTypeVersion()};
                f.Version.ReadDependencies=()=>children;
                var result=PlcLibraryInstanceCapture.Read(f.Item);var dependencies=(JObject)result["dependencies"];
                Equal("linked",result.Value<string>("status"));Equal("observed",dependencies.Value<string>("status"));
                Equal(false,dependencies.Value<bool>("dependencyClosureVerified"));Equal(false,dependencies.Value<bool>("nativeQualified"));
                Equal(string.Join(",",children.Select(c=>c.Identity.ToString("D"))),string.Join(",",dependencies["requiredVersionGuids"].Values<string>()));
                Equal(1,f.Info.VersionReads);Equal(1,f.Version.DependencyReads);Equal(true,children.All(c=>c.GuidReads==1));
                string original=dependencies.ToString();children[0]=new LibraryTypeVersion();Equal(original,dependencies.ToString());
            });
            test("empty observed dependency list differs from unavailable", () => {
                var f=new Fixture();var result=PlcLibraryInstanceCapture.Read(f.Item);
                Equal("observed",result["dependencies"].Value<string>("status"));Equal(0,((JArray)result["dependencies"]["requiredVersionGuids"]).Count);
                f.Version.ReadDependencies=()=>null;Unavailable(f,"invalidOrUnboundedDependencies");
            });
            test("repeated and self dependency identities remain original observations", () => {
                var f=new Fixture();f.Version.ReadDependencies=()=>new[]{f.Version,f.Version};
                var result=PlcLibraryInstanceCapture.Read(f.Item);
                Equal(2,((JArray)result["dependencies"]["requiredVersionGuids"]).Count);
                Equal(3,f.Version.GuidReads);
            });
            foreach(string invalid in new[]{"nullEntry","emptyGuid","tooMany","nativeGetter","nativeIterator","nativeGuid"})
                test("dependency failure retains linked owner and no partial list: "+invalid,()=> {
                    var f=new Fixture();
                    switch(invalid)
                    {
                        case "nullEntry":f.Version.ReadDependencies=()=>new LibraryTypeVersion[]{null};break;
                        case "emptyGuid":f.Version.ReadDependencies=()=>new[]{new LibraryTypeVersion {ReadGuid=()=>Guid.Empty}};break;
                        case "tooMany":f.Version.ReadDependencies=()=>Enumerable.Repeat(new LibraryTypeVersion(),1001);break;
                        case "nativeGetter":f.Version.ReadDependencies=()=>throw new EngineeringException("fixture");break;
                        case "nativeIterator":f.Version.ReadDependencies=BrokenNative;break;
                        case "nativeGuid":f.Version.ReadDependencies=()=>new[]{new LibraryTypeVersion {ReadGuid=()=>throw new EngineeringException("fixture")}};break;
                    }
                    Unavailable(f,invalid.StartsWith("native",StringComparison.Ordinal)?"nativeReadFailed":"invalidOrUnboundedDependencies");
                });
            foreach(string point in new[]{"getter","iterator","guid"})
                test("unexpected dependency "+point+" failure propagates",()=> {
                    var f=new Fixture();
                    if(point=="getter")f.Version.ReadDependencies=()=>throw new InvalidOperationException("fixture");
                    else if(point=="iterator")f.Version.ReadDependencies=BrokenUnexpected;
                    else f.Version.ReadDependencies=()=>new[]{new LibraryTypeVersion{ReadGuid=()=>throw new InvalidOperationException("fixture")}};
                    try {PlcLibraryInstanceCapture.Read(f.Item);}catch(InvalidOperationException){return;}
                    throw new Exception("Unexpected failure swallowed");
                });
            test("shared budget stops before another native dependency getter",()=> {
                var budget=new OpennessSession.LibraryDependencyBudgetProbe(2);var first=new Fixture();var second=new Fixture();
                var a=PlcLibraryInstanceCapture.Read(first.Item,budget.Take);var b=PlcLibraryInstanceCapture.Read(second.Item,budget.Take);
                Equal("observed",a["dependencies"].Value<string>("status"));Equal("linked",b.Value<string>("status"));
                Equal("budgetExhausted",b["dependencies"].Value<string>("reason"));Equal(0,second.Version.DependencyReads);
                Equal(2,budget.Used);Equal(true,budget.Truncated);
            });
            test("budget exhaustion disposes enumeration without partial identity publication",()=> {
                int disposed=0;var child=new LibraryTypeVersion();var f=new Fixture();var budget=new OpennessSession.LibraryDependencyBudgetProbe(2);
                IEnumerable<LibraryTypeVersion> Items(){try{yield return child;yield return child;}finally{disposed++;}}
                f.Version.ReadDependencies=Items;var actual=PlcLibraryInstanceCapture.Read(f.Item,budget.Take);
                Equal("budgetExhausted",actual["dependencies"].Value<string>("reason"));Equal(true,actual["dependencies"]["requiredVersionGuids"]==null);
                Equal(1,disposed);Equal(0,child.GuidReads);Equal(2,budget.Used);
            });
            test("exact budget includes terminal enumeration probe",()=> {
                var f=new Fixture();f.Version.ReadDependencies=()=>new[]{new LibraryTypeVersion()};
                var exact=new OpennessSession.LibraryDependencyBudgetProbe(3);
                Equal("observed",PlcLibraryInstanceCapture.Read(f.Item,exact.Take)["dependencies"].Value<string>("status"));Equal(3,exact.Used);Equal(false,exact.Truncated);
                var shortBudget=new OpennessSession.LibraryDependencyBudgetProbe(2);
                Equal("budgetExhausted",PlcLibraryInstanceCapture.Read(f.Item,shortBudget.Take)["dependencies"].Value<string>("reason"));
            });
            test("native getter failures still consume shared work budget",()=> {
                var f=new Fixture();f.Version.ReadDependencies=()=>throw new EngineeringException("fixture");var budget=new OpennessSession.LibraryDependencyBudgetProbe(1);
                Equal("nativeReadFailed",PlcLibraryInstanceCapture.Read(f.Item,budget.Take)["dependencies"].Value<string>("reason"));
                Equal("budgetExhausted",PlcLibraryInstanceCapture.Read(f.Item,budget.Take)["dependencies"].Value<string>("reason"));Equal(1,f.Version.DependencyReads);
            });
        }
        static IEnumerable<LibraryTypeVersion> BrokenNative(){yield return new LibraryTypeVersion();throw new EngineeringException("fixture");}
        static IEnumerable<LibraryTypeVersion> BrokenUnexpected(){yield return new LibraryTypeVersion();throw new InvalidOperationException("fixture");}
        static void Unavailable(Fixture fixture,string reason)
        {
            var value=PlcLibraryInstanceCapture.Read(fixture.Item);
            Equal("linked",value.Value<string>("status"));Equal(fixture.Version.Identity.ToString("D"),value.Value<string>("versionGuid"));
            var dependencies=(JObject)value["dependencies"];Equal("unavailable",dependencies.Value<string>("status"));Equal(reason,dependencies.Value<string>("reason"));
            Equal(true,dependencies["requiredVersionGuids"]==null);Equal(false,dependencies.Value<bool>("dependencyClosureVerified"));
        }
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal sealed class LibraryDependencyBudgetProbe
        {
            private readonly ScanBudget budget;
            internal LibraryDependencyBudgetProbe(int maximum){budget=new ScanBudget(maximum);}
            internal bool Take()=>budget.Take();
            internal int Used=>budget.Used;
            internal bool Truncated=>budget.Truncated;
        }
    }
}
