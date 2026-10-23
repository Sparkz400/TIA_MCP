using TiaMcp.Core.Security;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Host;

if (args.Length > 0 && args[0] == "--lost-reply") { OperationChecks.LostReply(args); return; }
if (args.Length > 0 && args[0] == "--recovery-lost-reply") { RecoveryChecks.LostReply(args); return; }
if (args.Length > 0 && args[0] == "--probe")
{
    try
    {
        var input = CredentialVault.Parse<CredentialStatus>(File.ReadAllBytes(args[2]));
        var store = new CredentialVault(args[1]);
        Console.WriteLine(store.Inspect(input.Reference, input.Binding).DecryptionVerified ? "readable" : "retired");
    }
    catch (BridgeException e) { Console.WriteLine(e.Code); }
    return;
}
string output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "credentials-" + Guid.NewGuid().ToString("N")));
if (Directory.Exists(output)) throw new Exception("Fresh output required"); Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string code, string label) { try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected " + code + ": " + label); }
string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
string root = Path.Combine(output, "vault"); var vault = new CredentialVault(root);
var binding = new CredentialBinding(new("fixture-provider", "fixture-authority", "CPU/one", "account", "monitoring"), "runtime-authentication", "monitor-reader");
byte[] secret = Encoding.UTF8.GetBytes("SYNTHETIC-ONLY-" + Guid.NewGuid().ToString("N") + "-\u03a9");
var first = vault.Create(binding, secret); var second = vault.Create(binding, secret);
Check(first.Reference != second.Reference && first.Reference.CipherSha256 != second.Reference.CipherSha256, "Fresh references and randomized ciphertext preserve prior generations");
Check(!first.DecryptionVerified && first.SecretReturned == false, "Creation never claims provider use or returns a secret");
byte[]? borrowed = null; vault.Use(first.Reference, binding, value => { Check(value.SequenceEqual(secret), "Actual current-user DPAPI round trip"); borrowed = value; });
Check(borrowed!.All(b => b == 0), "Consumer buffer cleared after success");
Reject(() => vault.Use(first.Reference, binding, value => { borrowed = value; throw new Exception(Encoding.UTF8.GetString(value)); }), "CredentialConsumerFailed", "Consumer errors cannot expose the secret");
Check(borrowed!.All(b => b == 0), "Consumer buffer cleared after failure");
Check(vault.Inspect(first.Reference, binding).DecryptionVerified, "Inspect independently verifies readability without returning bytes");
foreach (var other in new[] { binding with { Purpose = "other" }, binding with { SubjectId = "OTHER" }, binding with { Resource = binding.Resource with { OwnerId = "CPU/two" } }, binding with { Resource = binding.Resource with { Authority = "other" } } })
    Reject(() => vault.Use(first.Reference, other, _ => throw new Exception("consumer called")), "CredentialContextMismatch", "Different expected purpose subject owner or authority refuses before consumer");
Reject(() => vault.Create(binding with { Resource = binding.Resource with { ResourceKind = null, ResourceId = null } }, secret), "CredentialContextMismatch", "Owner scope alone cannot bind a credential");
Reject(() => vault.Create(binding with { Purpose = "bad\ud800" }, secret), "CredentialInputInvalid", "Malformed Unicode refused before serialization");
Reject(() => vault.Create(binding, []), "InvalidCredentialLength", "Empty secret refused");
Reject(() => vault.Create(binding, new byte[CredentialVault.MaxSecretBytes + 1]), "InvalidCredentialLength", "Oversized secret refused");
byte[] maximum = new byte[CredentialVault.MaxSecretBytes]; RandomNumberGenerator.Fill(maximum);
var maximumStatus = vault.Create(binding with { Purpose="bounded-binary-input" }, maximum);
vault.Use(maximumStatus.Reference, maximumStatus.Binding, bytes => Check(bytes.SequenceEqual(maximum), "Maximum supported binary secret survives DPAPI storage"));
CryptographicOperations.ZeroMemory(maximum);
string statusPath = Path.Combine(output, "status.json"); File.WriteAllBytes(statusPath, JsonSerializer.SerializeToUtf8Bytes(first, CredentialVault.Json));
string Probe()
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (string arg in new[] { typeof(CredentialVault).Assembly.Location.Replace("TiaMcp.Host.dll", "TiaMcp.Credentials.Tests.dll"), "--probe", root, statusPath }) start.ArgumentList.Add(arg);
    // The test assembly is next to the copied Host reference in this test output.
    using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(15000)) { process.Kill(true); throw new Exception("Probe timeout"); }
    if (process.ExitCode != 0) throw new Exception(stderr.GetAwaiter().GetResult());
    return stdout.GetAwaiter().GetResult().Trim();
}
Check(Probe() == "readable", "Separate process same user opens exact protected reference");
using (var held = new FileStream(Path.Combine(root, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(Probe() == "CredentialStoreBusy", "Separate process cannot bypass an active vault lock");
string recordPath = Path.Combine(root, first.Reference.CredentialId + ".credential.json"); byte[] original = File.ReadAllBytes(recordPath);
void Tamper(Action<JsonObject> edit, CredentialReference reference, CredentialBinding expected, string code, string label)
{
    var node = JsonNode.Parse(original)!.AsObject(); edit(node);
    try { File.WriteAllBytes(recordPath, JsonSerializer.SerializeToUtf8Bytes(node)); Reject(() => vault.Inspect(reference, expected), code, label); }
    finally { File.WriteAllBytes(recordPath, original); }
}
Tamper(n => n["binding"]!["purpose"] = "other", first.Reference, binding, "CredentialUnavailable", "Unbound metadata tampering refused");
var replacementBinding = binding with { Purpose = "other" }; string replacementHash = CredentialVault.BindingHash(replacementBinding);
var replacementRef = first.Reference with { BindingSha256 = replacementHash };
Tamper(n => { n["binding"]!["purpose"] = "other"; n["reference"]!["bindingSha256"] = replacementHash; }, replacementRef, replacementBinding, "CredentialUnavailable", "Rehashed public metadata cannot change DPAPI purpose binding");
var record = JsonNode.Parse(original)!; byte[] cipher = Convert.FromBase64String((string)record["ciphertext"]!); cipher[^1] ^= 1;
string tamperedHash = Sha(cipher);
Tamper(n => { n["ciphertext"] = Convert.ToBase64String(cipher); n["reference"]!["cipherSha256"] = tamperedHash; }, first.Reference with { CipherSha256 = tamperedHash }, binding, "CredentialUnavailable", "Rehashed ciphertext tampering fails DPAPI integrity");
Tamper(n => n["secret"] = "SYNTHETIC-SURPLUS", first.Reference, binding, "CredentialInputInvalid", "Surplus secret field refused");
try { File.WriteAllText(recordPath, "{\"schemaVersion\":null,\"schemaVersion\":null}"); Reject(() => vault.Inspect(first.Reference, binding), "CredentialInputInvalid", "Duplicate fields refused"); }
finally { File.WriteAllBytes(recordPath, original); }
string link = Path.Combine(root, "alias.json");
if (!Links.CreateHardLink(link, recordPath, IntPtr.Zero)) throw new Exception("Hard-link fixture could not be created");
try { Reject(() => vault.Inspect(first.Reference, binding), "InvalidResourceKey", "Multiply linked encrypted file refuses before decryption"); }
finally { File.Delete(link); }
Reject(() => vault.Inspect(first.Reference with { CredentialId = "../escape" }, binding), "CredentialInputInvalid", "Reference cannot escape the vault directory");
Reject(() => vault.Inspect(first.Reference with { VaultId = new string('a',32) }, binding), "CredentialContextMismatch", "Wrong vault identity refused");
var file = new FileInfo(recordPath); var fileAcl = file.GetAccessControl(); var widened = file.GetAccessControl();
widened.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
try { file.SetAccessControl(widened); Reject(() => vault.Inspect(first.Reference, binding), "CredentialAclUnsafe", "Widened file ACL refuses without decrypting"); Check(file.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Count == 2, "Unsafe ACL is not silently repaired"); }
finally { var restore = new FileSecurity(); restore.SetSecurityDescriptorSddlForm(fileAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner), AccessControlSections.Access | AccessControlSections.Owner); file.SetAccessControl(restore); }
var directory = new DirectoryInfo(root); var directoryAcl = directory.GetAccessControl(); var wideDirectory = directory.GetAccessControl(); wideDirectory.SetAccessRuleProtection(false, true);
try { directory.SetAccessControl(wideDirectory); Reject(() => vault.List(), "CredentialAclUnsafe", "Inherited directory ACL refused"); }
finally { var restore = new DirectorySecurity(); restore.SetSecurityDescriptorSddlForm(directoryAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner), AccessControlSections.Access | AccessControlSections.Owner); directory.SetAccessControl(restore); }
string headerPath = Path.Combine(root, "vault.json"); byte[] header = File.ReadAllBytes(headerPath);
try { var node = JsonNode.Parse(header)!; node["userSid"] = "S-1-5-18"; File.WriteAllBytes(headerPath, JsonSerializer.SerializeToUtf8Bytes(node)); Reject(() => vault.Inspect(first.Reference, binding), "CredentialOwnerMismatch", "Foreign declared Windows owner refused"); }
finally { File.WriteAllBytes(headerPath, header); }
try { File.Move(headerPath, headerPath + ".held"); Reject(() => vault.Inspect(first.Reference, binding), "CredentialUnavailable", "Lost vault metadata never silently creates a replacement identity"); }
finally { File.Move(headerPath + ".held", headerPath); }
string copiedRoot = Path.Combine(output, "copied-vault"); var copied = new CredentialVault(copiedRoot);
foreach (var path in Directory.EnumerateFiles(root)) { if (Path.GetFileName(path) == ".lock") continue; string target = Path.Combine(copiedRoot, Path.GetFileName(path)); File.Copy(path, target); var acl = new FileSecurity(); acl.SetSecurityDescriptorSddlForm(new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner), AccessControlSections.Access | AccessControlSections.Owner); new FileInfo(target).SetAccessControl(acl); }
Reject(() => copied.Inspect(first.Reference, binding), "CredentialUnavailable", "Copied store cannot silently rebind to a different path");
var beforeRetire = File.ReadAllBytes(recordPath); var retired = vault.Retire(first.Reference, binding);
Check(retired.State == "retired" && !retired.DecryptionVerified && !retired.SecretReturned, "Retirement reports only local disabled use");
Check(File.ReadAllBytes(recordPath).SequenceEqual(beforeRetire), "Retirement preserves encrypted recovery generation");
Check(vault.Retire(first.Reference, binding) == retired && Probe() == "retired", "Retirement is idempotent and persists across processes");
Reject(() => vault.Use(first.Reference, binding, _ => throw new Exception("consumer called")), "CredentialRetired", "Retired reference cannot be consumed");
string lifecyclePath = Path.Combine(root, first.Reference.CredentialId + ".state.json");
try { File.Move(lifecyclePath, lifecyclePath + ".held"); Reject(() => vault.Inspect(first.Reference, binding), "CredentialUnavailable", "Lost lifecycle state cannot reactivate a retired reference"); }
finally { File.Move(lifecyclePath + ".held", lifecyclePath); }
byte[] lifecycle = File.ReadAllBytes(lifecyclePath);
try { var damaged=JsonNode.Parse(lifecycle)!; damaged["state"]="unknown"; File.WriteAllBytes(lifecyclePath,JsonSerializer.SerializeToUtf8Bytes(damaged)); Reject(() => vault.Inspect(first.Reference,binding),"CredentialUnavailable","Unreadable lifecycle state cannot authorize credential use"); }
finally { File.WriteAllBytes(lifecyclePath,lifecycle); }
Check(vault.Inspect(second.Reference, binding).DecryptionVerified, "Retiring one reference leaves a separate generation usable");
Check(vault.List().Count == 3 && vault.List().All(x => !x.DecryptionVerified && !x.SecretReturned), "List recovers public references without decrypting");
var keys = new Queue<ConsoleKeyInfo>(new[] { new ConsoleKeyInfo('a',ConsoleKey.A,false,false,false), new ConsoleKeyInfo('x',ConsoleKey.X,false,false,false), new ConsoleKeyInfo('\b',ConsoleKey.Backspace,false,false,false), new ConsoleKeyInfo('\u03a9',ConsoleKey.O,false,false,false), new ConsoleKeyInfo('\r',ConsoleKey.Enter,false,false,false) });
Check(CredentialCommand.ReadConsole(() => keys.Dequeue()).SequenceEqual(Encoding.UTF8.GetBytes("a\u03a9")), "Hidden console input preserves UTF8 and supports backspace");
Reject(() => CredentialCommand.ReadConsole(() => new ConsoleKeyInfo('\0',ConsoleKey.Escape,false,false,false)), "CredentialInputCancelled", "Escape cancels input");
Reject(() => CredentialCommand.ReadConsole(() => new ConsoleKeyInfo('\x03',ConsoleKey.C,false,false,true)), "CredentialInputCancelled", "Control-C cancels input");
Check(CredentialCommand.ReadPipe(new MemoryStream([1,2,10,0,255])).SequenceEqual(new byte[]{1,2,10,0,255}), "Pipe input preserves exact binary bytes and trailing newline");
Reject(() => CredentialCommand.ReadPipe(new MemoryStream()), "InvalidCredentialLength", "Empty pipe refused");
Reject(() => CredentialCommand.ReadPipe(new MemoryStream(new byte[CredentialVault.MaxSecretBytes+1])), "InvalidCredentialLength", "Oversized pipe refused before retention");
foreach (string path in Directory.EnumerateFiles(root)) Check(File.ReadAllBytes(path).AsSpan().IndexOf(secret) < 0, "Retained vault file contains no plaintext sentinel");
var capacity = new CredentialVault(Path.Combine(output,"capacity-vault"));capacity.List();
for(int i=0;i<8192;i++)File.WriteAllBytes(Path.Combine(output,"capacity-vault",i+".orphan"),[]);
Reject(()=>capacity.Create(binding,secret),"CredentialCapacity","Retained orphan files count toward bounded store capacity");
CryptographicOperations.ZeroMemory(secret);
OperationChecks.Run(Path.Combine(output, "operations"));
RecoveryChecks.Run(Path.Combine(output, "recovery"));
File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { completed=true, nativeExecuted=false, currentUserDpapiExecuted=true, crossUserProfileTested=false, count=checks.Count, checks }, CredentialVault.Json));
Console.WriteLine($"PASS protected credential storage: {checks.Count} checks; actual current-user DPAPI/ACL, no native provider. Receipts retained at {output}");

internal static class Links
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, EntryPoint="CreateHardLinkW", SetLastError=true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
