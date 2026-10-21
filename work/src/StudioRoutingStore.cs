using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    internal sealed class StudioRoutingSettings
    {
        public string Engine {get;set;}
        public string Policy {get;set;}
        public bool AutoForNew {get;set;}
        public bool IncludeGeminiCli {get;set;}
        public bool IncludeGeminiApi {get;set;}
        public string AzureEndpoint {get;set;}
        public string AzureDeployment {get;set;}
        public StudioRoutingSettings(){Engine="jev";Policy=StudioRoutingPolicy.LegacyTaskPolicy;AzureEndpoint="";AzureDeployment="Microsoft-Decision-1";}
    }
    internal sealed class StudioRoutingStore
    {
        readonly string root;
        internal StudioRoutingStore(string root){this.root=Path.GetFullPath(root);}
        internal string SettingsPath {get{return Path.Combine(root,"routing.state");}}
        internal string KeyPath(string engine){ValidateEngine(engine);return Path.Combine(root,engine+".key.dat");}
        static void ValidateEngine(string engine){if(!StudioRoutingPolicy.Engines.Contains(engine))throw new StudioRouteFailure("settings");}
        internal static bool ValidKey(string value){return value!=null&&value.Length>=16&&value.Length<=2048&&Regex.IsMatch(value,@"\A[A-Za-z0-9_.+/=\-]+\z");}
        static byte[] Entropy(string purpose){return Encoding.UTF8.GetBytes("Nephylo.ADE.Routing.v1/"+purpose);}
        void Guard(string file){StudioDevelopmentProfile.ValidatePath(root);StudioDevelopmentProfile.ValidatePath(file);if(Directory.Exists(file))throw new IOException();}
        byte[] Read(string file,string purpose)
        {
            Guard(file);if(!File.Exists(file))return null;
            if(new FileInfo(file).Length<16||new FileInfo(file).Length>16384)throw new StudioRouteFailure("storage");
            try{return ProtectedData.Unprotect(File.ReadAllBytes(file),Entropy(purpose),DataProtectionScope.CurrentUser);}catch{throw new StudioRouteFailure("storage");}
        }
        void Write(string file,string purpose,byte[] bytes)
        {
            string temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";byte[] encrypted=null;
            try{
                Guard(file);Directory.CreateDirectory(root);Guard(file);encrypted=ProtectedData.Protect(bytes,Entropy(purpose),DataProtectionScope.CurrentUser);
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(encrypted,0,encrypted.Length);output.Flush(true);}
                Guard(file);if(File.Exists(file))StudioStore.ReplaceAtomicWithRetry(temporary,file);else File.Move(temporary,file);
            }catch{throw new StudioRouteFailure("storage");}
            finally{if(encrypted!=null)Array.Clear(encrypted,0,encrypted.Length);if(File.Exists(temporary)&&File.Exists(file)){try{Guard(temporary);File.Delete(temporary);}catch{}}}
        }
        internal StudioRoutingSettings LoadSettings()
        {
            byte[] bytes=null;
            try{bytes=Read(SettingsPath,"settings");if(bytes==null)return new StudioRoutingSettings();if(bytes.Length>4096)throw new IOException();var settings=new JavaScriptSerializer{MaxJsonLength=4096}.Deserialize<StudioRoutingSettings>(new UTF8Encoding(false,true).GetString(bytes));ValidateSettings(settings);return settings;}
            catch{throw new StudioRouteFailure("storage");}finally{if(bytes!=null)Array.Clear(bytes,0,bytes.Length);}
        }
        internal static void ValidateSettings(StudioRoutingSettings value)
        {
            if(value==null)throw new StudioRouteFailure("settings");ValidateEngine(value.Engine);
            StudioRoutingPolicy.NormalizePolicy(value.Policy);
            if((value.AzureEndpoint??"").Length>300||(value.AzureDeployment??"").Length>80)throw new StudioRouteFailure("settings");
        }
        internal void SaveSettings(StudioRoutingSettings value)
        {
            ValidateSettings(value);if(File.Exists(SettingsPath))LoadSettings();
            var bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));try{Write(SettingsPath,"settings",bytes);}finally{Array.Clear(bytes,0,bytes.Length);}
        }
        internal string LoadKey(string engine)
        {
            byte[] bytes=null;try{bytes=Read(KeyPath(engine),engine);if(bytes==null)return null;var value=new UTF8Encoding(false,true).GetString(bytes);if(!ValidKey(value))throw new IOException();return value;}catch{throw new StudioRouteFailure("storage");}finally{if(bytes!=null)Array.Clear(bytes,0,bytes.Length);}
        }
        internal void SaveKey(string engine,string key)
        {
            if(!ValidKey(key))throw new StudioRouteFailure("key");string file=KeyPath(engine);if(File.Exists(file))LoadKey(engine);
            var bytes=Encoding.UTF8.GetBytes(key);try{Write(file,engine,bytes);}finally{Array.Clear(bytes,0,bytes.Length);}
        }
        internal void ClearKey(string engine){var file=KeyPath(engine);Guard(file);if(File.Exists(file))File.Delete(file);}
    }
}
