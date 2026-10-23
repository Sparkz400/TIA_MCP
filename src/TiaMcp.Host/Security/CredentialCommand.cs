using TiaMcp.Core.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;

namespace TiaMcp.Host;

internal static class CredentialCommand
{
    internal static int Run(string[] args)
    {
        byte[]? secret = null;
        try
        {
            if (args.Length == 1 && args[0] == "help")
            {
                Console.WriteLine("Local protected credentials (Windows user profile required):\n  --credential-store create --binding public-binding.json [--stdin] [--vault-name default|32-hex-id]\n  --credential-store inspect|retire --binding public-binding.json --reference public-reference.json [--vault-name default|32-hex-id]\n  --credential-store list [--vault-name default|32-hex-id]\nSecret input is an un-echoed console prompt, or exact raw bytes from redirected stdin with --stdin. No secret argument, environment-variable or file-path option, and no export command. Only public status/references are printed. Retirement disables local use; it does not revoke an external credential or erase encrypted recovery data.");
                return 0;
            }
            if (args.Length == 0 || args[0] is not ("create" or "inspect" or "retire" or "list")) throw CredentialVault.Error("CredentialCommandInvalid");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                string name = args[i];
                if (name is not ("--binding" or "--reference" or "--vault-name" or "--stdin") || options.ContainsKey(name)) throw CredentialVault.Error("CredentialCommandInvalid");
                if (name == "--stdin") options.Add(name, "true");
                else { if (++i >= args.Length) throw CredentialVault.Error("CredentialCommandInvalid"); options.Add(name, args[i]); }
            }
            bool create = args[0] == "create", list = args[0] == "list";
            if ((!create && options.ContainsKey("--stdin")) || list && (options.ContainsKey("--binding") || options.ContainsKey("--reference")) ||
                !list && !options.ContainsKey("--binding") || create && options.ContainsKey("--reference") || !list && !create && !options.ContainsKey("--reference")) throw CredentialVault.Error("CredentialCommandInvalid");
            CredentialBinding? binding = list ? null : CredentialVault.Parse<CredentialBinding>(CredentialVault.ReadBounded(options["--binding"], 16384));
            if (binding is not null) CredentialVault.ValidateBinding(binding);
            CredentialReference? reference = list || create ? null : CredentialVault.Parse<CredentialReference>(CredentialVault.ReadBounded(options["--reference"], 4096));
            if (create && (options.ContainsKey("--stdin") != Console.IsInputRedirected)) throw CredentialVault.Error("CredentialInputModeRequired");
            var vault = new CredentialVault(CredentialVault.UserRoot(options.GetValueOrDefault("--vault-name", "default")));
            object result;
            if (create)
            {
                if (options.ContainsKey("--stdin")) secret = ReadPipe(Console.OpenStandardInput());
                else
                {
                    Console.Error.Write("Secret (input hidden; Escape cancels): ");
                    bool previous = Console.TreatControlCAsInput;
                    try { Console.TreatControlCAsInput = true; secret = ReadConsole(() => Console.ReadKey(intercept: true)); }
                    finally { Console.TreatControlCAsInput = previous; Console.Error.WriteLine(); }
                }
                result = vault.Create(binding!, secret);
            }
            else if (list) result = vault.List();
            else if (args[0] == "retire") result = vault.Retire(reference!, binding!);
            else result = vault.Inspect(reference!, binding!);
            Console.WriteLine(JsonSerializer.Serialize(result, CredentialVault.Json));
            return 0;
        }
        catch (Exception exception)
        {
            // No exception text/inner stack: a consumer or input parser can include secret bytes.
            string code = exception is BridgeException bridge ? bridge.Code : "CredentialUnavailable";
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = code, secretReturned = false }, CredentialVault.Json));
            return 1;
        }
        finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
    }
    internal static byte[] ReadPipe(Stream input)
    {
        byte[] buffer = new byte[CredentialVault.MaxSecretBytes + 1];
        try
        {
            int count = 0, read;
            while (count < buffer.Length && (read = input.Read(buffer, count, buffer.Length - count)) != 0) count += read;
            if (count is < 1 or > CredentialVault.MaxSecretBytes) throw CredentialVault.Error("InvalidCredentialLength");
            return buffer[..count];
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
    internal static byte[] ReadConsole(Func<ConsoleKeyInfo> readKey)
    {
        char[] buffer = new char[CredentialVault.MaxSecretBytes]; int count = 0;
        try
        {
            while (true)
            {
                var key = readKey();
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) throw CredentialVault.Error("CredentialInputCancelled");
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (count > 0) buffer[--count] = '\0'; continue; }
                if (key.KeyChar == '\0' || char.IsControl(key.KeyChar)) throw CredentialVault.Error("CredentialInputInvalid");
                if (count == buffer.Length) throw CredentialVault.Error("InvalidCredentialLength");
                buffer[count++] = key.KeyChar;
            }
            var encoding = new UTF8Encoding(false, true);
            int bytes = encoding.GetByteCount(buffer, 0, count);
            if (bytes is < 1 or > CredentialVault.MaxSecretBytes) throw CredentialVault.Error("InvalidCredentialLength");
            return encoding.GetBytes(buffer, 0, count);
        }
        finally { Array.Clear(buffer); }
    }
}
