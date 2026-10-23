using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TiaMcp.Core;

internal static class RuntimeCaptureAclChecks
{
    [SupportedOSPlatform("windows")]
    internal static async Task<int> Run(string root)
    {
        int checks=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
        await using var f=new RuntimePollingChecks.PollFixture(root);
        f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(1001);return Task.CompletedTask;};
        await f.Start(duration:10);_=await f.Clock.NextTimer();var delay=await f.Clock.NextTimer();
        var before=JsonSerializer.SerializeToElement(await f.Captures.Status(f.Id,CancellationToken.None),BridgeProtocol.Json);
        string reference=before.GetProperty("snapshotRef").GetString()!;
        var directory=new DirectoryInfo(Path.Combine(f.Root,"runtime-captures",f.Id));
        // Let Windows normalize inherited-ACL bookkeeping before freezing the baseline.
        // A fresh directory can gain SE_DACL_AUTO_INHERITED on its first ACL write.
        var baseline=new DirectorySecurity();
        baseline.SetSecurityDescriptorSddlForm(directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access),AccessControlSections.Access);
        directory.SetAccessControl(baseline);
        var original=directory.GetAccessControl();var denied=directory.GetAccessControl();
        using var identity=WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!,FileSystemRights.CreateFiles,AccessControlType.Deny));
        string head=Path.Combine(directory.FullName,"head.json");byte[] headBytes=File.ReadAllBytes(head);
        try
        {
            directory.SetAccessControl(denied);
            try{File.WriteAllText(Path.Combine(directory.FullName,"denied-probe"),"must refuse");throw new Exception("Expected OS access-denied write");}
            catch(UnauthorizedAccessException){checks++;}
            f.Clock.Advance((int)(delay.Due-f.Clock.GetTimestamp()));
            var failed=await f.Ended();
            Check(failed.GetProperty("state").GetString()=="interrupted" && failed.GetProperty("localFailure").GetString()=="RuntimeCaptureStorageFailure","Actual directory ACL denial interrupts publication");
            Check(failed.GetProperty("snapshotRef").GetString()==reference && File.ReadAllBytes(head).SequenceEqual(headBytes),"OS denial preserves exact committed head and prefix");
            Check(failed.GetProperty("uncommittedDataUnknown").GetBoolean() && f.Page(failed).GetProperty("totalRows").GetInt32()==1,"Denied uncommitted reads stay unknown and are not exported as committed");
        }
        finally
        {
            var restore=new DirectorySecurity();
            restore.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),AccessControlSections.Access);
            directory.SetAccessControl(restore);
        }
        Check(directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)==original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),"Original directory access control is restored");
        int calls=f.Sessions.Reads.Count;var reopened=JsonSerializer.SerializeToElement(await f.Start(duration:10),BridgeProtocol.Json);
        Check(reopened.GetProperty("snapshotRef").GetString()==reference && f.Sessions.Reads.Count==calls,"Restoring permission does not replay acquisition or failed publication");
        _=await f.Captures.Export(reference,"json",CancellationToken.None);
        Check(f.Sessions.Reads.Count==calls,"Export after permission restoration is local observation only");
        return checks;
    }
}
