using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    internal enum StudioFailureKind { Authentication, Quota, Model, Network, Timeout, Compatibility, Permission, Project, Storage, Disconnected, Context, Installation, Browser, Unknown }
    // Never retain or display upstream text: it can contain prompts, paths, tokens or login URLs.
    internal sealed class StudioFailure : Exception
    {
        internal StudioFailureKind Kind { get; private set; }
        internal StudioFailure(StudioFailureKind kind) : base("Studio: " + kind) { Kind=kind; }
        internal static StudioFailure FromServer(Dictionary<string,object> error)
        {
            string raw=error==null?"":new JavaScriptSerializer().Serialize(error);
            if(raw.Length>8192)raw=raw.Substring(0,8192);
            string value=raw.ToLowerInvariant(); object code;
            if(error!=null&&error.TryGetValue("code",out code)&&(Convert.ToString(code)=="-32601"||Convert.ToString(code)=="-32602"))return new StudioFailure(StudioFailureKind.Compatibility);
            if(Has(value,"unauthorized","authentication","not logged in","token expired","invalid_token","\"httpstatuscode\":401","\"status\":401"))return new StudioFailure(StudioFailureKind.Authentication);
            if(Has(value,"ratelimit","rate_limit","rate limit","quota","usage limit","usagelimit","\"httpstatuscode\":429","\"status\":429"))return new StudioFailure(StudioFailureKind.Quota);
            if(Has(value,"contextwindowexceeded","context_length","context window"))return new StudioFailure(StudioFailureKind.Context);
            if(Has(value,"modelnotfound","model_not_found","modelnotavailable","model is not supported","model is not available","unsupported model"))return new StudioFailure(StudioFailureKind.Model);
            if(Has(value,"permission","forbidden","sandbox","\"httpstatuscode\":403","\"status\":403"))return new StudioFailure(StudioFailureKind.Permission);
            if(Has(value,"timeout","timed out"))return new StudioFailure(StudioFailureKind.Timeout);
            if(Has(value,"network","connection","httpconnection","streamconnection","dns","serviceunavailable","serveroverloaded","\"httpstatuscode\":500","\"httpstatuscode\":502","\"httpstatuscode\":503"))return new StudioFailure(StudioFailureKind.Network);
            return new StudioFailure(StudioFailureKind.Unknown);
        }
        static bool Has(string value,params string[] terms){foreach(var term in terms)if(value.Contains(term))return true;return false;}
        internal static StudioFailureKind Classify(Exception error)
        {
            var known=error as StudioFailure;if(known!=null)return known.Kind;
            if(error is TimeoutException)return StudioFailureKind.Timeout;
            if(error is InvalidDataException)return StudioFailureKind.Compatibility;
            if(error is ObjectDisposedException||error is IOException)return StudioFailureKind.Disconnected;
            return StudioFailureKind.Unknown;
        }
        internal static string Guidance(StudioFailureKind kind,bool english)
        {
            switch(kind){
                case StudioFailureKind.Installation:return english?"Could not find or start Codex. Check the Windows Codex installation and executable path in widget settings.":"Codex를 찾거나 실행하지 못했습니다. Windows용 Codex 설치와 위젯 설정의 실행 경로를 확인하세요.";
                case StudioFailureKind.Browser:return english?"Could not open the sign-in browser. Check the Windows default browser, reconnect and retry Browser sign-in.":"로그인 브라우저를 열지 못했습니다. Windows 기본 브라우저를 확인하고 다시 연결한 뒤 브라우저 로그인을 시도하세요.";
                case StudioFailureKind.Authentication:return english?"Sign-in expired or was rejected. Use Browser sign-in, then retry your question.":"로그인이 만료되었거나 거절됐습니다. 브라우저 로그인 후 질문을 다시 보내세요.";
                case StudioFailureKind.Quota:return english?"Codex usage or request limit reached. Check ADE's allowance and reset time before retrying.":"Codex 사용량 또는 요청 한도에 도달했습니다. ADE 실행 계정의 잔여량·초기화 시각을 확인한 뒤 다시 시도하세요.";
                case StudioFailureKind.Model:return english?"The saved or selected model is unavailable. Reconnect to refresh the list. For a started task, use context transfer to change the model.":"저장되었거나 선택한 모델을 사용할 수 없습니다. 다시 연결해 목록을 갱신하세요. 시작한 작업은 문맥 전달에서 모델을 변경하세요.";
                case StudioFailureKind.Network:return english?"The network or Codex service is unavailable. Check your connection and try Connect Codex again.":"네트워크 또는 Codex 서비스에 연결하지 못했습니다. 인터넷 상태를 확인하고 Codex 연결을 다시 시도하세요.";
                case StudioFailureKind.Timeout:return english?"Codex did not respond in time. Review any partial answer, reconnect, then retry if needed.":"Codex 응답 시간이 초과됐습니다. 부분 답변을 확인하고 다시 연결한 뒤 필요한 경우 재시도하세요.";
                case StudioFailureKind.Compatibility:return english?"This Codex version could not process the Studio protocol. Update the official Codex installation and reconnect.":"현재 Codex 버전이 작업실 요청을 처리하지 못했습니다. 공식 Codex를 업데이트하고 다시 연결하세요.";
                case StudioFailureKind.Permission:return english?"The request conflicts with read-only or account permissions. Check the project and account permissions; protections were not relaxed.":"읽기 전용 또는 계정 권한으로 요청을 실행할 수 없습니다. 프로젝트·계정 권한을 확인하세요. 보호 설정은 유지했습니다.";
                case StudioFailureKind.Project:return english?"The project folder is unavailable. Check its location or start a new conversation with an existing folder.":"프로젝트 폴더를 찾거나 열 수 없습니다. 위치를 확인하거나 유효한 폴더로 새 대화를 시작하세요.";
                case StudioFailureKind.Storage:return english?"Conversation save failed. Check disk space and folder permissions, then retry saving or copy the conversation. The window will remain open.":"대화 저장에 실패했습니다. 디스크 공간·폴더 권한을 확인한 뒤 다시 저장하거나 내용을 복사하세요. 창은 열린 상태로 유지됩니다.";
                case StudioFailureKind.Disconnected:return english?"The Codex connection ended. Partial content is retained in this window. Reconnect before continuing.":"Codex 연결이 종료됐습니다. 부분 내용은 이 창에 유지됩니다. 다시 연결한 뒤 이어가세요.";
                case StudioFailureKind.Context:return english?"This conversation exceeds the model's context limit. Start a new conversation with a short summary.":"대화가 모델의 문맥 한도를 초과했습니다. 필요한 내용을 요약해 새 대화에서 이어가세요.";
                default:return english?"Codex could not complete the request. Your draft or partial answer remains here. Reconnect, and report the failing step if it repeats.":"Codex가 요청을 완료하지 못했습니다. 초안·부분 답변은 이 창에 남아 있습니다. 다시 연결하고 반복되면 문제가 발생한 단계를 알려주세요.";
            }
        }
        internal static string Guidance(StudioFailureKind kind,bool english,string provider)
        {
            if(StudioProviderCatalog.IsGemini(provider)){
                if(kind==StudioFailureKind.Authentication)return provider=="gemini-api"?(english?"Gemini API key was missing or rejected. Configure a key with access to this project and model, then reconnect.":"Gemini API 키가 없거나 거절됐습니다. 해당 프로젝트·모델에 접근할 수 있는 키를 설정하고 다시 연결하세요."):(english?"Sign in with Google through official Gemini CLI, then reconnect. The dedicated ADE profile has separate authentication.":"공식 Gemini CLI에서 Google 로그인한 뒤 다시 연결하세요. ADE 전용 프로필의 인증은 별도입니다.");
                if(kind==StudioFailureKind.Installation)return english?"Official Gemini CLI or Node.js could not be found. Install supported versions, then reconnect.":"공식 Gemini CLI 또는 Node.js를 찾지 못했습니다. 지원 버전을 설치한 뒤 다시 연결하세요.";
                if(kind==StudioFailureKind.Compatibility)return english?"This Gemini connection does not support the required protocol. Update the official CLI or check API model availability.":"현재 Gemini 연결이 필요한 프로토콜을 지원하지 않습니다. 공식 CLI를 업데이트하거나 API 모델 사용 가능 여부를 확인하세요.";
                if(kind==StudioFailureKind.Quota)return english?"Gemini quota or billing limit reached. Check the selected CLI account or API project; their limits are separate.":"Gemini 사용 한도 또는 청구 한도에 도달했습니다. 선택한 CLI 계정이나 API 프로젝트를 확인하세요. 두 한도는 별도입니다.";
                if(kind==StudioFailureKind.Permission)return english?"Gemini chat-only permissions could not be confirmed or access was denied. Tools and broader permissions were not enabled.":"Gemini의 대화 전용 권한을 확인하지 못했거나 접근이 거절됐습니다. 도구나 추가 권한을 활성화하지 않았습니다.";
                if(kind==StudioFailureKind.Context)return english?"Gemini conversation context or session recovery could not be verified. Review the saved conversation and start a new execution through context transfer if needed.":"Gemini 대화 문맥 또는 세션 복원을 확인하지 못했습니다. 저장된 대화를 검토하고 필요한 경우 문맥 전달로 새 실행을 시작하세요.";
                return Guidance(kind,english).Replace("Codex",StudioProviderCatalog.Name(provider)).Replace("OpenAI","Google");
            }
            if(provider!="claude")return Guidance(kind,english);
            switch(kind){
                case StudioFailureKind.Installation:return english?"Official Windows Claude Code could not be found or started. Install the native Claude Code CLI, then reconnect.":"Windows용 공식 Claude Code를 찾거나 실행하지 못했습니다. 네이티브 Claude Code CLI를 설치한 뒤 다시 연결하세요.";
                case StudioFailureKind.Authentication:return english?"Claude Code authentication or entitlement is unavailable. Sign in through the official CLI or configure your own API access, then reconnect.":"Claude Code 인증 또는 이용 권한을 확인할 수 없습니다. 공식 CLI에서 로그인하거나 본인 API 연결을 준비한 뒤 다시 연결하세요.";
                case StudioFailureKind.Compatibility:return english?"This Claude Code version does not support the required safe streaming options. Update the official CLI and reconnect.":"현재 Claude Code 버전이 필요한 실행·스트리밍 옵션을 지원하지 않습니다. 공식 CLI를 업데이트한 뒤 다시 연결하세요.";
                case StudioFailureKind.Permission:return english?"Claude Code could not confirm chat-only permissions. The request was stopped; file and command tools were not enabled.":"Claude Code의 대화 전용 권한을 확인하지 못해 요청을 중단했습니다. 파일·명령 도구를 활성화하지 않았습니다.";
                case StudioFailureKind.Quota:return english?"Claude Code usage or billing limit reached. Check your Claude account or API balance before retrying.":"Claude Code의 사용량 또는 청구 한도에 도달했습니다. Claude 계정의 이용 권한·한도 또는 API 잔액을 확인하세요.";
                default:return Guidance(kind,english).Replace("Codex","Claude Code").Replace("OpenAI","Anthropic");
            }
        }
    }
}
