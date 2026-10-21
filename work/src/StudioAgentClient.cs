using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UsageWidget
{
    internal interface IStudioAgentClient : IDisposable
    {
        string Provider { get; }
        string Profile { get; }
        string ConnectionId { get; }
        bool Connected { get; }
        StudioAccountIdentity AccountIdentity { get; }
        event Action<string,string> Notice;
        event Action<string,string,string> Message;
        Task ConnectAsync(string executable,string home);
        Task<Dictionary<string,object>> AccountAsync();
        Task<List<StudioModel>> ModelsAsync();
        Task<string> ThreadAsync(string id,string project,string model);
        Task<string> RunBoundAsync(string threadId,string project,string model,string prompt,string expectedAccountFingerprint);
        Task InterruptAsync();
        Task<StudioQuotaSnapshot> QuotaAsync();
    }
    // Stateless APIs receive only the confirmed turns from their own local execution.
    // The new prompt, unsent draft and other providers' messages are excluded.
    internal interface IStudioConversationClient
    {
        void PrepareHistory(string threadId,IList<StudioMessage> messages);
    }
    // Some official ACP versions replay history asynchronously during session/load.
    // The adapter must verify that replay before accepting output for a new prompt.
    internal interface IStudioReplayClient
    {
        void PrepareReplay(string threadId,IList<StudioMessage> messages);
    }
}
