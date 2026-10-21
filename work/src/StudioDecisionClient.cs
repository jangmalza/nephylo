using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    internal interface IStudioDecisionClient : IDisposable
    {
        Task<StudioRouteDecision> DecideAsync(StudioRoutingSettings settings,string key,StudioRouteInput input,CancellationToken cancellation);
    }
    internal sealed class StudioDecisionClient : IStudioDecisionClient
    {
        readonly HttpClient http;
        internal static HttpClientHandler CreateHandler(){return new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,UseDefaultCredentials=false,CheckCertificateRevocationList=true,SslProtocols=SslProtocols.Tls12};}
        internal StudioDecisionClient():this(CreateHandler()){}
        internal StudioDecisionClient(HttpMessageHandler handler){http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(20)};}
        internal static Uri Endpoint(StudioRoutingSettings settings)
        {
            StudioRoutingStore.ValidateSettings(settings);
            if(settings.Engine=="jev")return new Uri("https://api.typesafe.ai/v1/systemone");
            if(settings.Engine=="openai-decisions")return new Uri("https://api.openai.com/v1/decisions");
            Uri endpoint;
            if(!Uri.TryCreate(settings.AzureEndpoint,UriKind.Absolute,out endpoint)||endpoint.Scheme!="https"||endpoint.Port!=443||!String.IsNullOrEmpty(endpoint.UserInfo)||!String.IsNullOrEmpty(endpoint.Query)||!String.IsNullOrEmpty(endpoint.Fragment)||endpoint.AbsolutePath!="/"||!Regex.IsMatch(endpoint.DnsSafeHost,@"\A[a-z0-9][a-z0-9-]{0,62}\.(services\.ai\.azure\.com|cognitiveservices\.azure\.com|inference\.ai\.azure\.com)\z",RegexOptions.CultureInvariant)||!Regex.IsMatch(settings.AzureDeployment??"",@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,79}\z"))throw new StudioRouteFailure("endpoint");
            return new Uri(endpoint,"providers/microsoft/v1/systemone");
        }
        internal static object RequestBody(StudioRoutingSettings settings,StudioRouteInput input)
        {
            if(input==null||StudioRoutingPolicy.NormalizePolicy(settings.Policy)!=StudioRoutingPolicy.NormalizePolicy(input.Policy))throw new StudioRouteFailure("settings");
            var state=input.State();var questions=input.Questions();
            if(settings.Engine=="openai-decisions")return new{model="gpt-6-luna",input=new JavaScriptSerializer().Serialize(state),questions=questions.Select(q=>new{type="choice",name=q.Name,instructions=q.Instructions,choices=q.Options.Select(option=>new{value=option.Key,description=option.Value}).ToArray()}).ToArray()};
            return new{model=settings.Engine=="jev"?"jev-latest":settings.AzureDeployment,state=state,questions=questions.ToDictionary(q=>q.Name,q=>(object)new{type="choice",instructions=q.Instructions,criteria=q.Options})};
        }
        public async Task<StudioRouteDecision> DecideAsync(StudioRoutingSettings settings,string key,StudioRouteInput input,CancellationToken cancellation)
        {
            if(!StudioRoutingStore.ValidKey(key))throw new StudioRouteFailure("key");
            var endpoint=Endpoint(settings);var json=new JavaScriptSerializer{MaxJsonLength=196608};var body=json.Serialize(RequestBody(settings,input));
            if(Encoding.UTF8.GetByteCount(body)>196608)throw new StudioRouteFailure("input");
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation)){
            deadline.CancelAfter(TimeSpan.FromSeconds(20));var watch=Stopwatch.StartNew();
            try{
                using(var request=new HttpRequestMessage(HttpMethod.Post,endpoint)){
                    if(settings.Engine=="ms-decision")request.Headers.Add("api-key",key);else request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
                    request.Content=new StringContent(body,new UTF8Encoding(false),"application/json");
                    using(var result=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token)){
                        if((int)result.StatusCode==429)throw new StudioRouteFailure("rate_limit");
                        if(result.StatusCode==HttpStatusCode.Unauthorized||result.StatusCode==HttpStatusCode.Forbidden)throw new StudioRouteFailure("authentication");
                        if((int)result.StatusCode>=500)throw new StudioRouteFailure("unavailable");
                        if(!result.IsSuccessStatusCode)throw new StudioRouteFailure("http");
                        if(result.Content.Headers.ContentLength>65536)throw new StudioRouteFailure("response");
                        using(var stream=await result.Content.ReadAsStreamAsync())using(var data=new MemoryStream()){
                            var bytes=new byte[4096];int read;
                            while((read=await stream.ReadAsync(bytes,0,bytes.Length,deadline.Token))>0){if(data.Length+read>65536)throw new StudioRouteFailure("response");data.Write(bytes,0,read);}
                            var parsed=Parse(settings,input,new UTF8Encoding(false,true).GetString(data.ToArray()));parsed.Milliseconds=watch.ElapsedMilliseconds;return parsed;
                        }
                    }
                }
            }catch(StudioRouteFailure){throw;}catch(OperationCanceledException){if(cancellation.IsCancellationRequested)throw;throw new StudioRouteFailure("timeout");}catch{throw new StudioRouteFailure("network");}
            }
        }
        static double Number(object value)
        {
            if(!(value is byte||value is short||value is int||value is long||value is float||value is double||value is decimal))throw new StudioRouteFailure("response");
            double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);if(Double.IsNaN(number)||Double.IsInfinity(number)||number<0||number>1)throw new StudioRouteFailure("response");return number;
        }
        static StudioRouteChoice Choice(Dictionary<string,object> answer,StudioRouteQuestion question,bool openai,bool requireConfidence)
        {
            if(StudioCodexClient.Str(answer,"type")!="choice")throw new StudioRouteFailure("refusal");
            string selected=StudioCodexClient.Str(answer,"choice");if(selected==null||!question.Options.ContainsKey(selected))throw new StudioRouteFailure("response");
            var probabilities=new Dictionary<string,double>(StringComparer.Ordinal);object raw;
            if(openai){
                foreach(var item in StudioCodexClient.Items(answer,"probabilities")){
                    string id=StudioCodexClient.Str(item,"value");object probability;if(id==null||probabilities.ContainsKey(id)||!item.TryGetValue("probability",out probability))throw new StudioRouteFailure("response");probabilities.Add(id,Number(probability));
                }
            }else{
                foreach(var item in StudioCodexClient.Obj(answer,"probabilities")??new Dictionary<string,object>())probabilities.Add(item.Key,Number(item.Value));
            }
            if(probabilities.Count!=question.Options.Count||probabilities.Keys.Any(id=>!question.Options.ContainsKey(id))||Math.Abs(probabilities.Values.Sum()-1)>.02||probabilities[selected]+.000001<probabilities.Values.Max())throw new StudioRouteFailure("response");
            double? confidence=answer.TryGetValue("confidence",out raw)&&raw!=null?(double?)Number(raw):null;
            if(requireConfidence&&!confidence.HasValue)throw new StudioRouteFailure("response");
            return new StudioRouteChoice{Value=selected,Probabilities=probabilities,Confidence=confidence};
        }
        internal static StudioRouteDecision Parse(StudioRoutingSettings settings,StudioRouteInput input,string text)
        {
            try{
                if(input==null||StudioRoutingPolicy.NormalizePolicy(settings.Policy)!=StudioRoutingPolicy.NormalizePolicy(input.Policy))throw new StudioRouteFailure("settings");
                var root=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=30}.Deserialize<Dictionary<string,object>>(text);bool openai=settings.Engine=="openai-decisions";
                var answers=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
                if(openai){foreach(var answer in StudioCodexClient.Items(root,"answers")){string name=StudioCodexClient.Str(answer,"name");if(name==null||answers.ContainsKey(name))throw new StudioRouteFailure("response");answers.Add(name,answer);}}
                else{foreach(var answer in StudioCodexClient.Obj(root,"answers")??new Dictionary<string,object>())answers.Add(answer.Key,answer.Value as Dictionary<string,object>);}
                var questions=input.Questions();if(answers.Count!=questions.Count)throw new StudioRouteFailure("response");
                var parsed=questions.ToDictionary(q=>q.Name,q=>Choice(answers.ContainsKey(q.Name)?answers[q.Name]:null,q,openai,settings.Engine!="ms-decision"));
                object tokens;long? inputTokens=null;var usage=StudioCodexClient.Obj(root,"usage");
                if(usage!=null&&usage.TryGetValue("input_tokens",out tokens)&&(tokens is int||tokens is long)){long count=Convert.ToInt64(tokens);if(count>=0&&count<=1000000000)inputTokens=count;}
                return new StudioRouteDecision{Task=parsed["task"],Files=parsed["files"],InputTokens=inputTokens};
            }catch(StudioRouteFailure){throw;}catch{throw new StudioRouteFailure("response");}
        }
        public void Dispose(){http.Dispose();}
    }
}
