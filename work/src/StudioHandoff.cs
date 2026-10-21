using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UsageWidget
{
    // Provider sessions are independent even when the UI presents one conversation.
    // AccountKey is an opaque local identity, never an access token or credential.
    internal sealed class StudioExecutionSession
    {
        public string Id { get; set; }
        public string Provider { get; set; }
        public string Profile { get; set; }
        public string AccountKey { get; set; }
        public string Model { get; set; }
        public string ThreadId { get; set; }
        public string DeliveryState { get; set; }
    }

    internal sealed class StudioHandoffRecord
    {
        public string Id { get; set; }
        public string SourceExecutionId { get; set; }
        public string TargetExecutionId { get; set; }
        public string ResumeExecutionId { get; set; }
        public string SourceHash { get; set; }
        public string TargetProvider { get; set; }
        public string TargetModel { get; set; }
        public List<string> SelectedMessageIds { get; set; }
        public string Payload { get; set; }
        public string PayloadHash { get; set; }
        public string ReviewHash { get; set; }
        public string State { get; set; }
        public string Created { get; set; }
    }

    internal static class StudioSessions
    {
        internal const int SchemaVersion=2;
        internal const int MaximumExecutions=40;
        internal static string NewId() { return Guid.NewGuid().ToString("N"); }
        internal static bool IsId(string value) { Guid id;return value!=null&&value.Length==32&&Guid.TryParseExact(value,"N",out id); }
        internal static bool IsProvider(string value) { return StudioProviderCatalog.IsValid(value); }
        private static void Limit(string value,int length) { if(value!=null&&value.Length>length)throw new InvalidDataException("Session metadata is too large."); }

        // Loading trusts the explicit active segment; saving copies the backwards-compatible
        // Model/ThreadId UI fields into it. Activating a segment never copies the old mirrors.
        internal static void Normalize(StudioSession session,bool afterRestart)
        {
            if(session==null||session.Messages==null||session.SchemaVersion>SchemaVersion)throw new InvalidDataException("Unsupported conversation format.");
            StudioRouteRecord.Validate(session.Route);
            if(!String.IsNullOrEmpty(session.PreferredProvider)&&!IsProvider(session.PreferredProvider))throw new InvalidDataException("Unsupported preferred provider.");
            if(session.ProviderSessions==null)session.ProviderSessions=new List<StudioExecutionSession>();
            if(session.Handoffs==null)session.Handoffs=new List<StudioHandoffRecord>();
            bool legacy=session.ProviderSessions.Count==0&&(!String.IsNullOrEmpty(session.ThreadId)||session.Messages.Count>0);
            if(legacy) {
                var first=new StudioExecutionSession{Id=NewId(),Provider="codex",Profile="ade-codex",Model=session.Model,ThreadId=session.ThreadId,DeliveryState="idle"};
                session.ProviderSessions.Add(first);session.ActiveExecutionId=first.Id;
            }
            if(session.ProviderSessions.Count>MaximumExecutions)throw new InvalidDataException("Too many execution sessions.");
            var executionIds=new HashSet<string>(StringComparer.Ordinal);
            foreach(var execution in session.ProviderSessions) {
                if(execution==null||!IsId(execution.Id)||!executionIds.Add(execution.Id)||!IsProvider(execution.Provider))throw new InvalidDataException("Invalid execution session.");
                Limit(execution.Profile,1024);Limit(execution.AccountKey,256);Limit(execution.Model,512);Limit(execution.ThreadId,512);
                if(String.IsNullOrEmpty(execution.DeliveryState))execution.DeliveryState="idle";
                if(execution.DeliveryState!="idle"&&execution.DeliveryState!="running"&&execution.DeliveryState!="awaitingVerification")throw new InvalidDataException("Invalid delivery state.");
                if(afterRestart&&execution.DeliveryState=="running")execution.DeliveryState="awaitingVerification";
            }
            var active=session.ProviderSessions.FirstOrDefault(x=>x.Id==session.ActiveExecutionId);
            if(active==null&&(session.ProviderSessions.Count>0||!String.IsNullOrEmpty(session.ActiveExecutionId)))throw new InvalidDataException("The active execution session is missing.");
            if(afterRestart&&active!=null) { session.Model=active.Model;session.ThreadId=active.ThreadId; }
            var messageIds=new HashSet<string>(StringComparer.Ordinal);
            foreach(var message in session.Messages) {
                if(message==null||(message.Role!="user"&&message.Role!="assistant"))throw new InvalidDataException("Invalid conversation message.");
                if(String.IsNullOrEmpty(message.Id))message.Id=NewId();
                if(!IsId(message.Id)||!messageIds.Add(message.Id))throw new InvalidDataException("Invalid conversation message identity.");
                if(String.IsNullOrEmpty(message.ExecutionId))message.ExecutionId=active.Id;
                var source=session.ProviderSessions.FirstOrDefault(x=>x.Id==message.ExecutionId);
                if(source==null)throw new InvalidDataException("Message execution session is missing.");
                if(String.IsNullOrEmpty(message.Provider))message.Provider=source.Provider;
                if(message.Provider!=source.Provider)throw new InvalidDataException("Message provider does not match its execution session.");
                if(afterRestart&&message.State=="running")source.DeliveryState="awaitingVerification";
            }
            StudioHandoff.ValidateRecords(session);
            session.SchemaVersion=SchemaVersion;
        }
        internal static StudioExecutionSession Active(StudioSession session)
        {
            if(session==null||session.Messages==null||session.SchemaVersion>SchemaVersion)throw new InvalidDataException("Unsupported conversation format.");
            // UI state reads are frequent; payload integrity belongs at save/load/transition
            // boundaries, not in every frame while an editable draft may be incomplete.
            if(session.SchemaVersion<SchemaVersion||session.ProviderSessions==null||session.Handoffs==null||(session.ProviderSessions.Count==0&&(!String.IsNullOrEmpty(session.ThreadId)||session.Messages.Count>0))||session.Messages.Any(x=>x==null||String.IsNullOrEmpty(x.Id)||String.IsNullOrEmpty(x.ExecutionId)||String.IsNullOrEmpty(x.Provider)))Normalize(session,false);
            var active=session.ProviderSessions.FirstOrDefault(x=>x.Id==session.ActiveExecutionId);
            if(active==null&&(session.ProviderSessions.Count>0||!String.IsNullOrEmpty(session.ActiveExecutionId)))throw new InvalidDataException("The active execution session is missing.");
            return active;
        }
        internal static StudioExecutionSession SyncActive(StudioSession session)
        {
            var active=Active(session);Limit(session.Model,512);Limit(session.ThreadId,512);
            if(active!=null) { active.Model=session.Model;active.ThreadId=session.ThreadId; }return active;
        }
        internal static StudioExecutionSession Start(StudioSession session,string provider,string profile,string model,string accountKey)
        {
            Normalize(session,false);SyncActive(session);
            if(!IsProvider(provider)||String.IsNullOrWhiteSpace(model))throw new InvalidDataException("Choose a valid provider and model.");
            if(session.ProviderSessions.Count>=MaximumExecutions)throw new InvalidDataException("Too many execution sessions. Start a new conversation.");
            Limit(profile,1024);Limit(model,512);Limit(accountKey,256);
            var next=new StudioExecutionSession{Id=NewId(),Provider=provider,Profile=profile,AccountKey=accountKey,Model=model,DeliveryState="idle"};
            session.ProviderSessions.Add(next);session.ActiveExecutionId=next.Id;session.Model=next.Model;session.ThreadId=null;return next;
        }
        internal static StudioExecutionSession Activate(StudioSession session,string executionId)
        {
            Normalize(session,false);SyncActive(session);
            var next=session.ProviderSessions.FirstOrDefault(x=>x.Id==executionId);
            if(next==null)throw new InvalidDataException("The execution session does not exist.");
            session.ActiveExecutionId=next.Id;session.Model=next.Model;session.ThreadId=next.ThreadId;return next;
        }
    }

    internal static class StudioHandoff
    {
        internal const int MaximumPayloadLength=32000;
        internal const int MaximumRecords=32;
        internal const int MaximumSelectedMessages=40;
        private static bool IsHash(string value) { return value!=null&&value.Length==64&&value.All(x=>(x>='0'&&x<='9')||(x>='a'&&x<='f')); }
        private static string Hash(string text) { using(var sha=SHA256.Create())return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text??String.Empty)).Select(x=>x.ToString("x2",CultureInfo.InvariantCulture))); }
        private static void Field(StringBuilder output,string text) { output.Append((text??String.Empty).Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text??String.Empty).Append(';'); }
        private static string ReviewHash(StudioHandoffRecord record)
        {
            var content=new StringBuilder();Field(content,record.Id);Field(content,record.SourceExecutionId);Field(content,record.SourceHash);Field(content,record.TargetProvider);Field(content,record.TargetModel);Field(content,record.PayloadHash);
            // Preserve hashes of existing records while binding new resume choices.
            if(!String.IsNullOrEmpty(record.ResumeExecutionId)){Field(content,"resume");Field(content,record.ResumeExecutionId);}
            foreach(var id in record.SelectedMessageIds??new List<string>())Field(content,id);return Hash(content.ToString());
        }

        // Handoffs and Updated are deliberately excluded: saving or preparing a handoff does
        // not invalidate it. Draft/model/project/message changes do invalidate the review.
        internal static string SourceHash(StudioSession session)
        {
            StudioSessions.Normalize(session,false);
            var active=StudioSessions.SyncActive(session);var content=new StringBuilder();
            Field(content,session.Id);Field(content,session.Project);Field(content,session.Draft);Field(content,session.Model);Field(content,session.ThreadId);
            if(active!=null) { Field(content,active.Id);Field(content,active.Provider);Field(content,active.Profile);Field(content,active.AccountKey);Field(content,active.Model);Field(content,active.ThreadId); }
            foreach(var message in session.Messages) { Field(content,message.Id);Field(content,message.Role);Field(content,message.Text);Field(content,message.State);Field(content,message.Model);Field(content,message.Provider);Field(content,message.ExecutionId); }
            return Hash(content.ToString());
        }
        internal static bool IsFresh(StudioSession session,StudioHandoffRecord record) { return record!=null&&record.SourceHash==SourceHash(session); }

        // Empty selection intentionally exports no messages. No files, environment variables,
        // credentials, or entire transcript are read or attached by this helper.
        internal static StudioHandoffRecord CreateDraft(StudioSession session,string targetProvider,string targetModel,IEnumerable<int> selectedMessageIndexes)
        {
            var active=StudioSessions.SyncActive(session);
            if(active==null)throw new InvalidDataException("Start a conversation before preparing a context transfer.");
            if(!StudioSessions.IsProvider(targetProvider)||String.IsNullOrWhiteSpace(targetModel)||targetModel.Length>512)throw new InvalidDataException("Choose a valid provider and model.");
            var indexes=(selectedMessageIndexes??Enumerable.Empty<int>()).Distinct().OrderBy(x=>x).ToList();
            if(indexes.Count>MaximumSelectedMessages||indexes.Any(x=>x<0||x>=session.Messages.Count))throw new InvalidDataException("Choose fewer valid messages.");
            var payload=new StringBuilder();
            payload.Append("# Context handoff\n\n## Goal\n\n## Constraints\n\n## Decisions\n\n## Results and remaining issues\n\n## Selected conversation excerpts\n");
            foreach(var index in indexes) {
                var message=session.Messages[index];
                payload.Append("\n[").Append(message.Provider).Append(" / ").Append(message.Role).Append("]\n").Append(message.Text).Append('\n');
                if(payload.Length>MaximumPayloadLength)throw new InvalidDataException("Selected context is too large. Choose fewer messages.");
            }
            payload.Append("\n## Next request\n\n");
            if(payload.Length>MaximumPayloadLength)throw new InvalidDataException("Selected context is too large. Choose fewer messages.");
            return new StudioHandoffRecord{Id=StudioSessions.NewId(),SourceExecutionId=active.Id,SourceHash=SourceHash(session),TargetProvider=targetProvider,TargetModel=targetModel,SelectedMessageIds=indexes.Select(x=>session.Messages[x].Id).ToList(),Payload=payload.ToString(),State="draft",Created=DateTime.UtcNow.ToString("o")};
        }
        // Preparation freezes a *copy*. Changing the editor's draft afterwards cannot change
        // the saved payload. Caller must persist before attempting any remote submission.
        internal static StudioHandoffRecord Prepare(StudioSession session,StudioHandoffRecord draft)
        {
            if(draft==null||draft.State!="draft"||!IsFresh(session,draft))throw new InvalidDataException("The conversation changed. Review the context again.");
            var existingIndex=session.Handoffs.FindIndex(x=>x.Id==draft.Id);
            if(existingIndex>=0&&session.Handoffs[existingIndex].State!="draft")throw new InvalidDataException("This context transfer is already prepared.");
            if(existingIndex<0&&session.Handoffs.Count>=MaximumRecords)throw new InvalidDataException("Too many context transfers. Start a new conversation.");
            var frozen=new StudioHandoffRecord{Id=draft.Id,SourceExecutionId=draft.SourceExecutionId,SourceHash=draft.SourceHash,TargetProvider=draft.TargetProvider,TargetModel=draft.TargetModel,ResumeExecutionId=draft.ResumeExecutionId,SelectedMessageIds=new List<string>(draft.SelectedMessageIds??new List<string>()),Payload=draft.Payload,PayloadHash=Hash(draft.Payload),State="prepared",Created=draft.Created};
            frozen.ReviewHash=ReviewHash(frozen);
            ValidateRecord(session,frozen);
            if(existingIndex>=0)session.Handoffs[existingIndex]=frozen;else session.Handoffs.Add(frozen);return frozen;
        }
        internal static void MarkAwaitingVerification(StudioHandoffRecord record) { Transition(record,"prepared","awaitingVerification"); }
        internal static void MarkCompleted(StudioHandoffRecord record,string targetExecutionId)
        {
            if(!StudioSessions.IsId(targetExecutionId))throw new InvalidDataException("Invalid target execution session.");
            Transition(record,"awaitingVerification","completed");record.TargetExecutionId=targetExecutionId;
        }
        internal static void MarkFinished(StudioHandoffRecord record,string state)
        {
            if(state!="completed"&&state!="interrupted"&&state!="failed")throw new InvalidDataException("Invalid confirmed context transfer result.");
            if(state=="completed"&&(record==null||!StudioSessions.IsId(record.TargetExecutionId)))throw new InvalidDataException("Completed context transfer has no target execution session.");
            Transition(record,"awaitingVerification",state);
        }
        internal static void MarkExported(StudioHandoffRecord record) { Transition(record,"prepared","exported"); }
        internal static void Cancel(StudioHandoffRecord record)
        {
            if(record==null||(record.State!="draft"&&record.State!="prepared"))throw new InvalidDataException("A submitted context transfer must be verified before retrying.");
            if(record.State=="draft") { record.PayloadHash=Hash(record.Payload);record.ReviewHash=ReviewHash(record); }
            record.State="cancelled";
        }
        private static void Transition(StudioHandoffRecord record,string from,string to)
        {
            if(record==null||record.State!=from||!IsHash(record.PayloadHash)||record.PayloadHash!=Hash(record.Payload)||!IsHash(record.ReviewHash)||record.ReviewHash!=ReviewHash(record))throw new InvalidDataException("The prepared context cannot be modified or submitted twice.");
            record.State=to;
        }
        internal static void ValidateRecords(StudioSession session)
        {
            if(session.Handoffs.Count>MaximumRecords)throw new InvalidDataException("Too many context transfers.");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var record in session.Handoffs) { ValidateRecord(session,record);if(!ids.Add(record.Id))throw new InvalidDataException("Duplicate context transfer."); }
        }
        private static void ValidateRecord(StudioSession session,StudioHandoffRecord record)
        {
            if(record==null||!StudioSessions.IsId(record.Id)||!IsHash(record.SourceHash)||!session.ProviderSessions.Any(x=>x.Id==record.SourceExecutionId)||!StudioSessions.IsProvider(record.TargetProvider)||String.IsNullOrWhiteSpace(record.TargetModel)||record.TargetModel.Length>512)throw new InvalidDataException("Invalid context transfer.");
            if(record.State!="draft"&&record.State!="prepared"&&record.State!="awaitingVerification"&&record.State!="completed"&&record.State!="interrupted"&&record.State!="failed"&&record.State!="exported"&&record.State!="cancelled")throw new InvalidDataException("Invalid context transfer state.");
            if((record.Payload!=null&&record.Payload.Length>MaximumPayloadLength)||(record.State!="draft"&&record.State!="cancelled"&&String.IsNullOrWhiteSpace(record.Payload)))throw new InvalidDataException("Context transfer must contain at most 32000 characters.");
            if(record.State!="draft"&&(!IsHash(record.PayloadHash)||record.PayloadHash!=Hash(record.Payload)||!IsHash(record.ReviewHash)||record.ReviewHash!=ReviewHash(record)))throw new InvalidDataException("Prepared context integrity check failed.");
            if(record.SelectedMessageIds==null||record.SelectedMessageIds.Count>MaximumSelectedMessages||record.SelectedMessageIds.Distinct().Count()!=record.SelectedMessageIds.Count||record.SelectedMessageIds.Any(x=>!session.Messages.Any(m=>m.Id==x)))throw new InvalidDataException("Invalid context message selection.");
            DateTime created;if(!DateTime.TryParse(record.Created,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out created))throw new InvalidDataException("Invalid context transfer timestamp.");
            if(!String.IsNullOrEmpty(record.TargetExecutionId)&&!session.ProviderSessions.Any(x=>x.Id==record.TargetExecutionId&&x.Provider==record.TargetProvider))throw new InvalidDataException("Invalid target execution session.");
            if(!String.IsNullOrEmpty(record.ResumeExecutionId)&&!session.ProviderSessions.Any(x=>x.Id==record.ResumeExecutionId&&x.Provider==record.TargetProvider&&x.Model==record.TargetModel))throw new InvalidDataException("Invalid requested resume session.");
            if(record.State=="completed"&&String.IsNullOrEmpty(record.TargetExecutionId))throw new InvalidDataException("Completed context transfer has no target execution session.");
        }
    }
}
