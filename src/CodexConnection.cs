// Local official Codex app-server client. No credential file reads or raw wire logging.
// C# 5 / .NET Framework 4.8, newline-delimited JSON over an owned hidden child process.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexCompanion {
 public sealed class LiveModel {
  public string Id {get;set;} public string Name {get;set;} public bool IsDefault {get;set;}
  public List<string> Efforts {get;set;} public LiveModel(){Efforts=new List<string>();}
 }
 public sealed class LiveChatMessage { public string Id {get;set;} public string Role {get;set;} public string Text {get;set;} }
 public sealed class LiveConnectionSnapshot {
  public bool Connected {get;set;} public bool Authenticated {get;set;} public bool IsSending {get;set;} public bool QuotaRefreshFailed {get;set;}
  public string Status {get;set;} public string ChatThreadId {get;set;} public string ActiveTurnId {get;set;}
  public string SelectedModel {get;set;} public string SelectedEffort {get;set;}
  public DateTime? QuotaObservedUtc {get;set;} public UsageWindow Primary {get;set;} public UsageWindow Secondary {get;set;}
  public List<LiveModel> Models {get;set;}
  public List<LiveChatMessage> Messages {get;set;} public LiveConnectionSnapshot(){Models=new List<LiveModel>();Messages=new List<LiveChatMessage>();}
 }
 public sealed class CodexConnection : IDisposable {
  readonly object gate=new object(),writeGate=new object();
  readonly SemaphoreSlim operations=new SemaphoreSlim(1,1);
  readonly Dictionary<int,TaskCompletionSource<Dictionary<string,object>>> pending=new Dictionary<int,TaskCompletionSource<Dictionary<string,object>>>();
  readonly Dictionary<string,string> streamingText=new Dictionary<string,string>(StringComparer.Ordinal);
  readonly string executable,codexHome,workspace;
  readonly LiveConnectionSnapshot state=new LiveConnectionSnapshot {Status="尚未连接"};
  Process child; StreamWriter inputWriter; bool initialized,disposed; int nextId; string resumedThreadId,transportProviderOverride;
  DateTime lastCatalogUtc=DateTime.MinValue,lastAccountUtc=DateTime.MinValue,lastQuotaUtc=DateTime.MinValue,lastQuotaAttemptUtc=DateTime.MinValue;
  public event Action Changed;
  public CodexConnection(string executable,string codexHome,string workspace) {
   this.executable=executable;this.codexHome=codexHome;this.workspace=String.IsNullOrWhiteSpace(workspace)?AppDomain.CurrentDomain.BaseDirectory:workspace;
  }
  public LiveConnectionSnapshot Snapshot {get {lock(gate)return Clone(state);}}
  public async Task RefreshAsync(bool force) {
   await operations.WaitAsync().ConfigureAwait(false);
   try {
    await EnsureStartedAsync().ConfigureAwait(false);
    DateTime now=DateTime.UtcNow;
    // Live quota does not depend on account/read: a slow account lookup must
    // never discard an otherwise valid limits response.
    if(force||(now-lastQuotaAttemptUtc).TotalSeconds>=30) {
     lastQuotaAttemptUtc=now;
     try {var result=await RequestAsync("account/rateLimits/read",null,15000).ConfigureAwait(false);lock(gate){ApplyQuota(state,result,false);lastQuotaUtc=DateTime.UtcNow;state.Connected=true;if(!state.IsSending)state.Status=state.QuotaObservedUtc.HasValue?"已连接 · 额度已更新":"已连接 · 暂无额度窗口";}Publish();}
     catch(Exception e){lock(gate)state.QuotaRefreshFailed=true;SetStatus("额度更新失败："+SafeError(e.Message));}
    }
    if(force||(now-lastAccountUtc).TotalMinutes>=5) {
     try {var result=await RequestAsync("account/read",Obj("refreshToken",false),5000).ConfigureAwait(false);lock(gate){state.Authenticated=Map(Get(result,"account"))!=null;lastAccountUtc=DateTime.UtcNow;if(!state.Authenticated){ClearQuota();if(!state.IsSending)state.Status="请先登录 Codex 账号";}}Publish();}
     catch(Exception){/* Quota and model catalog remain usable independently. */}
    }
    if(force||(now-lastCatalogUtc).TotalMinutes>=10) {
     try {await ReadModelsAsync().ConfigureAwait(false);lastCatalogUtc=DateTime.UtcNow;}
     catch(Exception e){SetStatus("模型列表暂不可用："+SafeError(e.Message));}
    }
   }catch(Exception e){SetDisconnected(SafeError(e.Message));}
   finally{operations.Release();}
  }
  public async Task SendAsync(string text,string model,string effort,string savedChatThreadId) {
   if(String.IsNullOrWhiteSpace(text))throw new ArgumentException("请输入消息。");
   if(String.IsNullOrWhiteSpace(model))throw new ArgumentException("请先选择模型。");
   if(text.Length>64000)throw new ArgumentException("单条消息过长，请分段发送。");
   await operations.WaitAsync().ConfigureAwait(false);
   try {
    lock(gate){if(state.IsSending)throw new InvalidOperationException("上一条回复尚未结束。");}
    Directory.CreateDirectory(workspace);
    await EnsureStartedAsync().ConfigureAwait(false);
    string target;lock(gate)target=state.ChatThreadId;
    if(!String.IsNullOrWhiteSpace(savedChatThreadId)&&!String.Equals(target,savedChatThreadId,StringComparison.Ordinal)){
     await ReadChatAsync(savedChatThreadId).ConfigureAwait(false);target=savedChatThreadId;
    }
    var restrictions=Obj("model",model,"cwd",workspace,"sandbox","read-only","approvalPolicy","untrusted",
     "developerInstructions","你是白璃桌宠中的聊天助手。默认只进行文字对话。除非用户在当前消息中明确请求操作电脑、执行命令或修改文件，否则不要调用工具。不得自行请求扩大权限。不可声称你就是另一个已经打开的聊天或已经读取其全部上下文。" );
    if(String.IsNullOrWhiteSpace(target)){
     var started=await RequestAsync("thread/start",restrictions,20000).ConfigureAwait(false);target=Text(Map(Get(started,"thread")),"id");
     if(String.IsNullOrWhiteSpace(target))throw new InvalidDataException("服务器没有返回对话编号。");
     lock(gate){state.ChatThreadId=target;state.Messages.Clear();}resumedThreadId=target;Publish();
    }else if(!String.Equals(resumedThreadId,target,StringComparison.Ordinal)){
     restrictions["threadId"]=target;restrictions["excludeTurns"]=true;
     await RequestAsync("thread/resume",restrictions,20000).ConfigureAwait(false);resumedThreadId=target;
    }
    string userId="baili-user-"+Guid.NewGuid().ToString("N");
    lock(gate){state.SelectedModel=model;state.SelectedEffort=effort;state.IsSending=true;state.ActiveTurnId=null;streamingText.Clear();state.Status="正在等待回复…";state.Messages.Add(new LiveChatMessage{Id=userId,Role="user",Text=ConversationBridge.CleanVisibleText(text)??""});TrimMessages(state.Messages);}
    Publish();
    try {
     var args=Obj("threadId",target,"input",new object[]{Obj("type","text","text",text)},"model",model,"clientUserMessageId",userId);
     if(!String.IsNullOrWhiteSpace(effort))args["effort"]=effort;
     var result=await RequestAsync("turn/start",args,20000).ConfigureAwait(false);string turn=Text(Map(Get(result,"turn")),"id");
     lock(gate){if(state.IsSending&&String.IsNullOrEmpty(state.ActiveTurnId))state.ActiveTurnId=turn;}
     Publish();
    }catch(Exception e){lock(gate){state.IsSending=false;state.ActiveTurnId=null;state.Status="发送失败："+SafeError(e.Message);}Publish();throw;}
   }finally{operations.Release();}
  }
  public async Task InterruptAsync() {
   string thread,turn;lock(gate){thread=state.ChatThreadId;turn=state.ActiveTurnId;}
   if(String.IsNullOrEmpty(thread)||String.IsNullOrEmpty(turn))return;
   try{await RequestAsync("turn/interrupt",Obj("threadId",thread,"turnId",turn),10000).ConfigureAwait(false);lock(gate){if(state.IsSending&&state.ActiveTurnId==turn)state.Status="正在停止回复…";}Publish();}
   catch(Exception e){SetStatus("停止失败："+SafeError(e.Message));throw;}
  }
  public async Task<Uri> BeginLoginAsync() {
   await operations.WaitAsync().ConfigureAwait(false);
   try {
    await EnsureStartedAsync().ConfigureAwait(false);
    var result=await RequestAsync("account/login/start",Obj("type","chatgpt"),15000).ConfigureAwait(false);
    Uri uri;string url=Text(result,"authUrl");
    if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||!(uri.Host=="auth.openai.com"||uri.Host=="chatgpt.com"||uri.Host.EndsWith(".openai.com",StringComparison.OrdinalIgnoreCase)))
     throw new InvalidDataException("登录接口未返回有效的 OpenAI 登录地址。");
    SetStatus("请在浏览器完成登录");return uri;
   }finally{operations.Release();}
  }
  public async Task LoadChatAsync(string threadId) {
   if(String.IsNullOrWhiteSpace(threadId))return;
   await operations.WaitAsync().ConfigureAwait(false);
   try{lock(gate){if(state.IsSending)throw new InvalidOperationException("回复进行中，暂不能切换对话。");}await EnsureStartedAsync().ConfigureAwait(false);await ReadChatAsync(threadId).ConfigureAwait(false);}
   finally{operations.Release();}
  }
  async Task ReadChatAsync(string threadId) {
   var result=await RequestAsync("thread/read",Obj("threadId",threadId,"includeTurns",false),15000).ConfigureAwait(false);
   var thread=Map(Get(result,"thread"));if(thread==null)throw new InvalidDataException("未找到桌宠对话。");
   var messages=new List<LiveChatMessage>();bool legacy=false;
   try{
    var page=await RequestAsync("thread/items/list",Obj("threadId",threadId,"limit",120,"sortDirection","desc"),15000).ConfigureAwait(false);
    var entries=Items(Get(page,"data")).Reverse();
    foreach(object entry in entries){var map=Map(entry);ReadMessage(Map(Get(map,"item"))??map,messages);}
   }catch(RpcFailure e){if(e.Code!=-32601)throw;legacy=true;}
   if(legacy){var full=await RequestAsync("thread/read",Obj("threadId",threadId,"includeTurns",true),15000).ConfigureAwait(false);foreach(object turn in Items(Get(Map(Get(full,"thread")),"turns")))foreach(object item in Items(Get(Map(turn),"items")))ReadMessage(Map(item),messages);}
   lock(gate){state.ChatThreadId=threadId;state.Messages=messages;state.SelectedModel=Text(thread,"model")??state.SelectedModel;state.SelectedEffort=Text(thread,"reasoningEffort")??state.SelectedEffort;TrimMessages(state.Messages);state.Status="已载入桌宠对话";}Publish();
  }
  async Task ReadModelsAsync(){
   var models=new List<LiveModel>();string cursor=null;var seen=new HashSet<string>(StringComparer.Ordinal);
   do{
    var args=Obj("limit",32);if(cursor!=null)args["cursor"]=cursor;
    var page=await RequestAsync("model/list",args,15000).ConfigureAwait(false);
    foreach(object raw in Items(Get(page,"data"))){var value=Map(raw);string id=Text(value,"model")??Text(value,"id");if(String.IsNullOrEmpty(id)||models.Any(m=>m.Id==id))continue;var model=new LiveModel{Id=id,Name=Text(value,"displayName")??id,IsDefault=Bool(Get(value,"isDefault"))};foreach(object effort in Items(Get(value,"supportedReasoningEfforts"))){string name=Text(Map(effort),"reasoningEffort");if(!String.IsNullOrEmpty(name))model.Efforts.Add(name);}models.Add(model);}
    cursor=Text(page,"nextCursor");
   }while(cursor!=null&&models.Count<64&&seen.Add(cursor));
   lock(gate){state.Models=models.Take(64).ToList();if(String.IsNullOrEmpty(state.SelectedModel)){var primary=models.FirstOrDefault(m=>m.IsDefault)??models.FirstOrDefault();if(primary!=null)state.SelectedModel=primary.Id;}}
   Publish();
  }
  async Task EnsureStartedAsync(){
   lock(gate){if(disposed)throw new ObjectDisposedException("CodexConnection");if(initialized&&child!=null&&!child.HasExited)return;}
   string path=ResolveExecutable(executable);
   string arguments="app-server --listen stdio://";
   if(transportProviderOverride!=null)arguments+=" -c model_providers."+transportProviderOverride+".supports_websockets=false";
   var start=new ProcessStartInfo(path,arguments){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Directory.Exists(workspace)?workspace:AppDomain.CurrentDomain.BaseDirectory,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
   if(!String.IsNullOrWhiteSpace(codexHome))start.EnvironmentVariables["CODEX_HOME"]=codexHome;
   var process=new Process{StartInfo=start};if(!process.Start())throw new InvalidOperationException("无法启动 Codex 连接。");
   lock(gate){if(disposed){try{process.Kill();}catch{}throw new ObjectDisposedException("CodexConnection");}child=process;inputWriter=new StreamWriter(process.StandardInput.BaseStream,new UTF8Encoding(false)){AutoFlush=true};resumedThreadId=null;state.Connected=false;state.Status="正在连接 Codex…";}
   Publish();
   Task.Run(delegate{ReadLoop(process);});Task.Run(delegate{try{while(process.StandardError.ReadLine()!=null){/* Drain without collecting or logging potentially sensitive data. */}}catch{}});
   try{
    await RequestAsync("initialize",Obj("clientInfo",Obj("name","baili_companion","title","BaiLi companion","version","0.8.0")),15000).ConfigureAwait(false);Notify("initialized",null);
    if(transportProviderOverride==null){
     // The HTTP/SSE transport is supported by the same Responses provider.
     // Discover that provider through the public config RPC, then override
     // only this owned process. Never change provider, URL, auth or disk config.
     string provider=null;
     try{
      var configuration=Map(Get(await RequestAsync("config/read",Obj("includeLayers",false),8000).ConfigureAwait(false),"config"));
      string selected=Text(configuration,"model_provider")??"openai";
      var settings=Map(Get(Map(Get(configuration,"model_providers")),selected));
      string wire=Text(settings,"wire_api");object websocket=Get(settings,"supports_websockets");
      if(Regex.IsMatch(selected,@"^[A-Za-z0-9_-]{1,80}$")&&(wire==null||wire=="responses")&&!(websocket is bool&&!(bool)websocket))provider=selected;
     }catch{ /* Older servers may not expose config/read; use their default. */ }
     if(provider!=null){
      transportProviderOverride=provider;
      lock(gate){if(Object.ReferenceEquals(child,process)){child=null;inputWriter=null;initialized=false;}}
      StopChild(process);await EnsureStartedAsync().ConfigureAwait(false);return;
     }
    }
    lock(gate){initialized=true;state.Connected=true;state.Status="已连接 Codex";}Publish();
   }
   catch{StopChild(process);throw;}
  }
  async Task<Dictionary<string,object>> RequestAsync(string method,Dictionary<string,object> args,int timeout){
   int id=Interlocked.Increment(ref nextId);var completion=new TaskCompletionSource<Dictionary<string,object>>((TaskCreationOptions)64);
   lock(gate){if(disposed)throw new ObjectDisposedException("CodexConnection");pending[id]=completion;}
   try{
    var message=Obj("id",id,"method",method);if(args!=null)message["params"]=args;Write(message);
    if(await Task.WhenAny(completion.Task,Task.Delay(timeout)).ConfigureAwait(false)!=completion.Task)throw new TimeoutException("接口响应超时（"+method+"）");
    return await completion.Task.ConfigureAwait(false);
   }finally{lock(gate)pending.Remove(id);}
  }
  void Notify(string method,Dictionary<string,object> args){var m=Obj("method",method);if(args!=null)m["params"]=args;Write(m);}
  void Write(Dictionary<string,object> message){string line=Json().Serialize(message);lock(writeGate){Process process;StreamWriter writer;lock(gate){process=child;writer=inputWriter;}if(process==null||process.HasExited||writer==null)throw new IOException("Codex 连接已断开。");writer.WriteLine(line);writer.Flush();}}
  void ReadLoop(Process process){
   try{
    string line;while((line=process.StandardOutput.ReadLine())!=null){
     Dictionary<string,object> message;try{message=Json().Deserialize<Dictionary<string,object>>(line);}catch{continue;}
     if(message==null)continue;
     if(message.ContainsKey("id")&&!message.ContainsKey("method")){
      int id;TaskCompletionSource<Dictionary<string,object>> waiter=null;if(Int32.TryParse(Convert.ToString(Get(message,"id"),CultureInfo.InvariantCulture),out id))lock(gate)pending.TryGetValue(id,out waiter);
      if(waiter!=null){var error=Map(Get(message,"error"));if(error!=null)waiter.TrySetException(new RpcFailure(Integer(Get(error,"code")),SafeError(Text(error,"message"))));else waiter.TrySetResult(Map(Get(message,"result"))??new Dictionary<string,object>());}
     }else if(message.ContainsKey("id"))DenyServerRequest(message);
     else HandleNotification(Text(message,"method"),Map(Get(message,"params")));
    }
   }catch(Exception){/* Connection closure is surfaced below, never raw wire data. */}
   finally{
    TaskCompletionSource<Dictionary<string,object>>[] ended=null;
    lock(gate){if(Object.ReferenceEquals(child,process)){child=null;inputWriter=null;initialized=false;resumedThreadId=null;state.Connected=false;state.IsSending=false;state.ActiveTurnId=null;state.QuotaRefreshFailed=state.QuotaObservedUtc.HasValue;state.Status="Codex 连接已断开，可点刷新重连。";ended=pending.Values.ToArray();pending.Clear();}}
    if(ended!=null){foreach(var waiter in ended)waiter.TrySetException(new IOException("Codex 连接已断开。"));Publish();}
    try{process.Dispose();}catch{}
   }
  }
  void DenyServerRequest(Dictionary<string,object> message){
   string method=Text(message,"method");object result;
   if(method=="item/commandExecution/requestApproval"||method=="item/fileChange/requestApproval"||method=="execCommandApproval"||method=="applyPatchApproval")result=Obj("decision","decline");
   else if(method=="item/permissions/requestApproval")result=Obj("permissions",Obj(),"scope","turn");
   else if(method=="item/tool/requestUserInput")result=Obj("answers",Obj());
   else if(method=="item/tool/call")result=Obj("success",false,"contentItems",new object[]{Obj("type","inputText","text","桌宠聊天不执行未经确认的工具请求。")});
   else{try{Write(Obj("id",Get(message,"id"),"error",Obj("code",-32601,"message","This companion client does not implement this server request.")));}catch{}return;}
   try{Write(Obj("id",Get(message,"id"),"result",result));}catch{}
   SetStatus("已拒绝额外操作请求；请在主客户端处理需要权限的任务。");
  }
  void HandleNotification(string method,Dictionary<string,object> data){
   if(data==null)return;bool changed=false;
   lock(gate){
    if(method=="account/rateLimits/updated"){ApplyQuota(state,data,true);lastQuotaUtc=DateTime.UtcNow;changed=true;}
    else if(method=="account/updated"){state.Authenticated=!String.IsNullOrEmpty(Text(data,"authMode"));ClearQuota();state.Models.Clear();lastAccountUtc=lastCatalogUtc=lastQuotaUtc=lastQuotaAttemptUtc=DateTime.MinValue;state.Status=state.Authenticated?"账号已更新，请刷新额度":"账号已退出，请重新登录";changed=true;}
    else if(method=="account/login/completed"){state.Status=Bool(Get(data,"success"))?"登录完成，请刷新额度":"登录未完成";state.Authenticated=Bool(Get(data,"success"));ClearQuota();lastAccountUtc=lastQuotaUtc=lastQuotaAttemptUtc=DateTime.MinValue;changed=true;}
    else if(Text(data,"threadId")==state.ChatThreadId&&state.ChatThreadId!=null){
     if(method=="turn/started"){state.ActiveTurnId=Text(Map(Get(data,"turn")),"id");state.IsSending=true;state.Status="正在回复…";changed=true;}
     else if(method=="item/agentMessage/delta"){string id=Text(data,"itemId"),delta=Text(data,"delta")??"";if(id==null)return;string raw;streamingText.TryGetValue(id,out raw);raw=(raw??"")+delta;streamingText[id]=raw;var item=state.Messages.FirstOrDefault(m=>m.Id==id&&m.Role=="assistant");if(item==null){item=new LiveChatMessage{Id=id,Role="assistant",Text=""};state.Messages.Add(item);}item.Text=ConversationBridge.CleanVisibleText(raw)??"";state.Status="正在回复…";changed=true;}
     else if(method=="item/completed"){var item=Map(Get(data,"item"));if(Text(item,"type")=="agentMessage"){string id=Text(item,"id");var found=state.Messages.FirstOrDefault(m=>m.Id==id&&m.Role=="assistant");string visible=ConversationBridge.CleanVisibleText(Text(item,"text"));if(found==null&&visible!=null)state.Messages.Add(new LiveChatMessage{Id=id,Role="assistant",Text=visible});else if(found!=null)found.Text=visible??"";if(id!=null)streamingText.Remove(id);changed=true;}}
     else if(method=="turn/completed"){
      var turn=Map(Get(data,"turn"));string id=Text(turn,"id");
      if(state.ActiveTurnId==null||state.ActiveTurnId==id){state.IsSending=false;state.ActiveTurnId=null;string status=Text(turn,"status");state.Status=status=="completed"?"回复完成":status=="interrupted"?"回复已停止":"回复失败："+SafeError(Text(Map(Get(turn,"error")),"message")??"未知错误");changed=true;}
     }
     else if(method=="error"){var error=Map(Get(data,"error"));state.Status="回复出错："+SafeError(Text(error,"message")??"未知错误");if(!Bool(Get(data,"willRetry"))){state.IsSending=false;state.ActiveTurnId=null;}changed=true;}
     TrimMessages(state.Messages);
    }
   }
   if(changed)Publish();
  }
  internal static void ApplyQuota(LiveConnectionSnapshot state,Dictionary<string,object> payload,bool partial){
   var map=Map(Get(payload,"rateLimitsByLimitId"));var limits=Map(Get(map,"codex"))??Map(Get(payload,"rateLimits"));
   if(limits==null){if(!partial){state.Primary=state.Secondary=null;state.QuotaObservedUtc=null;state.QuotaRefreshFailed=false;}return;}
   if(!partial||limits.ContainsKey("primary"))state.Primary=ReadWindow(Get(limits,"primary"),partial?state.Primary:null);
   if(!partial||limits.ContainsKey("secondary"))state.Secondary=ReadWindow(Get(limits,"secondary"),partial?state.Secondary:null);
   state.QuotaObservedUtc=DateTime.UtcNow;state.Authenticated=true;state.QuotaRefreshFailed=false;
  }
  static UsageWindow ReadWindow(object raw,UsageWindow previous){
   var map=Map(raw);if(map==null)return null;
   double? used=map.ContainsKey("usedPercent")?Number(Get(map,"usedPercent")):previous==null?null:previous.UsedPercent;
   if(used.HasValue&&(used<0||used>100))used=null;
   double? minutes=map.ContainsKey("windowDurationMins")?Number(Get(map,"windowDurationMins")):previous==null?null:previous.WindowMinutes;
   DateTime? reset=previous==null?null:previous.ResetsAtUtc;
   if(map.ContainsKey("resetsAt")){reset=null;double? seconds=Number(Get(map,"resetsAt"));if(seconds.HasValue&&seconds>=0&&seconds<253402300799)try{reset=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(seconds.Value);}catch{}}
   return new UsageWindow{UsedPercent=used,WindowMinutes=minutes.HasValue&&minutes>0&&minutes<=Int32.MaxValue&&Math.Floor(minutes.Value)==minutes.Value?(int?)minutes.Value:null,ResetsAtUtc=reset};
  }
  internal static void ReadMessage(Dictionary<string,object> item,List<LiveChatMessage> target){
   string type=Text(item,"type"),role=null,text=null;if(type=="agentMessage"){string phase=Text(item,"phase");if(!String.IsNullOrEmpty(phase)&&phase!="commentary"&&phase!="final_answer"&&phase!="final"&&phase!="answer")return;role="assistant";text=Text(item,"text");}else if(type=="userMessage"){role="user";var parts=new List<string>();foreach(object part in Items(Get(item,"content"))){var value=Map(part);if(Text(value,"type")=="text")parts.Add(Text(value,"text")??"");}text=String.Join("\n",parts);}
   text=ConversationBridge.CleanVisibleText(text);if(role!=null&&!String.IsNullOrEmpty(text))target.Add(new LiveChatMessage{Id=Text(item,"id"),Role=role,Text=text});
  }
  static void TrimMessages(List<LiveChatMessage> messages){if(messages.Count>240)messages.RemoveRange(0,messages.Count-240);}
  void ClearQuota(){state.Primary=state.Secondary=null;state.QuotaObservedUtc=null;state.QuotaRefreshFailed=false;}
  void SetStatus(string text){lock(gate){if(disposed)return;state.Status=text;}Publish();}
  void SetDisconnected(string reason){lock(gate){if(disposed)return;initialized=false;state.Connected=false;state.IsSending=false;state.ActiveTurnId=null;state.QuotaRefreshFailed=state.QuotaObservedUtc.HasValue;state.Status=reason;}Publish();}
  void Publish(){var handler=Changed;if(handler!=null)try{handler();}catch{}}
  void FailPending(Exception error){TaskCompletionSource<Dictionary<string,object>>[] waiters;lock(gate){waiters=pending.Values.ToArray();pending.Clear();}foreach(var waiter in waiters)waiter.TrySetException(error);}
  public void Dispose(){Process process;lock(gate){if(disposed)return;disposed=true;process=child;child=null;initialized=false;state.Connected=false;state.IsSending=false;state.ActiveTurnId=null;}FailPending(new ObjectDisposedException("CodexConnection"));StopChild(process);}
  static void StopChild(Process process){if(process==null)return;try{process.StandardInput.Close();if(!process.WaitForExit(500))process.Kill();}catch{}try{process.Dispose();}catch{}}
  internal static string ResolveExecutable(string explicitPath){
   if(!String.IsNullOrWhiteSpace(explicitPath)){string full=Path.GetFullPath(explicitPath);if(File.Exists(full)&&Path.GetExtension(full).Equals(".exe",StringComparison.OrdinalIgnoreCase))return full;throw new FileNotFoundException("配置的 Codex 程序不存在或不是 EXE。");}
   string npm=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe");
   if(File.Exists(npm))return npm;
   foreach(string directory in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){try{string candidate=Path.Combine(directory.Trim('"'),"codex.exe");if(File.Exists(candidate))return Path.GetFullPath(candidate);}catch{}}
   throw new FileNotFoundException("未找到 Codex CLI，请先安装并登录 Codex。");
  }
  static LiveConnectionSnapshot Clone(LiveConnectionSnapshot s){return new LiveConnectionSnapshot{Connected=s.Connected,Authenticated=s.Authenticated,IsSending=s.IsSending,QuotaRefreshFailed=s.QuotaRefreshFailed,Status=s.Status,ChatThreadId=s.ChatThreadId,ActiveTurnId=s.ActiveTurnId,SelectedModel=s.SelectedModel,SelectedEffort=s.SelectedEffort,QuotaObservedUtc=s.QuotaObservedUtc,Primary=Copy(s.Primary),Secondary=Copy(s.Secondary),Models=s.Models.Select(m=>new LiveModel{Id=m.Id,Name=m.Name,IsDefault=m.IsDefault,Efforts=new List<string>(m.Efforts)}).ToList(),Messages=s.Messages.Select(m=>new LiveChatMessage{Id=m.Id,Role=m.Role,Text=m.Text}).ToList()};}
  static UsageWindow Copy(UsageWindow w){return w==null?null:new UsageWindow{UsedPercent=w.UsedPercent,WindowMinutes=w.WindowMinutes,ResetsAtUtc=w.ResetsAtUtc};}
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=80};}
  internal static Dictionary<string,object> Obj(params object[] pairs){var value=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)value.Add((string)pairs[i],pairs[i+1]);return value;}
  internal static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>;}
  internal static object Get(Dictionary<string,object> map,string key){object value;return map!=null&&map.TryGetValue(key,out value)?value:null;}
  internal static string Text(Dictionary<string,object> map,string key){return Get(map,key) as string;}
  internal static IEnumerable<object> Items(object value){var a=value as IEnumerable;if(a==null||value is string||value is IDictionary)yield break;foreach(object item in a)yield return item;}
  static bool Bool(object value){return value is bool&&(bool)value;}
  static int Integer(object value){int result;return Int32.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out result)?result:0;}
  static double? Number(object value){if(value==null||value is bool)return null;double n;return Double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&!Double.IsNaN(n)&&!Double.IsInfinity(n)?n:(double?)null;}
  static string SafeError(string text){text=text??"未知错误";text=Regex.Replace(text,@"(?i)(Bearer\s+\S+|sk-[A-Za-z0-9_-]+|eyJ[A-Za-z0-9_.-]+)","[已隐藏]");return text.Length>300?text.Substring(0,300):text;}
  sealed class RpcFailure:Exception{public readonly int Code;public RpcFailure(int code,string message):base(message){Code=code;}}
 }
}
