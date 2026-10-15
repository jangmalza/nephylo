using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Remembers the single Free service. Pro entitlement is verified independently by the server.
    public sealed class ServicePlan
    {
        private static readonly string[] Ids = { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
        private readonly string path;
        private readonly bool memoryOnly;
        public string ActiveServiceId { get; private set; }
        public bool IsActive(string id) { return String.Equals(ActiveServiceId, id, StringComparison.Ordinal); }
        public static bool IsSupported(string id) { return Ids.Contains(id); }

        public ServicePlan(string directory, bool preview, IEnumerable<string> order, IEnumerable<string> configured)
        {
            memoryOnly = preview;
            path = preview ? null : Path.Combine(directory, "plan-settings.json");
            string saved = preview ? null : ReadActiveServiceId(directory);
            var ready = new HashSet<string>(configured ?? new string[0]);
            ActiveServiceId = saved ?? (order ?? Ids).FirstOrDefault(id => IsSupported(id) && ready.Contains(id)) ?? "codex";
        }

        public static string ReadActiveServiceId(string directory)
        {
            try {
                string file = Path.Combine(directory, "plan-settings.json");
                if (!File.Exists(file) || new FileInfo(file).Length > 4096) return null;
                var values = new JavaScriptSerializer { MaxJsonLength = 4096, RecursionLimit = 4 }.Deserialize<Dictionary<string,object>>(File.ReadAllText(file, Encoding.UTF8));
                object value;
                string id = values != null && values.TryGetValue("ActiveServiceId", out value) ? value as string : null;
                return IsSupported(id) ? id : null;
            } catch { return null; }
        }

        public void Select(string id)
        {
            if (!IsSupported(id)) throw new ArgumentException("지원하지 않는 서비스입니다.");
            if (!memoryOnly) {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    var json = new JavaScriptSerializer().Serialize(new Dictionary<string,object> { { "ActiveServiceId", id } });
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(json); file.Write(bytes,0,bytes.Length); file.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            ActiveServiceId = id;
        }
    }
}
