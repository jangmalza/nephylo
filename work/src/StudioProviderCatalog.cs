using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UsageWidget
{
    internal static class StudioProviderCatalog
    {
        internal static readonly string[] Ids={"codex","claude","gemini-cli","gemini-api"};
        internal static bool IsValid(string provider){return Ids.Contains(provider);}
        internal static string Normalize(string provider){if(String.IsNullOrEmpty(provider))return "codex";if(!IsValid(provider))throw new InvalidDataException("Unsupported AI provider.");return provider;}
        internal static int Index(string provider){return Array.IndexOf(Ids,provider);}
        internal static string FromIndex(int index){return index>=0&&index<Ids.Length?Ids[index]:null;}
        internal static bool IsGemini(string provider){return provider=="gemini-cli"||provider=="gemini-api";}
        internal static string Name(string provider){switch(provider){case "codex":return "Codex";case "claude":return "Claude Code";case "gemini-cli":return "Gemini CLI";case "gemini-api":return "Gemini API";default:return "AI";}}
        internal static string Profile(string provider){switch(provider){case "codex":return "ade-codex";case "claude":return "ade-claude-code";case "gemini-cli":return "ade-gemini-cli";case "gemini-api":return "ade-gemini-api";default:throw new InvalidDataException("Unsupported AI provider.");}}
        internal static List<StudioMessage> CompletedHistory(StudioSession session,string executionId)
        {
            var result=new List<StudioMessage>();var execution=session.ProviderSessions.Single(e=>e.Id==executionId);StudioMessage pending=null;
            foreach(var message in session.Messages.Where(m=>m.ExecutionId==executionId&&m.Provider==execution.Provider)){
                if(message.Role=="user"){pending=message.State=="completed"?message:null;continue;}
                if(message.Role=="assistant"){
                    if(pending!=null&&message.State=="completed"&&!String.IsNullOrEmpty(message.Text)){
                        foreach(var m in new[]{pending,message})result.Add(new StudioMessage{Id=m.Id,Role=m.Role,Text=m.Text,State="completed",Provider=m.Provider,ExecutionId=m.ExecutionId,Model=m.Model});
                    }
                    pending=null;
                }
            }
            return result;
        }
    }
}
