using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Storage DTO has no dependency on networking, routing execution or WPF.
    internal sealed class StudioRouteRecord
    {
        public string Engine {get;set;}
        public string Task {get;set;}
        public string Provider {get;set;}
        public string Model {get;set;}
        public string PromptHash {get;set;}
        public string CreatedUtc {get;set;}
        public double Probability {get;set;}
        public double? Confidence {get;set;}
        public double? Remaining {get;set;}
        public long DecisionMilliseconds {get;set;}
        public long? AnswerMilliseconds {get;set;}
        public long? InputTokens {get;set;}
        public string SelectionMode {get;set;}
        public string Workload {get;set;}
        public string AllowanceBand {get;set;}
        public string Intent {get;set;}
        public string PreferredProvider {get;set;}
        public string SelectionReason {get;set;}
        public int PoolCandidates {get;set;}
        public int WorkloadCandidates {get;set;}
        public int SelectedBandCandidates {get;set;}
        internal static string TaskForIntent(string intent)
        {
            switch(intent){case "debugging":case "coding":case "writing":case "general":return intent;case "code_analysis":case "text_analysis":return "analysis";case "technical_planning":case "general_planning":return "planning";default:return null;}
        }
        internal static void Validate(StudioRouteRecord value)
        {
            if(value==null)return;
            if(!new[]{"jev","openai-decisions","ms-decision"}.Contains(value.Engine)||!StudioProviderCatalog.IsValid(value.Provider)||String.IsNullOrWhiteSpace(value.Model)||value.Model.Length>200||value.Model.Any(Char.IsControl)||!new[]{"debugging","coding","analysis","writing","planning","general"}.Contains(value.Task)||value.PromptHash==null||value.PromptHash.Length!=64||value.PromptHash.Any(c=>!Uri.IsHexDigit(c))||value.Probability<0||value.Probability>1||Double.IsNaN(value.Probability)||Double.IsInfinity(value.Probability)||value.Confidence.HasValue&&(value.Confidence<0||value.Confidence>1||Double.IsNaN(value.Confidence.Value)||Double.IsInfinity(value.Confidence.Value))||value.DecisionMilliseconds<0||value.DecisionMilliseconds>600000||value.AnswerMilliseconds<0||value.InputTokens<0)throw new InvalidDataException("Invalid routing audit.");
            if(value.SelectionMode==null){if(value.Workload!=null||value.AllowanceBand!=null||value.PoolCandidates!=0||value.WorkloadCandidates!=0||value.SelectedBandCandidates!=0)throw new InvalidDataException("Invalid legacy routing audit.");}
            else if(!new[]{"task-policy-v2","profile-v2","task-affinity-v3"}.Contains(value.SelectionMode)||!new[]{"quick","balanced","deep"}.Contains(value.Workload)||(value.SelectionMode=="task-policy-v2"||value.SelectionMode=="task-affinity-v3")&&value.Workload!=(value.Task=="general"?"quick":value.Task=="planning"?"deep":"balanced")||!new[]{"healthy","unknown","low"}.Contains(value.AllowanceBand)||value.PoolCandidates<1||value.PoolCandidates>32||value.WorkloadCandidates<1||value.WorkloadCandidates>value.PoolCandidates||value.SelectedBandCandidates<1||value.SelectedBandCandidates>value.WorkloadCandidates)throw new InvalidDataException("Invalid routing selection audit.");
            if(value.SelectionMode=="task-affinity-v3"){
                if(TaskForIntent(value.Intent)!=value.Task||!new[]{"codex","claude"}.Contains(value.PreferredProvider)||!new[]{"task_preference","allowance_precedence","file_access_required","preferred_unavailable"}.Contains(value.SelectionReason))throw new InvalidDataException("Invalid task affinity audit.");
            }else if(value.Intent!=null||value.PreferredProvider!=null||value.SelectionReason!=null)throw new InvalidDataException("Unexpected task affinity audit.");
        }
    }
    internal sealed class StudioMessage
    {
        public string Id { get; set; }
        public string Role { get; set; }
        public string Text { get; set; }
        public string State { get; set; }
        public string Model { get; set; }
        public string Provider { get; set; }
        public string ExecutionId { get; set; }
    }
    internal sealed class StudioSession
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public bool TitleIsCustom { get; set; }
        public bool Archived { get; set; }
        public string Project { get; set; }
        public string ThreadId { get; set; }
        public string Model { get; set; }
        public string Updated { get; set; }
        public string PreferredProvider { get; set; }
        public string Draft { get; set; }
        public bool AutoRouting { get; set; }
        public StudioRouteRecord Route { get; set; }
        public List<StudioMessage> Messages { get; set; }
        public int SchemaVersion { get; set; }
        public string ActiveExecutionId { get; set; }
        public List<StudioExecutionSession> ProviderSessions { get; set; }
        public List<StudioHandoffRecord> Handoffs { get; set; }
        internal bool NeedsMigrationBackup { get; set; }
        public StudioSession() { Id = Guid.NewGuid().ToString("N"); Title = "New conversation"; Messages = new List<StudioMessage>(); ProviderSessions=new List<StudioExecutionSession>(); Handoffs=new List<StudioHandoffRecord>(); }
        public override string ToString() { return Title; }
    }
    internal sealed class StudioWorkspaceState
    {
        public string SelectedSessionId { get; set; }
        public bool SidebarCollapsed { get; set; }
        public bool DetailsOpen { get; set; }
        public bool? AnimationsEnabled { get; set; }
        public Dictionary<string,StudioViewBookmark> Views { get; set; }
        internal bool IsUnreadable { get; set; }
        public StudioWorkspaceState(){Views=new Dictionary<string,StudioViewBookmark>();}
    }
    internal sealed class StudioViewBookmark
    {
        public bool HasScroll {get;set;}
        public bool Following {get;set;}
        public double Offset {get;set;}
        public string Anchor {get;set;}
        public double AnchorOffset {get;set;}
        public int DraftSelectionStart {get;set;}
        public int DraftSelectionLength {get;set;}
        public double DraftVerticalOffset {get;set;}
        public List<string> FoldedCode {get;set;}
        public StudioViewBookmark(){Following=true;FoldedCode=new List<string>();}
    }
    internal sealed class StudioStore
    {
        internal static Action<string,string> AtomicReplaceOverride=null;
        private readonly string root;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nephylo.Studio.v1");
        private static readonly byte[] WorkspaceEntropy = Encoding.UTF8.GetBytes("Nephylo.Studio.Workspace.v1");
        internal StudioStore(string root) { this.root = Path.GetFullPath(root); }
        internal string CodexHome { get { return Path.Combine(root,"codex-home"); } }
        internal string ClaudeHome { get { return Path.Combine(root,"claude-workspace"); } }
        internal string GeminiCliHome { get { return Path.Combine(root,"gemini-cli"); } }
        internal string GeminiApiHome { get { return Path.Combine(root,"gemini-api"); } }
        internal string RoutingHome { get { return Path.Combine(root,"routing"); } }
        private void SafeRoot()
        {
            for (string p=root; !String.IsNullOrEmpty(p); p=Path.GetDirectoryName(p))
                if (Directory.Exists(p) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked storage is not supported.");
            Directory.CreateDirectory(root);
        }
        private string FileFor(string id)
        {
            Guid parsed; if (id == null || id.Length != 32 || !Guid.TryParseExact(id,"N",out parsed)) throw new InvalidDataException();
            return Path.Combine(root,id+".dat");
        }
        private string WorkspaceFile { get { return Path.Combine(root,"workspace.state"); } }
        private StudioWorkspaceState ReadWorkspace()
        {
            var file=WorkspaceFile;
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0 || new FileInfo(file).Length>2*1024*1024)throw new InvalidDataException();
            var raw=ProtectedData.Unprotect(File.ReadAllBytes(file),WorkspaceEntropy,DataProtectionScope.CurrentUser);
            if(raw.Length>1024*1024)throw new InvalidDataException();
            var state=new JavaScriptSerializer { MaxJsonLength=1024*1024,RecursionLimit=20 }.Deserialize<StudioWorkspaceState>(Encoding.UTF8.GetString(raw));
            ValidateWorkspace(state);
            return state;
        }
        static bool ValidPosition(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>=0&&value<=10000000;}
        void ValidateWorkspace(StudioWorkspaceState state)
        {
            if(state==null)throw new InvalidDataException();if(!String.IsNullOrEmpty(state.SelectedSessionId))FileFor(state.SelectedSessionId);
            if(state.Views==null)state.Views=new Dictionary<string,StudioViewBookmark>();if(state.Views.Count>100)throw new InvalidDataException();
            foreach(var entry in state.Views){FileFor(entry.Key);var view=entry.Value;
                if(view==null||!ValidPosition(view.Offset)||!ValidPosition(view.AnchorOffset)||!ValidPosition(view.DraftVerticalOffset)||view.DraftSelectionStart<0||view.DraftSelectionStart>32000||view.DraftSelectionLength<0||view.DraftSelectionLength>32000||view.Anchor!=null&&(view.Anchor.Length>100||view.Anchor.Any(Char.IsControl)))throw new InvalidDataException();
                if(view.FoldedCode==null)view.FoldedCode=new List<string>();if(view.FoldedCode.Count>64||view.FoldedCode.Any(s=>s==null||s.Length>100||s.Any(Char.IsControl)))throw new InvalidDataException();
            }
        }
        internal StudioWorkspaceState LoadWorkspace()
        {
            try {
                SafeRoot();
                return File.Exists(WorkspaceFile)?ReadWorkspace():new StudioWorkspaceState();
            } catch { return new StudioWorkspaceState { IsUnreadable=true }; }
        }
        internal void SaveWorkspace(string selectedId,bool sidebarCollapsed)
        {
            SafeRoot();StudioWorkspaceState state;try{state=File.Exists(WorkspaceFile)?ReadWorkspace():new StudioWorkspaceState();}catch(Exception e){throw new IOException("Workspace preferences could not be read. The original file was preserved.",e);}
            state.SelectedSessionId=String.IsNullOrEmpty(selectedId)?null:selectedId;state.SidebarCollapsed=sidebarCollapsed;SaveWorkspace(state);
        }
        internal void SaveWorkspace(StudioWorkspaceState state)
        {
            SafeRoot();
            ValidateWorkspace(state);
            // Preferences are separate from conversations: selecting an item must not change its recency.
            // Preserve unreadable preferences for recovery instead of replacing them with defaults.
            if(File.Exists(WorkspaceFile)) {
                try { ReadWorkspace(); }
                catch(Exception e) { throw new IOException("Workspace preferences could not be read. The original file was preserved.",e); }
            }
            var json=new JavaScriptSerializer { MaxJsonLength=1024*1024 }.Serialize(state);if(Encoding.UTF8.GetByteCount(json)>1024*1024)throw new InvalidDataException();
            var bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(json),WorkspaceEntropy,DataProtectionScope.CurrentUser);
            WriteAtomic(WorkspaceFile,bytes);
        }
        private static void WriteAtomic(string target,byte[] bytes)
        {
            var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { File.WriteAllBytes(temporary,bytes); if(File.Exists(target))ReplaceAtomicWithRetry(temporary,target);else File.Move(temporary,target); }
            // An ambiguous Windows replacement can remove the destination. Keep
            // the encrypted replacement for recovery if no destination remains.
            finally { if(File.Exists(temporary)&&File.Exists(target))File.Delete(temporary); }
        }
        internal static void ReplaceAtomicWithRetry(string temporary,string target)
        {
            // Windows 1175 leaves both original names intact. Retry that and
            // sharing/lock violations only while both files still exist. Never
            // turn an ambiguous partial replacement into an in-place overwrite.
            for(int attempt=0;;attempt++){
                try{if(AtomicReplaceOverride!=null)AtomicReplaceOverride(temporary,target);else File.Replace(temporary,target,null);return;}
                catch(IOException error){
                    int code=error.HResult&0xffff;
                    if(attempt>=3||(code!=32&&code!=33&&code!=1175)||!File.Exists(temporary)||!File.Exists(target)||(File.GetAttributes(temporary)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0)throw;
                    System.Threading.Thread.Sleep(20<<attempt);
                }
            }
        }
        internal List<StudioSession> Load(out int unreadable)
        {
            unreadable=0; SafeRoot(); var result=new List<StudioSession>();
            foreach (var f in Directory.GetFiles(root,"*.dat").OrderByDescending(File.GetLastWriteTimeUtc).Take(100)) {
                try {
                    if ((File.GetAttributes(f)&FileAttributes.ReparsePoint)!=0 || new FileInfo(f).Length>4*1024*1024) throw new InvalidDataException();
                    var raw=ProtectedData.Unprotect(File.ReadAllBytes(f),Entropy,DataProtectionScope.CurrentUser);
                    var session=new JavaScriptSerializer { MaxJsonLength=3*1024*1024 }.Deserialize<StudioSession>(Encoding.UTF8.GetString(raw));
                    if (session==null || FileFor(session.Id)!=f || session.Messages==null || session.Messages.Count>400) throw new InvalidDataException();
                    session.NeedsMigrationBackup=session.SchemaVersion<StudioSessions.SchemaVersion;
                    StudioSessions.Normalize(session,true);
                    foreach(var m in session.Messages) { if(m==null || (m.Role!="user"&&m.Role!="assistant"))throw new InvalidDataException(); if(m.State=="running")m.State="unconfirmed"; }
                    result.Add(session);
                } catch { unreadable++; } // Preserve corrupt/foreign-user files; never replace them with an empty history.
            }
            return result;
        }
        internal bool HasSaved(StudioSession session)
        {
            SafeRoot();return File.Exists(FileFor(session.Id));
        }
        internal void Save(StudioSession session)
        {
            SafeRoot(); var target=FileFor(session.Id);
            if(File.Exists(target)&&(File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0)throw new IOException();
            StudioSessions.Normalize(session,false);StudioSessions.SyncActive(session);
            if(session.Messages.Count>400)throw new InvalidDataException("Conversation is full. Start a new conversation.");
            session.Updated=DateTime.UtcNow.ToString("o");
            var json=new JavaScriptSerializer { MaxJsonLength=3*1024*1024 }.Serialize(session);
            if(Encoding.UTF8.GetByteCount(json)>2*1024*1024)throw new InvalidDataException("Conversation is full. Start a new conversation.");
            var bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(json),Entropy,DataProtectionScope.CurrentUser);
            // The first new-format save keeps the old encrypted file intact for recovery.
            // Merely opening an old conversation does not rewrite it or create a backup.
            if(session.NeedsMigrationBackup&&File.Exists(target)) {
                var backup=target+".legacy.bak";
                if(File.Exists(backup)) {
                    if((File.GetAttributes(backup)&FileAttributes.ReparsePoint)!=0)throw new IOException();
                } else File.Copy(target,backup,false);
            }
            WriteAtomic(target,bytes);
            session.NeedsMigrationBackup=false;
        }
    }
}
