using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace TiaMcp.OptionalStartup
{
    // Test-only CLR observer. It neither redirects nor denies assembly loads and
    // has no Siemens references or engineering operations. The tested worker's
    // binary is unchanged; only the isolated qualification config loads this DLL.
    public sealed class Observer : AppDomainManager
    {
        private static readonly object Gate = new object();
        public override void InitializeNewDomain(AppDomainSetup setup)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => Record(args.LoadedAssembly);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) Record(assembly);
            base.InitializeNewDomain(setup);
        }

        private static void Record(Assembly assembly)
        {
            string path = Environment.GetEnvironmentVariable("TIA_MCP_OPTIONAL_ASSEMBLY_LOG");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string line = Convert.ToBase64String(Encoding.UTF8.GetBytes(assembly.FullName)) + "\t" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(assembly.IsDynamic ? "<dynamic>" : assembly.Location)) + Environment.NewLine;
                lock (Gate) File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch { /* Missing observer evidence fails the harness, never the worker. */ }
        }
    }
}
