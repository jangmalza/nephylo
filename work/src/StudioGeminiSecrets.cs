using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageWidget
{
    // This file contains only the user's explicit ADE API key, never CLI OAuth credentials.
    internal sealed class StudioGeminiSecrets
    {
        readonly string root;
        static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Nephylo.ADE.GeminiApiKey.v1");
        internal StudioGeminiSecrets(string root) { this.root=Path.GetFullPath(root); }
        internal string FilePath { get { return Path.Combine(root,"gemini-api.key.dat"); } }
        internal bool HasKey { get { return !String.IsNullOrEmpty(Load()); } }
        internal static bool ValidKey(string key)
        { return key!=null&&key.Length>=16&&key.Length<=512&&Regex.IsMatch(key,@"\A[A-Za-z0-9_-]+\z"); }
        void CheckPath()
        {
            for(string p=root;!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
                if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException();
            if(Directory.Exists(FilePath)||(File.Exists(FilePath)&&(File.GetAttributes(FilePath)&FileAttributes.ReparsePoint)!=0))throw new IOException();
        }
        internal string Load()
        {
            byte[] encrypted=null,plain=null;
            try {
                CheckPath();if(!File.Exists(FilePath))return null;
                using(var stream=new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(stream.Length<16||stream.Length>16384)throw new InvalidDataException();
                    encrypted=new byte[(int)stream.Length];int offset=0,count;
                    while(offset<encrypted.Length&&(count=stream.Read(encrypted,offset,encrypted.Length-offset))>0)offset+=count;
                    if(offset!=encrypted.Length)throw new InvalidDataException();
                }
                plain=ProtectedData.Unprotect(encrypted,Entropy,DataProtectionScope.CurrentUser);
                if(plain.Length>512)throw new InvalidDataException();
                var key=new UTF8Encoding(false,true).GetString(plain);
                if(!ValidKey(key))throw new InvalidDataException();
                return key;
            } catch { throw new StudioFailure(StudioFailureKind.Storage); }
            finally { if(plain!=null)Array.Clear(plain,0,plain.Length);if(encrypted!=null)Array.Clear(encrypted,0,encrypted.Length); }
        }
        internal void Save(string key)
        {
            if(!ValidKey(key))throw new StudioFailure(StudioFailureKind.Authentication);
            byte[] plain=null,encrypted=null;string temporary=null;
            try {
                CheckPath();Directory.CreateDirectory(root);CheckPath();
                // Do not silently overwrite a corrupt existing secret; the user must clear it first.
                if(File.Exists(FilePath))Load();
                plain=Encoding.UTF8.GetBytes(key);encrypted=ProtectedData.Protect(plain,Entropy,DataProtectionScope.CurrentUser);
                temporary=Path.Combine(root,".gemini-key-"+Guid.NewGuid().ToString("N")+".tmp");
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(encrypted,0,encrypted.Length);stream.Flush(true); }
                CheckPath();
                if(File.Exists(FilePath))File.Replace(temporary,FilePath,null);else File.Move(temporary,FilePath);
                temporary=null;
            } catch { throw new StudioFailure(StudioFailureKind.Storage); }
            finally {
                if(plain!=null)Array.Clear(plain,0,plain.Length);if(encrypted!=null)Array.Clear(encrypted,0,encrypted.Length);
                if(temporary!=null){try{CheckPath();if(File.Exists(temporary)&&(File.GetAttributes(temporary)&FileAttributes.ReparsePoint)==0)File.Delete(temporary);}catch{}}
            }
        }
        internal void Clear()
        {
            try { CheckPath();if(File.Exists(FilePath))File.Delete(FilePath); }
            catch { throw new StudioFailure(StudioFailureKind.Storage); }
        }
    }
}
