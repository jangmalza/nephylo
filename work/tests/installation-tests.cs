using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.CSharp;
using UsageWidget;

internal static class InstallationTests
{
    private static int assertions;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message) { try { action(); } catch { assertions++; return; } throw new Exception(message); }
    private static string Hash(string path) { using (var file = File.OpenRead(path)) using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(file)); }
    private static void Fixture(string directory, string version, string product = "Usage Widget")
    {
        Directory.CreateDirectory(directory);
        using (var compiler = new CSharpCodeProvider()) {
            var settings = new CompilerParameters(new[] { "System.dll" },Path.Combine(directory,"UsageWidget.exe"),false) { GenerateExecutable = true, CompilerOptions = "/target:winexe" };
            var result = compiler.CompileAssemblyFromSource(settings,"using System.Reflection; [assembly:AssemblyProduct(\"" + product + "\")] [assembly:AssemblyVersion(\"" + version + "\")] public static class Fixture { public static void Main() {} }");
            if (result.Errors.HasErrors) throw new Exception("Fixture compilation failed");
        }
        File.WriteAllText(Path.Combine(directory,"UsageWidget.exe.config"),"<configuration />");
        File.WriteAllText(Path.Combine(directory,"README.txt"),"Fixture version " + version);
        File.WriteAllText(Path.Combine(directory,"api-key-do-not-copy.dat"),"synthetic-secret-not-distributed");
    }
    private static WidgetInstallResult Install(string source, string data, string link, Func<string,bool> running, ref string startup, bool failLink = false, bool failStartup = false, string legacyLink = null, string[] additionalLegacyLinks = null)
    {
        string current = startup;
        try {
            return WidgetInstallation.Install(source,data,link,running,(path,target) => {
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path,target);
                if (failLink) throw new IOException("synthetic shortcut failure");
            },() => current,value => { current = value; if (failStartup) throw new IOException("synthetic registry failure"); },legacyLink,
                (path,target) => File.Exists(path) && File.ReadAllText(path) == target,additionalLegacyLinks);
        } finally { startup = current; }
    }
    [STAThread]
    public static int Main()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        string root = Path.Combine(parent,"installation-isolated-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(root);
            Check(SettingsStore.DataDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nephylo"), "Account profile uses the stable user directory");
            Check(WidgetInstallation.InstalledExecutable == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageWidget", "App", "UsageWidget.exe"), "Installed executable retains its existing location independently of account profile");
            string older = Path.Combine(root,"older"), current = Path.Combine(root,"current");
            Fixture(older,"1.0.0.0"); Fixture(current,"2.0.0.0","Nephylo");
            string previousBrand = Path.Combine(root,"milos"); Fixture(previousBrand,"1.5.0.0","Milos");
            string data = Path.Combine(root,"data"), link = Path.Combine(root,"start-menu","Usage Widget.lnk");
            Directory.CreateDirectory(data);
            foreach (string name in new[] { "elevenlabs-key.dat", "settings.json", "usage-journal.json", "membership-session.dat", "higgsfield-settings.json" }) File.WriteAllText(Path.Combine(data,name),"preserved-fixture-" + name);
            var hashes = Directory.GetFiles(data).ToDictionary(p => p,Hash);
            string startup = null;
            var first = Install(older,data,link,p => false,ref startup);
            Check(first.ExecutablePath == Path.Combine(data,"App","UsageWidget.exe") && File.Exists(first.ExecutablePath),"Installs executable into fixed App subfolder");
            Check(!first.AlreadyInstalled && !first.StartupUpdated && startup == null,"First installation does not enable disabled startup");
            Check(File.ReadAllText(link) == first.ExecutablePath,"Start-menu shortcut points to installed copy");
            Check(!File.Exists(Path.Combine(data,"App","api-key-do-not-copy.dat")),"Only allowlisted distribution files copied");
            foreach (var pair in hashes) Check(Hash(pair.Key) == pair.Value,"Parent user data is byte-identical: " + Path.GetFileName(pair.Key));
            File.WriteAllText(Path.Combine(data,"App","user-note.txt"),"preserve unknown app file");
            Install(previousBrand,data,link,p => false,ref startup);
            Check(Hash(first.ExecutablePath) == Hash(Path.Combine(previousBrand,"UsageWidget.exe")),"Previous Milos distribution remains recognized for upgrade compatibility");
            string before = Hash(first.ExecutablePath);
            Reject(() => { string local = startup; Install(current,data,link,p => true,ref local); },"Running target must be rejected");
            Check(Hash(first.ExecutablePath) == before,"Running target leaves existing executable unchanged");
            startup = "\"C:\\old-widget\\UsageWidget.exe\"";
            var updated = Install(current,data,link,p => false,ref startup);
            Check(Hash(updated.ExecutablePath) == Hash(Path.Combine(current,"UsageWidget.exe")),"Update copies new verified bytes");
            Check(updated.StartupUpdated && startup == "\"" + updated.ExecutablePath + "\"","Enabled widget startup retargets to fixed App path");
            Check(File.ReadAllText(Path.Combine(data,"App","user-note.txt")) == "preserve unknown app file","Update preserves unknown destination files");
            Check(hashes.All(pair => Hash(pair.Key) == pair.Value),"Update preserves all sibling settings, keys and history");
            string newerHash = Hash(updated.ExecutablePath);
            string transientData = Path.Combine(root,"transient-data"), transientLink = Path.Combine(root,"transient-menu","Nephylo.lnk");
            string transientStartup = null;
            var transientOld = Install(older,transientData,transientLink,p => false,ref transientStartup);
            FileStream transientLock = null;
            Thread releaseLock = null;
            int runningChecks = 0;
            try {
                Install(current,transientData,transientLink,p => {
                    if (++runningChecks == 2) {
                        transientLock = new FileStream(transientOld.ExecutablePath,FileMode.Open,FileAccess.Read,FileShare.Read);
                        releaseLock = new Thread(() => { Thread.Sleep(120); transientLock.Dispose(); });
                        releaseLock.Start();
                    }
                    return false;
                },ref transientStartup);
            } finally { if (releaseLock != null) releaseLock.Join(); if (transientLock != null) transientLock.Dispose(); }
            Check(runningChecks == 2 && Hash(transientOld.ExecutablePath) == Hash(Path.Combine(current,"UsageWidget.exe")),"A brief external read lock during atomic replacement does not abort a valid upgrade");
            Check(Directory.GetDirectories(transientData,".install-*").Length == 0,"Transient replacement retry still cleans completed installation staging");
            Reject(() => { string local = startup; Install(older,data,link,p => false,ref local); },"Older distribution cannot downgrade installed copy");
            Check(Hash(updated.ExecutablePath) == newerHash,"Rejected downgrade retains new executable");
            var already = Install(Path.GetDirectoryName(updated.ExecutablePath),data,link,p => true,ref startup);
            Check(already.AlreadyInstalled,"Running installed copy gives no-op before touching files");

            string rollbackData = Path.Combine(root,"rollback-data"), rollbackLink = Path.Combine(root,"rollback-menu","Usage Widget.lnk");
            startup = null;
            var initial = Install(older,rollbackData,rollbackLink,p => false,ref startup);
            string oldBinary = Hash(initial.ExecutablePath), oldReadme = File.ReadAllText(Path.Combine(rollbackData,"App","README.txt")), oldLink = File.ReadAllText(rollbackLink);
            Reject(() => { string local = null; Install(current,rollbackData,rollbackLink,p => false,ref local,true); },"Shortcut write error fails installation");
            Check(Hash(initial.ExecutablePath) == oldBinary && File.ReadAllText(Path.Combine(rollbackData,"App","README.txt")) == oldReadme,"Failed shortcut rolls distribution files back");
            Check(File.ReadAllText(rollbackLink) == oldLink,"Failed shortcut restores previous shortcut");
            startup = "\"C:\\previous\\UsageWidget.exe\"";
            try { Install(current,rollbackData,rollbackLink,p => false,ref startup,false,true); throw new Exception("Expected startup failure"); } catch (InvalidOperationException) { assertions++; }
            Check(Hash(initial.ExecutablePath) == oldBinary,"Registry failure still attempts and completes binary rollback");
            Check(File.ReadAllText(rollbackLink) == oldLink,"Registry failure still restores shortcut");
            Check(startup == "\"C:\\previous\\UsageWidget.exe\"","Previous startup value restored after failed write");
            Check(Directory.GetDirectories(rollbackData,".install-*").Length == 1,"Backup retained when registry rollback reports failure");

            string foreignStartup = "\"C:\\another-app.exe\" --user-argument";
            startup = foreignStartup;
            var preserve = Install(current,rollbackData,rollbackLink,p => false,ref startup);
            Check(!preserve.StartupUpdated && startup == foreignStartup,"Unrecognized startup value is preserved");
            string broken = Path.Combine(root,"broken"); Directory.CreateDirectory(broken); File.Copy(Path.Combine(current,"UsageWidget.exe"),Path.Combine(broken,"UsageWidget.exe"));
            Reject(() => { string local = null; Install(broken,Path.Combine(root,"broken-data"),Path.Combine(root,"broken.lnk"),p => false,ref local); },"Partial ZIP missing config refused");
            Check(!Directory.Exists(Path.Combine(root,"broken-data","App")),"Incomplete source creates no installed App");
            Check(Directory.GetDirectories(data,".install-*").Length == 0,"Successful and ordinary rejected operations clean staging directories");
            string renamedLink = Path.Combine(root,"start-menu","Nephylo.lnk");
            string milosLink = Path.Combine(root,"start-menu","Milos.lnk"); File.WriteAllText(milosLink,updated.ExecutablePath);
            Install(current,data,renamedLink,p => false,ref startup,false,false,milosLink,new[] { link,milosLink });
            Check(File.Exists(renamedLink) && !File.Exists(link) && !File.Exists(milosLink),"Upgrade safely migrates both exact owned legacy shortcuts to Nephylo and deduplicates candidates");
            File.WriteAllText(link,"C:\\another-app.exe");
            File.WriteAllText(milosLink,updated.ExecutablePath + " --custom");
            Install(current,data,renamedLink,p => false,ref startup,false,false,milosLink,new[] { link });
            Check(File.ReadAllText(link) == "C:\\another-app.exe" && File.ReadAllText(milosLink) == updated.ExecutablePath + " --custom","Unrelated or customized legacy-name shortcuts are preserved");
            File.WriteAllText(renamedLink,"C:\\foreign-nephylo.exe");
            Reject(() => { string local = null; Install(current,data,renamedLink,p => false,ref local,false,false,link); },"Unrelated Nephylo-name shortcut is never overwritten");
            Check(File.ReadAllText(renamedLink) == "C:\\foreign-nephylo.exe" && Hash(updated.ExecutablePath) == newerHash,"Shortcut collision preserves unrelated shortcut and installed executable");
            string failedRenameLink = Path.Combine(root,"rollback-menu","Nephylo.lnk");
            Reject(() => { string local = null; Install(current,rollbackData,failedRenameLink,p => false,ref local,true,false,rollbackLink); },"Failed renamed shortcut leaves legacy installation recoverable");
            Check(File.ReadAllText(rollbackLink) == oldLink && !File.Exists(failedRenameLink),"Rollback restores legacy shortcut and removes failed new shortcut");
            string dualData = Path.Combine(root,"dual-data"), dualMenu = Path.Combine(root,"dual-menu");
            string dualOld = Path.Combine(dualMenu,"Usage Widget.lnk"), dualMilos = Path.Combine(dualMenu,"Milos.lnk"), dualNew = Path.Combine(dualMenu,"Nephylo.lnk");
            string dualStartup = null;
            var dualInitial = Install(older,dualData,dualOld,p => false,ref dualStartup);
            File.WriteAllText(dualMilos,dualInitial.ExecutablePath);
            string dualOldHash = Hash(dualInitial.ExecutablePath);
            using (var locked = new FileStream(dualOld,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                Reject(() => { string local = null; Install(current,dualData,dualNew,p => false,ref local,false,false,dualMilos,new[] { dualOld }); },"Second legacy deletion failure rolls back the entire rename");
            }
            Check(File.ReadAllText(dualOld) == dualInitial.ExecutablePath && File.ReadAllText(dualMilos) == dualInitial.ExecutablePath && !File.Exists(dualNew),"Partial legacy deletion restores both old shortcuts and removes the new shortcut");
            Check(Hash(dualInitial.ExecutablePath) == dualOldHash && Directory.GetDirectories(dualData,".install-*").Length == 0,"Partial shortcut migration rolls files back and cleans successful rollback backup");
            string nativeLink = Path.Combine(root,"isolated-real-shortcut.lnk");
            typeof(WidgetInstallation).GetMethod("WriteShortcut",BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,new object[] { nativeLink,updated.ExecutablePath });
            object shell = null, shortcutObject = null;
            try {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell"); shell = Activator.CreateInstance(shellType);
                shortcutObject = shellType.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[] { nativeLink });
                string actualTarget = (string)shortcutObject.GetType().InvokeMember("TargetPath",BindingFlags.GetProperty,null,shortcutObject,null);
                Check(File.Exists(nativeLink) && actualTarget == updated.ExecutablePath,"Actual Windows .lnk writer targets installed executable in isolated folder");
                Check((bool)typeof(WidgetInstallation).GetMethod("IsOwnedShortcut",BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,new object[] { nativeLink,updated.ExecutablePath }),"Actual Windows shortcut with exact installed target is recognized");
                shortcutObject.GetType().InvokeMember("Arguments",BindingFlags.SetProperty,null,shortcutObject,new object[] { "--custom" });
                shortcutObject.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,shortcutObject,null);
                Check(!(bool)typeof(WidgetInstallation).GetMethod("IsOwnedShortcut",BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,new object[] { nativeLink,updated.ExecutablePath }),"Customized shortcut arguments prevent automatic rename");
            } finally {
                if (shortcutObject != null) Marshal.FinalReleaseComObject(shortcutObject);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
            string invalidDistribution = Path.Combine(root,"not-widget"); Directory.CreateDirectory(invalidDistribution);
            File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(invalidDistribution,"UsageWidget.exe")); File.WriteAllText(Path.Combine(invalidDistribution,"UsageWidget.exe.config"),"<configuration />");
            Reject(() => { string local = null; Install(invalidDistribution,Path.Combine(root,"invalid-data"),Path.Combine(root,"invalid.lnk"),p => false,ref local); },"Renaming another executable to UsageWidget does not make a valid distribution");
            Console.WriteLine("Installation tests passed: " + assertions + " (isolated files, fake shortcuts/startup; no user state changed).");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            string full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) == parent && Path.GetFileName(full).StartsWith("installation-isolated-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
