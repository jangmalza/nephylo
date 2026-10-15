using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UsageWidget;

public static class ProfileMigrationTests
{
    private static int passed;
    private static string root;
    private static Exception lastFixtureStorageFailure;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    private static void Write(string directory, string name, string value) { string path = Path.Combine(directory,name); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path,value,new UTF8Encoding(false)); }
    private static string NewCase(string name) { string value=Path.Combine(root,name); Directory.CreateDirectory(value); return value; }
    private static void Initialize(string destination,string legacy)
    {
        lastFixtureStorageFailure = null;
        WidgetDataProfile.InitializeForTests(destination,legacy,null,null);
    }
    private static ProfileInitializationException Reject(Action action,string reason)
    {
        try { action(); } catch (ProfileInitializationException error) { Check(error.Reason == reason,"Expected " + reason + ", got " + error.Reason); Check(!String.IsNullOrWhiteSpace(error.EnglishMessage),"English recovery message missing"); return error; }
        throw new Exception("Expected rejected profile: " + reason);
    }
    private static bool NoStaging(string caseRoot) { return Directory.GetDirectories(caseRoot,"*.migrating-*").Length == 0; }
    private static void CopyBaseline(string legacy)
    {
        Write(legacy,"settings.json","{\"Language\":\"en\"}");
        Write(legacy,"extra-settings.json","{\"Connected\":true}");
        Write(legacy,"higgsfield-settings.json","{\"WebConnected\":true}");
        Write(legacy,"plan-settings.json","{\"selected\":\"eleven\"}");
        Write(legacy,"membership-session.dat","protected-session-account-A");
        Write(legacy,"elevenlabs-key.dat","protected-key-A");
        Write(legacy,"openai-admin-key.dat","protected-openai-A");
        Write(legacy,"usage-journal.json","{\"history\":[]}");
        Write(legacy,"claude-usage.json","{\"credit\":80}");
        Write(legacy,"claude-usage-" + new String('a',64) + ".json","{\"source\":\"wsl\"}");
        Write(legacy,"claude-usage-win-" + new String('b',64) + ".json","{\"source\":\"windows\"}");
        Write(legacy,"claude-hook-" + new String('c',64) + ".json","{\"state\":\"installed\"}");
        Write(legacy,"update-attempt.json","{\"Version\":\"1.25.7\"}");
        Write(legacy,Path.Combine("tools","higgsfield.exe"),"fixture CLI bytes");
    }

    public static int Main()
    {
        AppDomain.CurrentDomain.FirstChanceException += (sender,args) => {
            Exception error = args.Exception;
            if ((error is IOException && !(error is FileNotFoundException) && !(error is DirectoryNotFoundException)) || error is UnauthorizedAccessException)
                lastFixtureStorageFailure = error;
        };
        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".nephylo-profile-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string declared=WidgetDataProfile.DataDirectory;
            Check(declared==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".nephylo"),"Canonical data must live outside virtualized AppData");
            Check(WidgetDataProfile.LegacyInstallationDirectory==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UsageWidget"),"Legacy install location unchanged");

            string example=NewCase("full"), legacy=Path.Combine(example,"legacy"), destination=Path.Combine(example,"canonical");
            CopyBaseline(legacy);
            Write(legacy,Path.Combine("App","UsageWidget.exe"),"must not migrate");
            Write(legacy,Path.Combine("Updates","update.zip"),"must not migrate");
            Write(legacy,Path.Combine("tools","untrusted.exe"),"must not migrate");
            Write(legacy,"auth.json","must not migrate");
            Write(legacy,"settings.json.tmp","must not migrate");
            Write(legacy,"claude-usage-INVALID.json","must not migrate");
            var before=Directory.GetFiles(legacy,"*",SearchOption.AllDirectories).ToDictionary(x=>x,x=>File.ReadAllBytes(x));
            Initialize(destination,legacy);
            Check(File.ReadAllText(Path.Combine(destination,WidgetDataProfile.MarkerName))==WidgetDataProfile.MarkerContents,"Commit marker present");
            foreach (string name in new [] { "settings.json","extra-settings.json","higgsfield-settings.json","plan-settings.json","elevenlabs-key.dat","openai-admin-key.dat","usage-journal.json","claude-usage.json","claude-usage-"+new String('a',64)+".json","claude-usage-win-"+new String('b',64)+".json","claude-hook-"+new String('c',64)+".json","update-attempt.json",Path.Combine("tools","higgsfield.exe") })
                Check(File.ReadAllBytes(Path.Combine(destination,name)).SequenceEqual(File.ReadAllBytes(Path.Combine(legacy,name))),"Byte-preserving migration: " + name);
            Check(Directory.GetFiles(destination,"*",SearchOption.AllDirectories).Length==14,"Only active allowlisted files and marker migrated");
            Check(!Directory.Exists(Path.Combine(destination,"App")) && !Directory.Exists(Path.Combine(destination,"Updates")),"Payloads remain outside profile");
            foreach (var file in before) Check(File.ReadAllBytes(file.Key).SequenceEqual(file.Value),"Source remains unchanged");
            Check(NoStaging(example),"Successful migration leaves no staging tree");
            Check(WidgetDataProfile.ReadRecoveryNoticeFrom(destination, false) == null,"Valid profile needs no recovery warning");

            // Damaged derived data must not turn intact service connections into a startup failure.
            string[] cacheNames = { "claude-usage.json", "update-attempt.json", "usage-journal.json",
                "claude-usage-" + new String('a',64) + ".json", "claude-usage-win-" + new String('b',64) + ".json",
                "claude-hook-" + new String('c',64) + ".json" };
            foreach (string cacheName in cacheNames)
            {
                foreach (string badValue in new [] { "", "{SECRET_TOKEN PRIVATE_EMAIL", "null", "[]" })
                {
                    string recoveryCase = NewCase("recover-" + Guid.NewGuid().ToString("N"));
                    string recoveryLegacy = Path.Combine(recoveryCase,"legacy"), recoveryDestination = Path.Combine(recoveryCase,"canonical");
                    CopyBaseline(recoveryLegacy); Write(recoveryLegacy,cacheName,badValue);
                    Initialize(recoveryDestination,recoveryLegacy);
                    Check(!File.Exists(Path.Combine(recoveryDestination,"membership-session.dat")) && File.ReadAllText(Path.Combine(recoveryLegacy,"membership-session.dat"))=="protected-session-account-A","Obsolete session remains intact in legacy storage without being imported");
                    Check(File.ReadAllText(Path.Combine(recoveryDestination,"elevenlabs-key.dat"))=="protected-key-A","Invalid optional data preserves API key cohort");
                    Check(!File.Exists(Path.Combine(recoveryDestination,cacheName)),"Invalid optional file does not become active canonical data");
                    Check(File.ReadAllText(Path.Combine(recoveryLegacy,cacheName))==badValue,"Damaged original remains unchanged for recovery");
                    string notice=WidgetDataProfile.ReadRecoveryNoticeFrom(recoveryDestination,false);
                    string english=WidgetDataProfile.ReadRecoveryNoticeFrom(recoveryDestination,true);
                    Check(!String.IsNullOrWhiteSpace(notice) && !String.IsNullOrWhiteSpace(english),"Migration recovery has localized guidance");
                    string report=File.ReadAllText(Path.Combine(recoveryDestination,WidgetDataProfile.RecoveryNoticeName));
                    Check(report.Length<128 && !report.Contains("SECRET") && !report.Contains("PRIVATE") && !report.Contains(cacheName) && !report.Contains(recoveryLegacy),"Report stores bounded categories only, not filenames or secrets");
                    Check(english.Contains(cacheName=="usage-journal.json" ? "history" : "cache"),"Guidance identifies cache versus history recovery");
                    File.Delete(Path.Combine(recoveryDestination,"membership-session.dat")); Initialize(recoveryDestination,recoveryLegacy);
                    Check(!File.Exists(Path.Combine(recoveryDestination,"membership-session.dat")) && !File.Exists(Path.Combine(recoveryDestination,cacheName)),"Recovery does not reimport logged out session or bad cache on next launch");
                    Check(WidgetDataProfile.ReadRecoveryNoticeFrom(recoveryDestination,true)==english && NoStaging(recoveryCase),"Recovery guidance persists across launch without partial staging");
                }
            }
            string oversizeCase=NewCase("recover-oversized"), oversizeLegacy=Path.Combine(oversizeCase,"legacy"), oversizeDestination=Path.Combine(oversizeCase,"canonical");
            CopyBaseline(oversizeLegacy); Write(oversizeLegacy,"update-attempt.json",new String('x',4097));
            using (var oversizedHistory=new FileStream(Path.Combine(oversizeLegacy,"usage-journal.json"),FileMode.Create,FileAccess.Write)) oversizedHistory.SetLength(16777217);
            Initialize(oversizeDestination,oversizeLegacy);
            Check(!File.Exists(Path.Combine(oversizeDestination,"update-attempt.json")) && !File.Exists(Path.Combine(oversizeDestination,"usage-journal.json")),"Oversized optional files are skipped without copying");
            Check(new FileInfo(Path.Combine(oversizeLegacy,"usage-journal.json")).Length==16777217 && new FileInfo(Path.Combine(oversizeLegacy,"update-attempt.json")).Length==4097,"Oversized original files are retained");
            Check(File.ReadAllText(Path.Combine(oversizeDestination,WidgetDataProfile.RecoveryNoticeName))=="Nephylo recovery 1\n1\n1\n","Combined cache and history report is fixed and minimal");
            Write(oversizeDestination,"usage-journal.json","{\"newHistory\":true}");
            Check(new FileInfo(Path.Combine(oversizeLegacy,"usage-journal.json")).Length==16777217,"New history never overwrites damaged original");
            Write(oversizeDestination,WidgetDataProfile.RecoveryNoticeName,"Nephylo recovery 1\n999999999\n1\n");
            Check(WidgetDataProfile.ReadRecoveryNoticeFrom(oversizeDestination,false)==null,"Malformed recovery report cannot inject user-facing content");

            string invalidUtfCase=NewCase("recover-invalid-utf8"), invalidUtfLegacy=Path.Combine(invalidUtfCase,"legacy"), invalidUtfDestination=Path.Combine(invalidUtfCase,"canonical");
            CopyBaseline(invalidUtfLegacy); File.WriteAllBytes(Path.Combine(invalidUtfLegacy,"claude-usage.json"),new byte[] { 0x7b, 0xff, 0x7d });
            Initialize(invalidUtfDestination,invalidUtfLegacy);
            Check(!File.Exists(Path.Combine(invalidUtfDestination,"claude-usage.json")),"Invalid UTF-8 optional file does not block startup");
            WidgetDataProfile.AcknowledgeRecoveryNoticeAt(invalidUtfDestination);
            WidgetDataProfile.AcknowledgeRecoveryNoticeAt(invalidUtfDestination);
            Check(WidgetDataProfile.ReadRecoveryNoticeFrom(invalidUtfDestination,false)==null,"Acknowledged recovery warning is removed idempotently");
            Check(File.Exists(Path.Combine(invalidUtfDestination,"elevenlabs-key.dat")) && File.Exists(Path.Combine(invalidUtfLegacy,"claude-usage.json")),"Acknowledging recovery preserves service credentials and damaged original");

            string failedRecovery=NewCase("recovery-rollback"), failedLegacy=Path.Combine(failedRecovery,"legacy"), failedDestination=Path.Combine(failedRecovery,"canonical");
            CopyBaseline(failedLegacy); Write(failedLegacy,"claude-usage.json","");
            Reject(()=>WidgetDataProfile.InitializeForTests(failedDestination,failedLegacy,null,name=>{ throw new IOException("fixture failure"); }),"MigrationFailed");
            Check(!Directory.Exists(failedDestination) && NoStaging(failedRecovery),"Later mandatory copy failure rolls back recovered cohort too");
            Check(File.ReadAllText(Path.Combine(failedLegacy,"membership-session.dat"))=="protected-session-account-A" && new FileInfo(Path.Combine(failedLegacy,"claude-usage.json")).Length==0,"Recovery rollback leaves all originals unchanged");

            // Logout and deliberate disconnect must not resurrect credentials from the legacy backup.
            File.Delete(Path.Combine(destination,"membership-session.dat"));
            File.Delete(Path.Combine(destination,"elevenlabs-key.dat"));
            Write(destination,"settings.json","{\"new\":true}");
            Write(legacy,"membership-session.dat","stale-account-B");
            Initialize(destination,legacy);
            Check(!File.Exists(Path.Combine(destination,"membership-session.dat")),"Logout remains logged out");
            Check(!File.Exists(Path.Combine(destination,"elevenlabs-key.dat")),"Disconnected API key is not restored");
            Check(File.ReadAllText(Path.Combine(destination,"settings.json"))=="{\"new\":true}","Canonical settings win as one cohort");
            WidgetDataProfile.InitializeForTests(destination,legacy,path=>path.StartsWith(legacy,StringComparison.OrdinalIgnoreCase)?Path.Combine(example,"shadow"):WidgetDataProfile.ResolvePhysicalPath(path),null);
            Check(!File.Exists(Path.Combine(destination,"membership-session.dat")),"Initialized canonical is authoritative even under redirected launcher");

            example=NewCase("dpapi-preservation"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); Directory.CreateDirectory(legacy);
            byte[] fixtureSecret=Encoding.UTF8.GetBytes("synthetic-provider-token-no-real-credentials");
            byte[] fixtureEntropy=Encoding.UTF8.GetBytes("isolated-provider-entropy");
            byte[] protectedFixture=ProtectedData.Protect(fixtureSecret,fixtureEntropy,DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(legacy,"elevenlabs-key.dat"),protectedFixture); Initialize(destination,legacy);
            byte[] recovered=ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(destination,"elevenlabs-key.dat")),fixtureEntropy,DataProtectionScope.CurrentUser);
            Check(recovered.SequenceEqual(fixtureSecret),"Moving profile keeps Windows DPAPI provider credentials decryptable for the same user");
            Array.Clear(fixtureSecret,0,fixtureSecret.Length); Array.Clear(recovered,0,recovered.Length);

            example=NewCase("first-free"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical");
            Initialize(destination,legacy);
            Check(Directory.GetFiles(destination).Length==1,"Fresh Free profile contains only marker");
            Check(!Directory.Exists(legacy),"Fresh launch does not create legacy data folder");
            CopyBaseline(legacy); Initialize(destination,legacy);
            Check(!File.Exists(Path.Combine(destination,"membership-session.dat")),"Later legacy account never replaces initialized Free account");

            example=NewCase("redirected"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy);
            Reject(()=>WidgetDataProfile.InitializeForTests(destination,legacy,path=>path.StartsWith(legacy,StringComparison.OrdinalIgnoreCase)?Path.Combine(example,"Package","LocalCache","Local","UsageWidget"):WidgetDataProfile.ResolvePhysicalPath(path),null),"RedirectedLegacyProfile");
            Check(!Directory.Exists(destination) && NoStaging(example),"Redirected first launch does not initialize or import shadow session");
            Check(File.Exists(Path.Combine(legacy,"membership-session.dat")),"Redirected source preserved");
            example=NewCase("explicit-package"); legacy=Path.Combine(example,@"AppData\Local\Packages\Codex\LocalCache\Local\UsageWidget"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy);
            Reject(()=>Initialize(destination,legacy),"RedirectedLegacyProfile");
            Check(!Directory.Exists(destination),"Explicit package data cannot become canonical");

            example=NewCase("unmarked"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy); Write(destination,"membership-session.dat","existing-canonical-account-C");
            Reject(()=>Initialize(destination,legacy),"UninitializedProfile");
            Check(File.ReadAllText(Path.Combine(destination,"membership-session.dat"))=="existing-canonical-account-C","Unmarked existing data is never overwritten");
            Write(destination,WidgetDataProfile.MarkerName,"wrong version"); Reject(()=>Initialize(destination,legacy),"UninitializedProfile");
            example=NewCase("file-collision"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); File.WriteAllText(destination,"existing file");
            Reject(()=>Initialize(destination,legacy),"UninitializedProfile");

            example=NewCase("partial-copy-failure"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy);
            int copies=0;
            var safeError=Reject(()=>WidgetDataProfile.InitializeForTests(destination,legacy,null,name=>{ if (++copies==3) throw new IOException("SECRET_TOKEN PRIVATE_EMAIL fixture failure"); }),"MigrationFailed");
            Check(!safeError.Message.Contains("SECRET_TOKEN") && !safeError.EnglishMessage.Contains("PRIVATE_EMAIL"),"Failure message excludes sensitive exception details");
            Check(!Directory.Exists(destination) && NoStaging(example),"Partial failure commits nothing and cleans stage");
            Check(File.ReadAllText(Path.Combine(legacy,"membership-session.dat"))=="protected-session-account-A","Partial failure preserves source account");
            Initialize(destination,legacy); Check(File.Exists(Path.Combine(destination,"elevenlabs-key.dat")),"Retry after partial failure succeeds");

            example=NewCase("locked-session"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy);
            using (var locked=new FileStream(Path.Combine(legacy,"membership-session.dat"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Initialize(destination,legacy);
            Check(File.Exists(Path.Combine(destination,"elevenlabs-key.dat")) && !File.Exists(Path.Combine(destination,"membership-session.dat")) && NoStaging(example),"Locked obsolete Nephylo session cannot block complete provider migration");
            Check(File.ReadAllText(Path.Combine(legacy,"membership-session.dat"))=="protected-session-account-A","Locked obsolete session is preserved at its original location");

            example=NewCase("oversized-session"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy); Write(legacy,"membership-session.dat",new String('x',65537));
            Write(legacy,"membership-reauth-required.json",new String('x',8193));
            using(var lockedMarker=new FileStream(Path.Combine(legacy,"membership-reauth-required.json"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) Initialize(destination,legacy);
            Check(File.Exists(Path.Combine(destination,"settings.json")) && !File.Exists(Path.Combine(destination,"membership-session.dat")) && !File.Exists(Path.Combine(destination,"membership-reauth-required.json")),"Oversized sessions and locked retired auth markers are never read or imported");
            Check(new FileInfo(Path.Combine(legacy,"membership-session.dat")).Length==65537 && new FileInfo(Path.Combine(legacy,"membership-reauth-required.json")).Length==8193,"Malformed retired authentication originals are retained unchanged");
            Write(destination,"membership-session.dat","existing-canonical-token"); Initialize(destination,legacy);
            Check(File.ReadAllText(Path.Combine(destination,"membership-session.dat"))=="existing-canonical-token","An existing canonical session is preserved even though new migrations no longer import it");

            example=NewCase("logout-tombstone"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); Write(legacy,"membership-session.dat",""); Initialize(destination,legacy);
            Check(new FileInfo(Path.Combine(legacy,"membership-session.dat")).Length==0 && !File.Exists(Path.Combine(destination,"membership-session.dat")),"An obsolete logout tombstone remains in legacy storage and is not reactivated");
            Write(legacy,"membership-session.dat","old account"); Initialize(destination,legacy);
            Check(!File.Exists(Path.Combine(destination,"membership-session.dat")),"Later obsolete credentials cannot enter an initialized accountless profile");

            example=NewCase("locked-provider-key"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy);
            using(var lockedKey=new FileStream(Path.Combine(legacy,"elevenlabs-key.dat"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) Reject(()=>Initialize(destination,legacy),"MigrationFailed");
            Check(!Directory.Exists(destination) && NoStaging(example),"Active provider credentials still migrate as a strict atomic cohort");
            example=NewCase("oversized-provider-key"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); Write(legacy,"elevenlabs-key.dat",new String('x',32769));
            Reject(()=>Initialize(destination,legacy),"InvalidLegacyFile"); Check(!Directory.Exists(destination),"Retiring membership does not weaken active provider credential size validation");
            example=NewCase("empty-settings"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); Write(legacy,"settings.json","");
            Reject(()=>Initialize(destination,legacy),"InvalidLegacyFile");
            example=NewCase("wrong-type-retired-session"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); Directory.CreateDirectory(Path.Combine(legacy,"membership-session.dat"));
            Initialize(destination,legacy); Check(Directory.Exists(Path.Combine(legacy,"membership-session.dat")) && !Directory.Exists(Path.Combine(destination,"membership-session.dat")),"An obsolete session directory is ignored without being traversed or removed");
            example=NewCase("wrong-type-provider-key"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); Directory.CreateDirectory(Path.Combine(legacy,"elevenlabs-key.dat"));
            Reject(()=>Initialize(destination,legacy),"InvalidLegacyFile");

            example=NewCase("mid-copy-retired-account-change"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); Write(legacy,"settings.json","{}");
            WidgetDataProfile.InitializeForTests(destination,legacy,null,name=>Write(legacy,"membership-session.dat","new-account"));
            Check(File.Exists(Path.Combine(destination,"settings.json")) && !File.Exists(Path.Combine(destination,"membership-session.dat")) && NoStaging(example),"An obsolete session appearing mid-copy cannot interrupt active profile migration");
            example=NewCase("mid-copy-provider-change"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); Write(legacy,"settings.json","{}");
            Reject(()=>WidgetDataProfile.InitializeForTests(destination,legacy,null,name=>Write(legacy,"elevenlabs-key.dat","new-provider")),"ProfileBusy");
            Check(!Directory.Exists(destination) && NoStaging(example),"An active provider key appearing mid-copy still aborts the whole cohort");

            example=NewCase("read-locks"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy);
            bool blocked=false;
            WidgetDataProfile.InitializeForTests(destination,legacy,null,name=>{ if (blocked) return; try { File.WriteAllText(Path.Combine(legacy,"elevenlabs-key.dat"),"changed provider"); } catch (IOException) { blocked=true; } });
            Check(blocked,"Active provider credentials remain read locked through cohort copy");
            Check(File.ReadAllText(Path.Combine(destination,"elevenlabs-key.dat"))=="protected-key-A","Active provider identity cannot change during copy");
            example=NewCase("concurrent"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical"); CopyBaseline(legacy);
            Exception threadError=null; bool markerVisibleEarly=false;
            var entered=new ManualResetEvent(false);
            var thread=new Thread(()=>{ try { WidgetDataProfile.InitializeForTests(destination,legacy,null,name=>{ markerVisibleEarly=Directory.Exists(destination); entered.Set(); Thread.Sleep(15); }); } catch(Exception e) { threadError=e; entered.Set(); } });
            thread.Start(); Check(entered.WaitOne(5000),"First initializer started"); Initialize(destination,legacy); thread.Join();
            Check(threadError==null && !markerVisibleEarly,"Concurrent initializers see no partial destination");
            Check(File.Exists(Path.Combine(destination,"elevenlabs-key.dat")) && NoStaging(example),"Concurrent initialization publishes complete single profile");
            entered.Dispose();

            // Junctions require no administrator rights on Windows; use fixture-only targets.
            example=NewCase("junction"); legacy=Path.Combine(example,"legacy"); destination=Path.Combine(example,"canonical");
            string external=Path.Combine(example,"external"); Directory.CreateDirectory(external);
            var junction=new ProcessStartInfo("cmd.exe","/d /c mklink /J \"" + destination + "\" \"" + external + "\"") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            using(var process=Process.Start(junction)) { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit(); Check(process.ExitCode==0,"Fixture junction created"); }
            Reject(()=>Initialize(destination,legacy),"UnsafeProfilePath"); Check(Directory.GetFiles(external).Length==0,"Reparse destination target untouched"); Directory.Delete(destination,false);
            Directory.CreateDirectory(legacy);
            junction.Arguments="/d /c mklink /J \"" + Path.Combine(legacy,"tools") + "\" \"" + external + "\"";
            using(var process=Process.Start(junction)) { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit(); Check(process.ExitCode==0,"Fixture tools junction created"); }
            Write(external,"higgsfield.exe","untrusted"); Reject(()=>Initialize(destination,legacy),"UnsafeProfilePath");
            Check(!Directory.Exists(destination),"Reparse tool is not copied"); Directory.Delete(Path.Combine(legacy,"tools"),false);

            Console.WriteLine("Profile migration tests passed: " + passed); return 0;
        }
        catch(Exception error) {
            // Product errors deliberately redact filesystem detail. These are only
            // synthetic fixture paths; capture the original failure for CI diagnosis.
            if (error is ProfileInitializationException && lastFixtureStorageFailure != null)
                Console.Error.WriteLine("Fixture storage failure: " + lastFixtureStorageFailure);
            Console.Error.WriteLine(error); return 1;
        }
        finally { DeleteFixture(root); }
    }

    private static void DeleteFixture(string path)
    {
        string expected=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)+Path.DirectorySeparatorChar+".nephylo-profile-test-";
        if (!Path.GetFullPath(path).StartsWith(expected,StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid fixture cleanup target");
        if (!Directory.Exists(path)) return;
        foreach(string entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes=File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory)!=0)
            {
                if ((attributes & FileAttributes.ReparsePoint)!=0) Directory.Delete(entry,false);
                else DeleteFixture(entry);
            }
            else File.Delete(entry);
        }
        Directory.Delete(path,false);
    }
}
