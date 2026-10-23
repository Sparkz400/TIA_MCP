using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class OptionalHmiIsolationProbe
{
    private static int Main(string[] args)
    {
        if(args.Length!=3 && args.Length!=4) return 2;
        string worker=Path.GetFullPath(args[0]), api=Path.GetFullPath(args[1]), missingApi=Path.GetFullPath(args[2]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request) {
            var name=new AssemblyName(request.Name);
            if(name.Name.StartsWith("Siemens.Engineering.WinCC",StringComparison.Ordinal))
                throw new InvalidOperationException("A PLC/metadata-only path tried to load an optional HMI assembly: "+name.Name);
            string directory=name.Name.StartsWith("Siemens.Engineering.",StringComparison.Ordinal)?api:Path.GetDirectoryName(worker);
            string path=Path.Combine(directory,name.Name+".dll");
            return File.Exists(path)?Assembly.LoadFrom(path):null;
        };
        try {
            var assembly=Assembly.LoadFrom(worker);
            var sessionType=assembly.GetType("TiaMcp.Openness.V21.OpennessSession",true);
            var constructor=sessionType.GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(bool)},null);
            if(constructor==null) throw new InvalidOperationException("The current (API directory, component qualification) session constructor is unavailable.");
            var session=constructor.Invoke(new object[]{missingApi,false});
            var jsonAssembly=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(worker),"Newtonsoft.Json.dll"));
            var jsonType=jsonAssembly.GetType("Newtonsoft.Json.Linq.JObject",true);
            var execute=sessionType.GetMethod("Execute",BindingFlags.Instance|BindingFlags.NonPublic);
            var parse=jsonType.GetMethod("Parse",new[]{typeof(string)});
            var portal=sessionType.GetField("_portal",BindingFlags.Instance|BindingFlags.NonPublic);
            if(execute==null || parse==null || portal==null) throw new InvalidOperationException("Required metadata probe entry point or native-startup guard is unavailable.");
            foreach(string operation in new[]{"bridge_info","library_capabilities","library_query"}) {
                if(portal.GetValue(session)!=null) throw new InvalidOperationException("A metadata-only probe acquired a Portal instance.");
                var arguments=operation=="library_query"?parse.Invoke(null,new object[]{"{\"action\":\"hmi_capabilities\"}"}):Activator.CreateInstance(jsonType);
                var result=execute.Invoke(session,new[]{(object)operation,arguments});
                if(result==null) throw new InvalidOperationException("Empty result: "+operation);
                if(portal.GetValue(session)!=null) throw new InvalidOperationException("A metadata-only probe acquired a Portal instance.");
                if(args.Length==4) File.WriteAllText(Path.Combine(args[3],operation+".json"),result.ToString());
                Console.WriteLine("PASS "+operation+" without optional HMI modules or native TIA startup");
            }
            ((IDisposable)session).Dispose();
            if(AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name.StartsWith("Siemens.Engineering.WinCC",StringComparison.Ordinal)))
                throw new InvalidOperationException("An optional HMI module was loaded through an alternate resolver.");
            return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
