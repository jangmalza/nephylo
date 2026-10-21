using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UsageWidget;

internal static class StudioNavigationTests
{
    private static int checks;
    private static void Check(bool condition,string label)
    {
        checks++;
        if(!condition)throw new Exception(label);
    }
    private static void WriteLegacy(string root,string id)
    {
        // This fixture deliberately lacks all newly added navigation metadata.
        string json="{\"Id\":\""+id+"\",\"Title\":\"Legacy title\",\"Project\":\"C:\\\\projects\\\\legacy\",\"ThreadId\":\"legacy-thread\",\"Model\":\"legacy-model\",\"Draft\":\"private unfinished input\",\"Updated\":\"2026-10-01T00:00:00Z\",\"Messages\":[{\"Role\":\"user\",\"Text\":\"Legacy question\",\"State\":\"completed\",\"Model\":\"legacy-model\"}]}";
        File.WriteAllBytes(Path.Combine(root,id+".dat"),ProtectedData.Protect(Encoding.UTF8.GetBytes(json),Encoding.UTF8.GetBytes("Nephylo.Studio.v1"),DataProtectionScope.CurrentUser));
    }
    private static void Storage(string root)
    {
        Directory.CreateDirectory(root);
        var store=new StudioStore(root);
        var state=store.LoadWorkspace();
        Check(state.SelectedSessionId==null&&!state.SidebarCollapsed&&!state.IsUnreadable,"Missing workspace starts with safe defaults");
        string id=Guid.NewGuid().ToString("N");
        WriteLegacy(root,id);
        int unreadable;
        var old=store.Load(out unreadable).Single();
        Check(unreadable==0&&!old.Archived&&!old.TitleIsCustom,"Legacy encrypted history loads without a metadata migration");
        Check(old.Draft=="private unfinished input"&&old.ThreadId=="legacy-thread"&&old.Messages.Single().Text=="Legacy question","Legacy draft, thread, and messages are retained");
        old.Title="Custom project conversation";old.TitleIsCustom=true;old.Archived=true;store.Save(old);
        var restored=store.Load(out unreadable).Single();
        Check(restored.Title==old.Title&&restored.TitleIsCustom&&restored.Archived,"Custom title and archive state round-trip");
        Check(restored.ThreadId==old.ThreadId&&restored.Draft==old.Draft&&restored.Model==old.Model,"Changing navigation metadata preserves execution context");
        var sessionFile=Path.Combine(root,id+".dat");
        var sessionBytes=File.ReadAllBytes(sessionFile);var sessionWrite=File.GetLastWriteTimeUtc(sessionFile);var updated=restored.Updated;
        store.SaveWorkspace(id,true);
        state=store.LoadWorkspace();
        Check(state.SelectedSessionId==id&&state.SidebarCollapsed&&!state.IsUnreadable,"Workspace selection and collapsed sidebar round-trip");
        var workspaceFile=Path.Combine(root,"workspace.state");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(workspaceFile)).Contains(id),"Workspace selection is encrypted");
        Check(File.ReadAllBytes(sessionFile).SequenceEqual(sessionBytes)&&File.GetLastWriteTimeUtc(sessionFile)==sessionWrite&&store.Load(out unreadable).Single().Updated==updated,"Navigation preferences do not change conversation recency or bytes");
        Check(store.Load(out unreadable).Count==1&&unreadable==0,"Workspace preferences are not treated as conversation history");
        store.SaveWorkspace(null,false);
        state=store.LoadWorkspace();
        Check(state.SelectedSessionId==null&&!state.SidebarCollapsed,"Empty selection is supported");
        store.SaveWorkspace(id,true);
        var workspaceBytes=File.ReadAllBytes(workspaceFile);
        bool refused=false;
        try{store.SaveWorkspace("../outside",false);}catch(InvalidDataException){refused=true;}
        Check(refused&&File.ReadAllBytes(workspaceFile).SequenceEqual(workspaceBytes),"Invalid selected ID cannot alter workspace preferences");
        bool lockRefused=false;
        using(var locked=new FileStream(workspaceFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            Check(store.LoadWorkspace().IsUnreadable,"Locked workspace reads report failure without exposing a partial selection");
            try{store.SaveWorkspace(null,false);}catch(IOException){lockRefused=true;}
        }
        Check(lockRefused&&File.ReadAllBytes(workspaceFile).SequenceEqual(workspaceBytes),"Locked workspace save preserves existing bytes");
        store.SaveWorkspace(null,false);
        Check(!store.LoadWorkspace().IsUnreadable,"Preference save recovers when the file lock is released");
        File.WriteAllText(workspaceFile,"corrupt preferences");
        state=store.LoadWorkspace();
        Check(state.IsUnreadable&&state.SelectedSessionId==null&&!state.SidebarCollapsed,"Corrupt preferences produce safe defaults and a failure signal");
        bool corruptRefused=false;
        try{store.SaveWorkspace(id,true);}catch(IOException){corruptRefused=true;}
        Check(corruptRefused&&File.ReadAllText(workspaceFile)=="corrupt preferences","Corrupt preferences are preserved even after a save attempt");
        Check(store.Load(out unreadable).Single().TitleIsCustom&&unreadable==0,"Corrupt preferences do not prevent loading conversations");
        restored.Archived=false;store.Save(restored);
        Check(!store.Load(out unreadable).Single().Archived,"Conversation save and unarchive still work with corrupt preferences");
        Check(Directory.GetFiles(root,"*.tmp").Length==0,"Atomic writes do not leave temporary files");
    }
    static void ViewPreferences(string root)
    {
        Directory.CreateDirectory(root);var store=new StudioStore(root);var id=Guid.NewGuid().ToString("N");WriteLegacy(root,id);
        var conversation=File.ReadAllBytes(Path.Combine(root,id+".dat"));
        var state=store.LoadWorkspace();Check(state.AnimationsEnabled==null&&state.Views.Count==0,"old or missing view settings retain Windows-governed animation defaults");
        state.SelectedSessionId=id;state.DetailsOpen=true;state.AnimationsEnabled=false;state.Views[id]=new StudioViewBookmark{HasScroll=true,Following=false,Offset=725,Anchor="message-id",AnchorOffset=25,DraftSelectionStart=7,DraftSelectionLength=3,DraftVerticalOffset=21};state.Views[id].FoldedCode.Add("message-id:1");store.SaveWorkspace(state);
        var restored=store.LoadWorkspace();var view=restored.Views[id];
        Check(!restored.IsUnreadable&&restored.DetailsOpen&&restored.AnimationsEnabled==false,"details and reduced-motion preference persist separately from conversations");
        Check(view.HasScroll&&!view.Following&&view.Offset==725&&view.AnchorOffset==25&&view.DraftSelectionStart==7&&view.DraftSelectionLength==3&&view.FoldedCode.Single()=="message-id:1","reading anchor, draft selection and code disclosure survive encrypted reload");
        store.SaveWorkspace(id,true);restored=store.LoadWorkspace();Check(restored.Views.Count==1&&restored.AnimationsEnabled==false&&restored.DetailsOpen,"legacy layout-save overload preserves all new view preferences");
        Check(File.ReadAllBytes(Path.Combine(root,id+".dat")).SequenceEqual(conversation),"view-only changes do not rewrite old conversation or draft bytes");
        var path=Path.Combine(root,"workspace.state");var bytes=File.ReadAllBytes(path);
        restored.Views[id].Offset=Double.NaN;bool rejected=false;try{store.SaveWorkspace(restored);}catch(InvalidDataException){rejected=true;}
        Check(rejected&&File.ReadAllBytes(path).SequenceEqual(bytes),"nonfinite bookmarks cannot overwrite valid preferences");restored=store.LoadWorkspace();restored.Views["../outside"]=new StudioViewBookmark();rejected=false;try{store.SaveWorkspace(restored);}catch(InvalidDataException){rejected=true;}
        Check(rejected&&File.ReadAllBytes(path).SequenceEqual(bytes),"view-state keys cannot escape the conversation-id boundary");
        restored=store.LoadWorkspace();restored.Views[id].FoldedCode=Enumerable.Repeat("code",65).ToList();rejected=false;try{store.SaveWorkspace(restored);}catch(InvalidDataException){rejected=true;}
        Check(rejected&&File.ReadAllBytes(path).SequenceEqual(bytes),"oversized fold metadata is rejected without replacing saved state");
    }
    public static int Main()
    {
        try {
            var output=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","..","outputs","studio-navigation-tests","fixture-"+Guid.NewGuid().ToString("N")));
            Storage(output);ViewPreferences(Path.Combine(output,"view-preferences"));
            Console.WriteLine("Studio navigation tests passed: "+checks);
            return 0;
        } catch(Exception e) { Console.Error.WriteLine(e);return 1; }
    }
}
