using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class StudioHandoffTests
{
    private static int count;
    private static void Check(bool value,string name) { count++;if(!value)throw new Exception(name); }
    private static void Invalid(Action action,string name) { bool rejected=false;try { action(); } catch(InvalidDataException) { rejected=true; }Check(rejected,name); }
    private static StudioSession Conversation(string project)
    {
        var session=new StudioSession{Project=project,Model="model-a",Draft="unsent source draft"};
        var execution=StudioSessions.Start(session,"codex","studio-codex","model-a","opaque-account-a");
        session.ThreadId="thread-a";StudioSessions.SyncActive(session);
        session.Messages.Add(new StudioMessage{Role="user",Text="SELECTED_USER_CONTENT",State="completed",Model="model-a",Provider="codex",ExecutionId=execution.Id});
        session.Messages.Add(new StudioMessage{Role="assistant",Text="EXCLUDED_PRIVATE_CONTENT",State="completed",Model="model-a",Provider="codex",ExecutionId=execution.Id});
        session.Messages.Add(new StudioMessage{Role="assistant",Text="SELECTED_ANSWER_CONTENT",State="completed",Model="model-a",Provider="codex",ExecutionId=execution.Id});
        return session;
    }
    private static void Legacy(string root)
    {
        var directory=Path.Combine(root,"legacy");Directory.CreateDirectory(directory);var id=Guid.NewGuid().ToString("N");var file=Path.Combine(directory,id+".dat");
        var legacy=new{Id=id,Title="old conversation",Project=root,Model="latest-model",ThreadId="existing-thread",Draft="old draft",Messages=new[]{new{Role="user",Text="old prompt",State="completed",Model="older-model"},new{Role="assistant",Text="old partial",State="running",Model="latest-model"}}};
        var original=ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(legacy)),Encoding.UTF8.GetBytes("Nephylo.Studio.v1"),DataProtectionScope.CurrentUser);File.WriteAllBytes(file,original);
        var store=new StudioStore(directory);int bad;var loaded=store.Load(out bad).Single();
        Check(bad==0&&loaded.SchemaVersion==2,"legacy format loads into new schema");
        Check(loaded.ProviderSessions.Count==1&&StudioSessions.Active(loaded).ThreadId=="existing-thread","legacy thread gets one execution");
        Check(loaded.Messages[0].Model=="older-model"&&loaded.Messages[1].Model=="latest-model","legacy individual model evidence remains unchanged");
        Check(loaded.Messages.All(x=>x.Provider=="codex"&&x.ExecutionId==loaded.ActiveExecutionId&&!String.IsNullOrEmpty(x.Id)),"legacy messages acquire provenance");
        Check(loaded.Messages[1].State=="unconfirmed"&&StudioSessions.Active(loaded).DeliveryState=="awaitingVerification","legacy in-flight work requires verification");
        Check(File.ReadAllBytes(file).SequenceEqual(original)&&!File.Exists(file+".legacy.bak"),"opening history makes no on-disk conversion");
        store.Save(loaded);Check(File.ReadAllBytes(file+".legacy.bak").SequenceEqual(original),"first conversion preserves encrypted legacy original");
        var again=store.Load(out bad).Single();Check(again.ProviderSessions.Count==1&&again.ActiveExecutionId==loaded.ActiveExecutionId,"repeated reload does not duplicate or change execution");
        Check(again.Messages.Select(x=>x.Id).SequenceEqual(loaded.Messages.Select(x=>x.Id)),"message identities persist after migration");
        again.Draft="new draft";store.Save(again);Check(File.ReadAllBytes(file+".legacy.bak").SequenceEqual(original),"later saves retain first legacy backup");
        Check(again.Model=="latest-model"&&again.ThreadId=="existing-thread","legacy model/thread mirrors retained");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("new draft"),"migrated data stays encrypted");
    }
    private static void Executions(string root)
    {
        var fresh=new StudioSession{Model="preselected-model"};Check(StudioSessions.Active(fresh)==null&&fresh.ProviderSessions.Count==0,"choosing a model does not create an execution");
        var store=new StudioStore(Path.Combine(root,"fresh"));store.Save(fresh);int bad;var reopened=store.Load(out bad).Single();Check(reopened.Model=="preselected-model"&&StudioSessions.Active(reopened)==null,"unstarted model survives without pinning a session");
        var source=Conversation(root);var codex=StudioSessions.Active(source);source.Model="changed-model";StudioSessions.SyncActive(source);Check(codex.Model=="changed-model","existing UI mirrors update active record");
        var claude=StudioSessions.Start(source,"claude","claude-api","claude-test","opaque-account-b");source.ThreadId="claude-thread";StudioSessions.SyncActive(source);
        Check(codex.ThreadId=="thread-a"&&source.ThreadId=="claude-thread"&&source.ProviderSessions.Count==2,"new provider keeps original thread");
        Check(source.Messages.All(x=>x.ExecutionId==codex.Id&&x.Provider=="codex"),"switching does not relabel original messages");
        source.Messages.Add(new StudioMessage{Role="assistant",Text="new provider answer",State="completed",ExecutionId=claude.Id});StudioSessions.SyncActive(source);
        Check(source.Messages.Last().Provider=="claude","new message provider derived from explicit execution");
        StudioSessions.Activate(source,codex.Id);Check(source.ThreadId=="thread-a"&&source.Model=="changed-model","returning restores exact original model and thread");
        Check(claude.ThreadId=="claude-thread"&&claude.Model=="claude-test","activation never overwrites target with departing mirror");
        Invalid(()=>StudioSessions.Activate(source,Guid.NewGuid().ToString("N")),"cannot activate unknown execution");
        Invalid(()=>StudioSessions.Start(source,"unsupported","x","model",null),"unsupported providers rejected");
        Invalid(()=>StudioSessions.Start(source,"claude","x",new string('x',513),null),"oversized model rejected");
        source.Messages.Last().Provider="codex";Invalid(()=>store.Save(source),"mismatched message provenance rejected");source.Messages.Last().Provider="claude";
        source.ProviderSessions.Add(codex);Invalid(()=>store.Save(source),"duplicate execution identity rejected");source.ProviderSessions.RemoveAt(source.ProviderSessions.Count-1);
        var full=new StudioSession();for(int i=0;i<StudioSessions.MaximumExecutions;i++)StudioSessions.Start(full,"codex","studio-codex","model",null);
        Invalid(()=>StudioSessions.Start(full,"codex","studio-codex","model",null),"execution count bounded");Check(full.ProviderSessions.Count==StudioSessions.MaximumExecutions,"rejected start does not append");
    }
    private static void Drafts(string root)
    {
        var session=Conversation(root);var blank=StudioHandoff.CreateDraft(session,"claude","claude-test",new int[0]);
        Check(!blank.Payload.Contains("SELECTED_")&&!blank.Payload.Contains("EXCLUDED_PRIVATE")&&!blank.Payload.Contains(session.Draft),"empty selection includes no transcript or unsent draft");
        Check(!blank.Payload.Contains(root)&&blank.Payload.Contains("## Goal")&&blank.Payload.Contains("## Next request"),"manual sections provided without filesystem path leakage");
        var draft=StudioHandoff.CreateDraft(session,"claude","claude-test",new[]{2,0,2});
        Check(draft.SelectedMessageIds.Count==2&&draft.SelectedMessageIds[0]==session.Messages[0].Id,"explicit selection is deduplicated in chronological order");
        Check(draft.Payload.Contains("SELECTED_USER_CONTENT")&&draft.Payload.Contains("SELECTED_ANSWER_CONTENT")&&!draft.Payload.Contains("EXCLUDED_PRIVATE_CONTENT"),"only explicitly selected messages included");
        Check(session.Handoffs.Count==0&&draft.State=="draft","opening review neither persists nor submits");
        draft.Payload="Manually reviewed goal, constraints, results, and next request.";var frozen=StudioHandoff.Prepare(session,draft);
        Check(session.Handoffs.Count==1&&frozen.State=="prepared"&&frozen.PayloadHash.Length==64,"preparation records exact reviewed payload");
        draft.Payload="EDITED AFTER REVIEW";draft.SelectedMessageIds.Clear();Check(frozen.Payload!="EDITED AFTER REVIEW"&&frozen.SelectedMessageIds.Count==2,"prepared copy is independent from editor");
        Check(StudioHandoff.IsFresh(session,frozen),"preparation does not invalidate source revision");session.Updated="changed timestamp";Check(StudioHandoff.IsFresh(session,frozen),"save timestamp excluded from semantic revision");
        Invalid(()=>StudioHandoff.Prepare(session,draft),"same review cannot prepare twice");
        Invalid(()=>StudioHandoff.CreateDraft(session,"codex","test",new[]{-1}),"negative selection rejected");
        Invalid(()=>StudioHandoff.CreateDraft(session,"codex","test",new[]{99}),"outside selection rejected");
        var fresh=new StudioSession{Model="model"};Invalid(()=>StudioHandoff.CreateDraft(fresh,"codex","test",new int[0]),"unstarted conversation cannot fake source execution");
        session.Messages[0].Text=new string('x',StudioHandoff.MaximumPayloadLength);Invalid(()=>StudioHandoff.CreateDraft(session,"codex","test",new[]{0}),"oversized selected context rejected without truncation");
        var another=Conversation(root);var longDraft=StudioHandoff.CreateDraft(another,"codex","test",null);longDraft.Payload=new string('x',StudioHandoff.MaximumPayloadLength+1);
        Invalid(()=>StudioHandoff.Prepare(another,longDraft),"oversized edited context rejected");Check(another.Handoffs.Count==0,"failed preparation appends nothing");
        longDraft.Payload=" ";Invalid(()=>StudioHandoff.Prepare(another,longDraft),"empty reviewed payload rejected");
    }
    private static void Revisions(string root)
    {
        var source=Conversation(root);var draft=StudioHandoff.CreateDraft(source,"claude","model",new[]{0});var baseline=draft.SourceHash;
        Action<Action,Action,string> verify=(change,restore,label)=>{change();Check(!StudioHandoff.IsFresh(source,draft),label);Invalid(()=>StudioHandoff.Prepare(source,draft),label+" blocks outdated prepare");restore();Check(StudioHandoff.SourceHash(source)==baseline,label+" restoration recovers semantic revision");};
        verify(()=>source.Project="other",()=>source.Project=root,"project change");
        verify(()=>source.Draft="changed",()=>source.Draft="unsent source draft","draft change");
        verify(()=>source.Model="other",()=>source.Model="model-a","model change");
        verify(()=>source.Messages[1].Text="new unseen result",()=>source.Messages[1].Text="EXCLUDED_PRIVATE_CONTENT","unselected message edit");
        verify(()=>source.Messages[0].State="interrupted",()=>source.Messages[0].State="completed","message state change");
        var original=StudioSessions.Active(source);var next=StudioSessions.Start(source,"claude","profile","other",null);Check(!StudioHandoff.IsFresh(source,draft),"execution change invalidates review");StudioSessions.Activate(source,original.Id);Check(StudioHandoff.IsFresh(source,draft),"return to unchanged source remains reviewable");
        Check(next.Id!=original.Id,"separate identities retained");
    }
    private static void DurableDraft(string root)
    {
        var source=Conversation(root);var store=new StudioStore(Path.Combine(root,"draft"));var draft=StudioHandoff.CreateDraft(source,"claude","model",new[]{0});
        source.Handoffs.Add(draft);draft.Payload="Goal: durable manually edited context";store.Save(source);
        int bad;var loaded=store.Load(out bad).Single();var restored=loaded.Handoffs.Single();
        Check(restored.State=="draft"&&restored.Payload==draft.Payload&&restored.PayloadHash==null,"editable handoff draft persists without implying submission");
        var before=File.ReadAllBytes(Path.Combine(root,"draft",source.Id+".dat"));var frozen=StudioHandoff.Prepare(loaded,restored);
        Check(loaded.Handoffs.Count==1&&loaded.Handoffs[0]==frozen&&frozen.State=="prepared","preparation replaces saved draft with frozen copy");
        Check(File.ReadAllBytes(Path.Combine(root,"draft",source.Id+".dat")).SequenceEqual(before),"preparation is memory only until caller saves");
        restored.Payload="editor changed old reference";Check(frozen.Payload!=restored.Payload,"restored editor reference cannot mutate frozen content");
        store.Save(loaded);Check(store.Load(out bad).Single().Handoffs.Single().Payload==frozen.Payload,"prepared replacement survives restart");
        Invalid(()=>StudioHandoff.Prepare(loaded,restored),"saved prepared draft cannot be prepared again");
        var cancelled=StudioHandoff.CreateDraft(loaded,"codex","model",null);loaded.Handoffs.Add(cancelled);StudioHandoff.Cancel(cancelled);store.Save(loaded);
        Check(store.Load(out bad).Single().Handoffs.Last().State=="cancelled","cancelled editable draft can be persisted");
        var empty=StudioHandoff.CreateDraft(loaded,"codex","model",null);loaded.Handoffs.Add(empty);empty.Payload="";store.Save(loaded);Check(store.Load(out bad).Single().Handoffs.Last().Payload=="","temporarily empty editor draft autosaves");
        Invalid(()=>StudioHandoff.Prepare(loaded,empty),"empty saved draft cannot be prepared");StudioHandoff.Cancel(empty);store.Save(loaded);Check(store.Load(out bad).Single().Handoffs.Last().State=="cancelled","empty editor can be discarded without blocking store");
        var full=Conversation(root);for(int i=0;i<StudioHandoff.MaximumRecords;i++)full.Handoffs.Add(StudioHandoff.CreateDraft(full,"codex","model",null));
        var fullPrepared=StudioHandoff.Prepare(full,full.Handoffs[0]);Check(full.Handoffs.Count==StudioHandoff.MaximumRecords&&fullPrepared.State=="prepared","full list may replace an existing draft without increasing count");
    }
    private static void Delivery(string root)
    {
        var session=Conversation(root);var store=new StudioStore(Path.Combine(root,"delivery"));
        var prepared=StudioHandoff.Prepare(session,StudioHandoff.CreateDraft(session,"codex","model-b",new[]{0}));store.Save(session);int bad;var reloaded=store.Load(out bad).Single();
        Check(reloaded.Handoffs.Single().State=="prepared","prepared handoff remains unsubmitted after restart");
        StudioHandoff.MarkAwaitingVerification(prepared);var target=StudioSessions.Start(session,"codex","studio-codex","model-b","opaque-account-a");prepared.TargetExecutionId=target.Id;target.DeliveryState="running";
        session.Messages.Add(new StudioMessage{Role="assistant",Text="partial response",Model="model-b",ExecutionId=target.Id,State="running"});store.Save(session);
        var raw=File.ReadAllBytes(Path.Combine(root,"delivery",session.Id+".dat"));Check(!Encoding.UTF8.GetString(raw).Contains("SELECTED_USER_CONTENT"),"context is DPAPI encrypted with conversation");
        reloaded=store.Load(out bad).Single();Check(reloaded.Handoffs.Single().State=="awaitingVerification"&&StudioSessions.Active(reloaded).DeliveryState=="awaitingVerification","uncertain delivery survives crash without auto success");
        Check(reloaded.Messages.Last().State=="unconfirmed","crashed assistant response preserves partial text with unknown outcome");
        Invalid(()=>StudioHandoff.MarkAwaitingVerification(prepared),"uncertain transmission cannot be automatically resubmitted");
        Invalid(()=>StudioHandoff.Cancel(prepared),"cannot cancel away unknown submission evidence");
        StudioHandoff.MarkCompleted(prepared,target.Id);target.DeliveryState="idle";session.Messages.Last().State="completed";store.Save(session);
        reloaded=store.Load(out bad).Single();Check(reloaded.Handoffs.Single().State=="completed"&&reloaded.Handoffs.Single().TargetExecutionId==target.Id,"completed handoff retains target identity");
        Invalid(()=>StudioHandoff.MarkCompleted(prepared,target.Id),"duplicate completion is rejected");
        var export=StudioHandoff.Prepare(session,StudioHandoff.CreateDraft(session,"claude","external-model",new int[0]));StudioHandoff.MarkExported(export);Check(export.State=="exported"&&export.TargetExecutionId==null,"clipboard export does not claim remote execution");
        var cancel=StudioHandoff.Prepare(session,StudioHandoff.CreateDraft(session,"claude","external-model",new int[0]));StudioHandoff.Cancel(cancel);Check(cancel.State=="cancelled","unsubmitted prepared transfer can be cancelled");
        var tamper=StudioHandoff.Prepare(session,StudioHandoff.CreateDraft(session,"codex","model",new int[0]));tamper.Payload+="silent mutation";
        Invalid(()=>StudioHandoff.MarkAwaitingVerification(tamper),"post-review payload mutation blocks submit");Invalid(()=>store.Save(session),"corrupted prepared record blocks persistence");session.Handoffs.Remove(tamper);
        var changedTarget=StudioHandoff.Prepare(session,StudioHandoff.CreateDraft(session,"codex","model",new int[0]));changedTarget.TargetModel="different-model";
        Invalid(()=>StudioHandoff.MarkAwaitingVerification(changedTarget),"changing model after review requires fresh preparation");Invalid(()=>store.Save(session),"changed reviewed target cannot be persisted");session.Handoffs.Remove(changedTarget);
        var before=File.ReadAllBytes(Path.Combine(root,"delivery",session.Id+".dat"));
        using(var locked=File.Open(Path.Combine(root,"delivery",session.Id+".dat"),FileMode.Open,FileAccess.Read,FileShare.Read)) {
            bool rejected=false;try { store.Save(session); } catch(IOException) { rejected=true; }Check(rejected,"locked history fails without in-place overwrite");
        }
        Check(File.ReadAllBytes(Path.Combine(root,"delivery",session.Id+".dat")).SequenceEqual(before),"failed save preserves prior conversation bytes");store.Save(session);Check(store.Load(out bad).Single().Handoffs.Count==3,"retry after file lock preserves valid transfer records");
        foreach(var terminal in new[]{"completed","interrupted","failed"}) {
            var finish=StudioHandoff.Prepare(session,StudioHandoff.CreateDraft(session,"codex","model-b",null));finish.TargetExecutionId=target.Id;StudioHandoff.MarkAwaitingVerification(finish);StudioHandoff.MarkFinished(finish,terminal);
            Check(finish.State==terminal,"confirmed terminal result: "+terminal);Invalid(()=>StudioHandoff.MarkAwaitingVerification(finish),"terminal result never auto resubmits: "+terminal);
        }
        store.Save(session);Check(store.Load(out bad).Single().Handoffs.Skip(3).Select(x=>x.State).SequenceEqual(new[]{"completed","interrupted","failed"}),"confirmed terminal states round trip");
    }
    private static void Limits(string root)
    {
        var source=Conversation(root);for(int i=0;i<StudioHandoff.MaximumRecords;i++)StudioHandoff.Prepare(source,StudioHandoff.CreateDraft(source,"codex","model",null));
        Invalid(()=>StudioHandoff.Prepare(source,StudioHandoff.CreateDraft(source,"codex","model",null)),"handoff record count bounded");Check(source.Handoffs.Count==StudioHandoff.MaximumRecords,"full record list unchanged by rejection");
        var selection=Conversation(root);for(int i=3;i<=StudioHandoff.MaximumSelectedMessages;i++)selection.Messages.Add(new StudioMessage{Role="user",Text="short",State="completed"});
        Invalid(()=>StudioHandoff.CreateDraft(selection,"codex","model",Enumerable.Range(0,StudioHandoff.MaximumSelectedMessages+1)),"selection count bounded");
        var store=new StudioStore(Path.Combine(root,"future"));var future=new StudioSession{SchemaVersion=99};Invalid(()=>store.Save(future),"future schemas never overwritten by old code");
        var mismatch=Conversation(root);var rec=StudioHandoff.Prepare(mismatch,StudioHandoff.CreateDraft(mismatch,"claude","model",null));StudioHandoff.MarkAwaitingVerification(rec);StudioHandoff.MarkCompleted(rec,mismatch.ActiveExecutionId);Invalid(()=>store.Save(mismatch),"target provider mismatch rejected");
    }
    private static void AtomicReplacementRetries(string root)
    {
        var store=new StudioStore(Path.Combine(root,"atomic-retry"));var session=Conversation(root);store.Save(session);
        string file=Path.Combine(root,"atomic-retry",session.Id+".dat");int attempts=0;
        try{
            StudioStore.AtomicReplaceOverride=(source,target)=>{attempts++;if(attempts<4)throw new IOException("Synthetic transient replacement failure",unchecked((int)0x80070497));File.Replace(source,target,null);};
            session.Draft="saved after transient replacement";store.Save(session);int unreadable;
            Check(attempts==4&&store.Load(out unreadable).Single().Draft==session.Draft,"bounded retry commits same encrypted payload after Windows 1175");
            var before=File.ReadAllBytes(file);attempts=0;
            StudioStore.AtomicReplaceOverride=(source,target)=>{attempts++;throw new IOException("Synthetic held replacement",unchecked((int)0x80070497));};
            session.Draft="must not replace original";bool rejected=false;try{store.Save(session);}catch(IOException){rejected=true;}Check(rejected,"persistent replacement failure remains visible");
            Check(attempts==4&&File.ReadAllBytes(file).SequenceEqual(before),"bounded failure preserves original conversation bytes");
            attempts=0;StudioStore.AtomicReplaceOverride=(source,target)=>{attempts++;throw new IOException("Synthetic ambiguous replacement",unchecked((int)0x80070498));};
            rejected=false;try{store.Save(session);}catch(IOException){rejected=true;}Check(rejected,"ambiguous replacement failures are not retried");Check(attempts==1&&File.ReadAllBytes(file).SequenceEqual(before),"unsafe replacement status is not masked or overwritten");
            StudioStore.AtomicReplaceOverride=(source,target)=>{File.Delete(target);throw new IOException("Synthetic missing destination",unchecked((int)0x80070498));};
            rejected=false;try{store.Save(session);}catch(IOException){rejected=true;}
            var recovery=Directory.GetFiles(Path.GetDirectoryName(file),"*.tmp");
            Check(rejected&&!File.Exists(file)&&recovery.Length==1,"missing replacement destination retains encrypted recovery data");
            var recovered=ProtectedData.Unprotect(File.ReadAllBytes(recovery[0]),Encoding.UTF8.GetBytes("Nephylo.Studio.v1"),DataProtectionScope.CurrentUser);
            Check(Encoding.UTF8.GetString(recovered).Contains(session.Draft),"retained replacement contains the intended draft rather than empty history");
        }finally{StudioStore.AtomicReplaceOverride=null;}
    }
    public static int Main()
    {
        var root=Path.GetFullPath(Path.Combine("outputs","studio-handoff-tests",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try { Legacy(root);Executions(root);Drafts(root);Revisions(root);AtomicReplacementRetries(root);DurableDraft(root);Delivery(root);Limits(root);Console.WriteLine("PASS studio-handoff-tests: "+count+" assertions; fixture="+root);return 0; }
        catch(Exception error) { Console.Error.WriteLine(error.ToString());return 1; }
    }
}
