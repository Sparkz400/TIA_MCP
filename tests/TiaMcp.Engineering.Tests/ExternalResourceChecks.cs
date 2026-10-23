using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

internal static class ExternalResourceChecks
{
    internal static void Process(string[] args)
    {
        string root=Path.GetFullPath(args[1]);var record=new EngineeringOperationStore(root).Read(args[2]);
        if(record.State!="dispatchIntent")throw new Exception("Fake provider requires persisted dispatch intent");
        byte[] bytes=File.ReadAllBytes(Path.Combine(root,"after-state.json"));
        using(var stream=new FileStream(Path.Combine(root,"provider-state.json"),FileMode.Create,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}
        File.AppendAllText(Path.Combine(root,"provider-calls.txt"),"one synthetic mutation\n");
        Environment.Exit(17); // Deliberately lose the reply after the separately owned fixture state changed.
    }
    internal static void Run(string? destination=null)
    {
        string root=Path.GetFullPath(destination??Path.Combine(Path.GetTempPath(),"external-resources-"+Guid.NewGuid().ToString("N")));
        if(Directory.Exists(root))throw new Exception("Fresh external resource evidence directory required");Directory.CreateDirectory(root);
        var checks=new List<string>();void Check(bool ok,string label){if(!ok)throw new Exception(label);checks.Add(label);}
        void Reject(Action action,string code,string label){try{action();}catch(BridgeException e)when(e.Code==code){Check(true,label);return;}throw new Exception("Expected "+code+": "+label);}
        string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));
        EngineeringArtifactReference Save(string name,byte[] data){File.WriteAllBytes(Path.Combine(root,name),data);return new(name,Hash(data));}
        EngineeringArtifactReference Json(string name,object value)=>Save(name,JsonSerializer.SerializeToUtf8Bytes(value,BridgeProtocol.Json));
        var before=Save("before-state.json",Encoding.UTF8.GetBytes("{\"application\":\"main\",\"version\":1}"));
        var after=Save("after-state.json",Encoding.UTF8.GetBytes("{\"application\":\"main\",\"version\":2}"));
        File.Copy(Path.Combine(root,before.RelativePath),Path.Combine(root,"provider-state.json"));
        var checkpoint=Save("project-checkpoint.bin",Encoding.UTF8.GetBytes("synthetic local project checkpoint"));
        var proof=Json("project-proof.json",new{synthetic=true,projectOnly=true});
        var identity=new EngineeringExternalIdentity("fixture-provider","service:fixture","CPU/1","deployment","main");
        var effect=new EngineeringExternalEffect(identity,new("present",before.Sha256),new("present",after.Sha256));
        string key=EngineeringResourceKey.ForExternal(identity);
        string oracle="external:"+Hash(Encoding.UTF8.GetBytes("{\"authority\":\"service:fixture\",\"ownerId\":\"CPU/1\",\"provider\":\"fixture-provider\"}"))+":"+Hash(Encoding.UTF8.GetBytes("{\"resourceId\":\"main\",\"resourceKind\":\"deployment\"}"));
        Check(key==oracle,"Independent canonical external key oracle");
        Check(EngineeringResourceKey.ForExternal(identity with{ResourceKind=null,ResourceId=null})==key[..key.LastIndexOf(':')],"Owner reservation is the parent of exact resource keys");
        foreach(var changed in new[]{identity with{Provider="different"},identity with{Authority="other"},identity with{OwnerId="CPU/2"},identity with{ResourceKind="trust"},identity with{ResourceId="peer"},identity with{OwnerId="cpu/1"}})Check(EngineeringResourceKey.ForExternal(changed)!=key,"Distinct exact provider authority owner kind or ID");
        foreach(var invalid in new[]{identity with{Provider=""},identity with{Authority="bad\nvalue"},identity with{OwnerId="bad\ud800"},identity with{ResourceId=new string('a',1025)},identity with{ResourceKind=null}})Reject(()=>EngineeringResourceKey.ForExternal(invalid),"InvalidResourceKey","Malformed external identity refused");
        var store=new EngineeringOperationStore(root);
        EngineeringOperationPlan Plan(string name,EngineeringExternalEffect? external=default)
        {
            string project=Path.Combine(root,"projects",name+".ap21");
            var reference=new EngineeringObjectReference(new(project,"/CPU","/root","/CPU","hardware"),new("synthetic-capture",new('a',64)),new('b',64));
            var target=new NativeTargetObservation(reference,project,"synthetic-epoch",new('c',64),new('d',64),"synthetic CPU",null,true,[]);
            var resources=new List<EngineeringResourceEffect>{new(EngineeringResourceKey.ForPath("project",project),"project","synthetic project effect","projectArchiveToNewLocation")};
            var gates=new List<string>{"project.readback"};
            if(external is not null){string resourceKey=EngineeringResourceKey.ForExternal(external.Identity);resources.Add(new(resourceKey,"external","synthetic provider update","externalStateReadback"){External=external});gates.Add(EngineeringExternalEvidence.OutcomeGate(resourceKey));}
            return new("fixture.external",target,resources,[before],[],gates);
        }
        EngineeringArtifactReference Observe(EngineeringOperationRecord r,EngineeringResourceEffect resource,string phase,Action<JsonObject>? edit=null)
        {
            byte[] data=File.ReadAllBytes(Path.Combine(root,"provider-state.json"));var state=Save("state-"+Hash(data)+".json",data);
            var observation=new EngineeringExternalObservation("engineering-external-observation.v1",r.Id,r.PlanSha256,r.Revision,resource.ResourceKey,phase,Guid.NewGuid().ToString("N"),new("present",state.Sha256),state);
            var node=JsonSerializer.SerializeToNode(observation,BridgeProtocol.Json)!.AsObject();edit?.Invoke(node);return Json("observation-"+Guid.NewGuid().ToString("N")+".json",node);
        }
        string Child(string mode,params string[] arguments)
        {
            var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add(typeof(ExternalResourceChecks).Assembly.Location);start.ArgumentList.Add(mode);foreach(string argument in arguments)start.ArgumentList.Add(argument);
            using var process=System.Diagnostics.Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(15000)){process.Kill(true);throw new Exception("Fixture process exceeded deadline");}
            if(mode=="--external-process"){Check(process.ExitCode==17,"Actual provider process loses reply after mutation");return "lost";}
            if(process.ExitCode!=0)throw new Exception(errors.GetAwaiter().GetResult());return output.GetAwaiter().GetResult().Trim();
        }
        var plan=Plan("primary",effect);string projectKey=plan.Resources[0].ResourceKey;
        var local=Plan("legacy");var localJson=JsonSerializer.SerializeToNode(local,BridgeProtocol.Json)!.AsObject();
        Check(!localJson["resources"]![0]!.AsObject().ContainsKey("external"),"Absent extension preserves legacy serialized resource shape");
        string legacyResource=JsonSerializer.Serialize(local.Resources[0],BridgeProtocol.Json);var legacyExpected=JsonSerializer.Serialize(new{resourceKey=local.Resources[0].ResourceKey,kind="project",effect="synthetic project effect",recoveryStrategy="projectArchiveToNewLocation"},BridgeProtocol.Json);Check(legacyResource==legacyExpected,"Legacy resource bytes remain identical");
        var legacy=store.Prepare(local,"legacy");Check(legacy.PlanSha256==CanonicalJson.Sha256(localJson),"Legacy canonical plan hash unchanged");
        Reject(()=>store.Prepare(plan with{RequiredGates=["project.readback"]},"missing-gate"),"InvalidOperationPlan","Each external effect requires its own success gate");
        Reject(()=>store.Prepare(plan with{Resources=[plan.Resources[0],plan.Resources[1] with{External=null}]},"missing-identity"),"InvalidResourceKey","External key alone cannot invent an identity");
        Reject(()=>store.Prepare(plan with{Resources=[plan.Resources[0] with{External=effect}]},"local-external"),"InvalidResourceKey","Local resource cannot carry an external effect");
        Reject(()=>store.Prepare(plan with{Resources=[plan.Resources[0],plan.Resources[1] with{RecoveryStrategy="projectArchiveToNewLocation"}]},"wrong-strategy"),"InvalidOperationPlan","External state cannot use a project archive recovery strategy");
        Reject(()=>store.Prepare(plan with{Resources=[plan.Resources[0],plan.Resources[1] with{External=effect with{Before=new("unknown",before.Sha256)}}]},"unknown"),"ExternalEvidenceInvalid","Unknown external baseline cannot prepare dispatch");
        var replacement=effect with{Identity=identity with{OwnerId="bad\ufffd"}};var replacementPlan=Plan("unicode",replacement);replacementPlan=replacementPlan with{Resources=[replacementPlan.Resources[0],replacementPlan.Resources[1] with{External=replacement with{Identity=identity with{OwnerId="bad\ud800"}}}]};
        Reject(()=>store.Prepare(replacementPlan,"unicode"),"InvalidResourceKey","Invalid Unicode refused before JSON replacement can alias a key");
        var r=store.Prepare(plan,"main");Check(store.Prepare(plan,"main").Id==r.Id,"External plan idempotency preserved");r=store.Acquire(r.Id,r.Revision);
        var ownerPlan=Plan("owner",effect with{Identity=identity with{ResourceKind=null,ResourceId=null}});var owner=store.Prepare(ownerPlan,"owner");Check(Child("--operation-probe",root,owner.Id,"acquire")=="ResourceBusy","Separate process owner scope cannot steal a child reservation");
        var same=store.Prepare(Plan("same",effect),"same");Check(Child("--operation-probe",root,same.Id,"acquire")=="ResourceBusy","Same external resource conflicts across different projects");
        foreach(var peer in new[]{effect with{Identity=identity with{ResourceId="peer"}},effect with{Identity=identity with{Authority="other"}}}){var p=store.Prepare(Plan(Guid.NewGuid().ToString("N"),peer),Guid.NewGuid().ToString("N"));p=store.Acquire(p.Id,p.Revision);store.Stop(p.Id,p.Revision);Check(true,"Unrelated external resource does not conflict");}
        r=store.BeginCheckpoint(r.Id,r.Revision,()=>plan.Target);
        Reject(()=>store.Checkpoint(r.Id,r.Revision,[checkpoint,proof]),"ExternalEvidenceInvalid","Project checkpoint cannot stand in for an external baseline");
        foreach(var mutation in new (string Name,Action<JsonObject> Edit)[]{("resource",n=>n["resourceKey"]=EngineeringResourceKey.ForExternal(identity with{ResourceId="peer"})),("operation",n=>n["operationId"]=new string('a',32)),("plan",n=>n["planSha256"]=new string('f',64)),("revision",n=>n["revision"]=0),("phase",n=>n["phase"]="outcome"),("unknown",n=>n["state"]!["presence"]="unreadable"),("extra",n=>n["nativeQualified"]=true),("bytes",n=>n["stateArtifact"]!["sha256"]=after.Sha256)})
        {var bad=Observe(r,plan.Resources[1],"checkpoint",mutation.Edit);Reject(()=>store.Checkpoint(r.Id,r.Revision,[checkpoint,bad]),"ExternalEvidenceInvalid","Checkpoint refuses foreign "+mutation.Name);}
        foreach(string malformed in new[]{"null", "[]", "{\"schemaVersion\":null}", "{\"schemaVersion\":1}"})
            Reject(()=>store.Checkpoint(r.Id,r.Revision,[checkpoint,Save("malformed.json",Encoding.UTF8.GetBytes(malformed))]),"ExternalEvidenceInvalid","Malformed receipt is refused");
        var duplicateSource=Observe(r,plan.Resources[1],"checkpoint");
        string duplicateText=File.ReadAllText(Path.Combine(root,duplicateSource.RelativePath));
        duplicateText="{\"schemaVersion\":\"engineering-external-observation.v1\","+duplicateText[1..];
        Reject(()=>store.Checkpoint(r.Id,r.Revision,[checkpoint,Save("duplicate.json",Encoding.UTF8.GetBytes(duplicateText))]),"ExternalEvidenceInvalid","Duplicate receipt fields refused");
        var baseline=Observe(r,plan.Resources[1],"checkpoint");r=store.Checkpoint(r.Id,r.Revision,[checkpoint,baseline]);
        var baselineNode=JsonNode.Parse(File.ReadAllBytes(Path.Combine(root,baseline.RelativePath)))!;string statePath=Path.Combine(root,(string)baselineNode["stateArtifact"]!["relativePath"]!);byte[] stateBytes=File.ReadAllBytes(statePath);File.WriteAllText(statePath,"corrupt");Reject(()=>store.Read(r.Id),"ArtifactChanged","Reopen revalidates independent baseline bytes");File.WriteAllBytes(statePath,stateBytes);
        r=store.Dispatch(r.Id,r.Revision,()=>plan.Target);Child("--external-process",root,r.Id);Check(store.Read(r.Id).State=="dispatchIntent","Lost reply leaves journal pending");Check(Hash(File.ReadAllBytes(Path.Combine(root,"provider-state.json")))==after.Sha256,"Independent provider state changed despite lost reply");
        r=new EngineeringOperationStore(root).MarkUnknown(r.Id,r.Revision,proof);Check(Child("--operation-probe",root,same.Id,"acquire")=="ResourceBusy","Unknown external outcome retains ownership after restart");
        Reject(()=>store.Dispatch(r.Id,r.Revision,()=>plan.Target),"RecoveryRequired","Unknown external dispatch is never replayed");Reject(()=>store.Stop(r.Id,r.Revision),"RecoveryRequired","Stop does not erase external uncertainty");
        Reject(()=>store.ReserveRecovery(r.Id,r.Revision,[new(key,Path.Combine(root,"external-copy"))]),"InvalidRecovery","External state cannot be restored by a local destination");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Target.ProjectFilePath)!);File.Copy(Path.Combine(root,checkpoint.RelativePath),plan.Target.ProjectFilePath);
        Reject(()=>store.Reconcile(r.Id,r.Revision,[new(projectKey,"unchanged",null,proof)]),"ReconciliationIncomplete","Restoring a local project cannot discharge an external resource");
        foreach(string disposition in new[]{"unchanged","verifiedApplied","externalCommitRetained","restoredToNewLocation"})Reject(()=>store.Reconcile(r.Id,r.Revision,[new(projectKey,"unchanged",null,proof),new(key,disposition,null,proof)]),"InvalidRecovery","Generic disposition cannot discharge external obligation: "+disposition);
        var unreadable=Json("unreadable.json",new{state="unreadable",synthetic=true});r=store.RecordRecoveryEvidence(r.Id,r.Revision,new("external.unreadable",unreadable));Check(store.Pending().Any(x=>x.Id==r.Id),"Unreadable external diagnostics persist without releasing ownership");
        Reject(()=>store.Reconcile(r.Id,r.Revision,[new(projectKey,"unchanged",null,proof),new(key,"externalBaselineObserved",null,baseline)]),"ExternalEvidenceInvalid","A checkpoint is not a post-attempt readback");
        var current=Observe(r,plan.Resources[1],"reconciliation");Reject(()=>store.Reconcile(r.Id,r.Revision,[new(projectKey,"unchanged",null,proof),new(key,"externalBaselineObserved",null,current)]),"ExternalEvidenceInvalid","Desired state cannot masquerade as baseline restoration");
        Reject(()=>store.Reconcile(r.Id,r.Revision,[new(projectKey,"externalDesiredStateObserved",null,current),new(key,"externalDesiredStateObserved",null,current)]),"InvalidRecovery","External readback cannot discharge the local project");
        var reconciled=store.Reconcile(r.Id,r.Revision,[new(projectKey,"unchanged",null,proof),new(key,"externalDesiredStateObserved",null,current)]);Check(reconciled.State=="reconciled"&&reconciled.Evidence.Any(x=>x.Gate=="external.unreadable"),"Independent external readback reconciles while retaining failure history");Check(File.ReadAllLines(Path.Combine(root,"provider-calls.txt")).Length==1,"Exactly one provider mutation despite interruption and retry attempts");
        owner=store.Acquire(owner.Id,owner.Revision);Check(Child("--operation-probe",root,same.Id,"acquire")=="ResourceBusy","Owner reservation blocks child after previous reconciliation");store.Stop(owner.Id,owner.Revision);
        var successPlan=Plan("success",effect with{Before=effect.After,After=effect.Before});var success=store.Prepare(successPlan,"success");success=store.Acquire(success.Id,success.Revision);success=store.BeginCheckpoint(success.Id,success.Revision,()=>successPlan.Target);success=store.Checkpoint(success.Id,success.Revision,[checkpoint,Observe(success,successPlan.Resources[1],"checkpoint")]);success=store.Dispatch(success.Id,success.Revision,()=>successPlan.Target);
        File.Copy(Path.Combine(root,before.RelativePath),Path.Combine(root,"provider-state.json"),true);
        Reject(()=>store.RecordOutcome(success.Id,success.Revision,true,[new("project.readback",proof)]),"VerificationIncomplete","Project verification alone cannot mark external effects succeeded");
        Reject(()=>store.RecordOutcome(success.Id,success.Revision,true,[new("project.readback",proof),new(EngineeringExternalEvidence.OutcomeGate(key),proof)]),"ExternalEvidenceInvalid","A named external gate with project-only bytes is refused");
        var outcome=Observe(success,successPlan.Resources[1],"outcome");success=store.RecordOutcome(success.Id,success.Revision,true,[new("project.readback",proof),new(EngineeringExternalEvidence.OutcomeGate(key),outcome)]);Check(success.State=="succeeded","Exact external desired-state readback is required for success");
        var final=JsonSerializer.SerializeToNode(success,BridgeProtocol.Json)!.AsObject();Check(final["plan"]!["resources"]![1]!["external"] is JsonObject,"External identity and states are visible in retained status");
        byte[] oldOutcome=File.ReadAllBytes(Path.Combine(root,outcome.RelativePath));File.WriteAllText(Path.Combine(root,outcome.RelativePath),"corrupt");Reject(()=>store.Read(success.Id),"ArtifactChanged","Successful tail cannot hide corrupt external readback");File.WriteAllBytes(Path.Combine(root,outcome.RelativePath),oldOutcome);
        // A local recovery copy and an external absence observation discharge separate obligations.
        var absentBytes=Save("absent-state.json",Encoding.UTF8.GetBytes("{\"resource\":\"main\",\"observedAbsent\":true}"));
        var absentEffect=effect with{After=new("absent",absentBytes.Sha256)};
        var mixedPlan=Plan("mixed",absentEffect);var mixed=store.Prepare(mixedPlan,"mixed");mixed=store.Acquire(mixed.Id,mixed.Revision);
        mixed=store.BeginCheckpoint(mixed.Id,mixed.Revision,()=>mixedPlan.Target);mixed=store.Checkpoint(mixed.Id,mixed.Revision,[checkpoint,Observe(mixed,mixedPlan.Resources[1],"checkpoint")]);mixed=store.Dispatch(mixed.Id,mixed.Revision,()=>mixedPlan.Target);mixed=store.MarkUnknown(mixed.Id,mixed.Revision,unreadable);
        string copy=Path.Combine(root,"restored.ap21");string mixedProject=mixedPlan.Resources[0].ResourceKey;mixed=store.ReserveRecovery(mixed.Id,mixed.Revision,[new(mixedProject,copy)]);File.Copy(Path.Combine(root,checkpoint.RelativePath),copy);
        var localRestored=new ResourceReconciliation(mixedProject,"restoredToNewLocation",copy,proof);
        var destinationResolved=new EngineeringRecoveryResolution(mixedProject,copy,"verifiedRestored",proof);
        Reject(()=>store.Reconcile(mixed.Id,mixed.Revision,[localRestored],[destinationResolved]),"ReconciliationIncomplete","Completed project restoration still cannot discharge the external effect");
        Check(Child("--operation-probe",root,same.Id,"acquire")=="ResourceBusy","Restored local copy leaves external reservation held");
        File.Copy(Path.Combine(root,absentBytes.RelativePath),Path.Combine(root,"provider-state.json"),true);
        var absentReadback=Observe(mixed,mixedPlan.Resources[1],"reconciliation",n=>n["state"]!["presence"]="absent");
        mixed=store.Reconcile(mixed.Id,mixed.Revision,[localRestored,new(key,"externalDesiredStateObserved",null,absentReadback)],[destinationResolved]);Check(store.Read(mixed.Id).State=="reconciled","Independently observed absence and recovered local copy reconcile separately");
        var baselinePlan=Plan("baseline-recovery",effect);var baselineRecovery=store.Prepare(baselinePlan,"baseline-recovery");baselineRecovery=store.Acquire(baselineRecovery.Id,baselineRecovery.Revision);baselineRecovery=store.MarkUnknown(baselineRecovery.Id,baselineRecovery.Revision,unreadable);
        File.Copy(Path.Combine(root,before.RelativePath),Path.Combine(root,"provider-state.json"),true);
        var baselineReadback=Observe(baselineRecovery,baselinePlan.Resources[1],"reconciliation");baselineRecovery=store.Reconcile(baselineRecovery.Id,baselineRecovery.Revision,[new(baselinePlan.Resources[0].ResourceKey,"unchanged",null,proof),new(key,"externalBaselineObserved",null,baselineReadback)]);
        Check(store.Read(baselineRecovery.Id).State=="reconciled","Independent baseline readback resolves a lost checkpoint outcome");
        var pendingPlan=Plan("pending",effect);var pending=store.Prepare(pendingPlan,"pending");pending=store.Acquire(pending.Id,pending.Revision);pending=store.MarkUnknown(pending.Id,pending.Revision,unreadable);
        File.WriteAllText(Path.Combine(root,"mcp-fixture.json"),JsonSerializer.Serialize(new{artifactRoot=root,legacyId=legacy.Id,reconciledId=reconciled.Id,successId=success.Id,pendingId=pending.Id,externalKey=key,outcome,baseline},BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new{completed=true,nativeExecuted=false,count=checks.Count,checks},BridgeProtocol.Json));Console.WriteLine($"PASS External resource identities and journal recovery: {checks.Count} checks; separate-process synthetic provider only. Receipts retained at {root}");
    }
}
