using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

// Seeds the UMAC-protected fixture for the project_open UMAC route (docs/reference/protected-credentials.md#project-open-umac-route):
// opens a saved, task-created TiaMcpScratch_ project under repository artifacts in its own headless Portal and protects it with
// ProjectBase.ProtectProject(administratorUsername, SecureString). The password is read from standard input as exact UTF-8 bytes
// (the same bytes stored with --credential-store create --stdin), never from an argument, and is not printed.
internal static class Program
{
    private static readonly string Api = InstalledApiDirectory();

    private static string InstalledApiDirectory()
    {
        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48"))
        {
            var basePath = key?.GetValue("Siemens.Engineering.Base") as string;
            return string.IsNullOrWhiteSpace(basePath) ? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48" : Path.GetDirectoryName(basePath);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, evt) =>
        {
            string name = new AssemblyName(evt.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            string path = Path.Combine(Api, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args).ToString(Formatting.None)); return 0; }
        catch (Exception ex) { Console.WriteLine(new JObject { ["completed"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.Message }.ToString(Formatting.None)); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JObject Run(string[] args)
    {
        if (args.Length != 3 || args[0] != "protect")
            throw new InvalidOperationException("Usage: UmacProjectSeeder.exe protect <TiaMcpScratch_*.ap21 under artifacts> <administratorUsername>  (password on stdin)");
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../artifacts")).TrimEnd('\\') + "\\";
        string projectPath = Path.GetFullPath(args[1]);
        if (!projectPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(projectPath).StartsWith("TiaMcpScratch_", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(projectPath), ".ap21", StringComparison.OrdinalIgnoreCase) || !File.Exists(projectPath))
            throw new InvalidOperationException("The target must be an existing task-created TiaMcpScratch_ .ap21 project under repository artifacts.");
        string user = args[2];
        var result = new JObject { ["projectPath"] = projectPath, ["administratorUsername"] = user };
        using (var password = ReadPassword())
        using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            var project = portal.Projects.Open(new FileInfo(projectPath));
            project.ProtectProject(user, password);
            project.Save();
            result["protected"] = true;
            project.Close();
        }
        result["completed"] = true;
        return result;
    }

    private static SecureString ReadPassword()
    {
        byte[] bytes;
        using (var input = Console.OpenStandardInput())
        using (var buffer = new MemoryStream())
        {
            input.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length == 0) throw new InvalidOperationException("No administrator password on standard input.");
            var password = new SecureString();
            foreach (char c in text) password.AppendChar(c);
            password.MakeReadOnly();
            return password;
        }
        finally { Array.Clear(bytes, 0, bytes.Length); }
    }
}
