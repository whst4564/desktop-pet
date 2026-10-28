using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CodexCompanion;

public static class CodexConnectionTests {
 static int checks;
 static JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=16000000};
 static string testRoot;
 static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
 static void Wait(Func<bool> condition,string message){var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<4000)Thread.Sleep(10);if(!condition())throw new Exception(message);}
 static string Folder(string name){string path=Path.Combine(testRoot,name);Directory.CreateDirectory(path);return path;}
 static CodexConnection Create(string mode){string path=Folder(mode);File.WriteAllText(Path.Combine(path,"mode.txt"),mode);return new CodexConnection(Assembly.GetExecutingAssembly().Location,path,path);}
 static string[] Audit(string mode){string path=Path.Combine(Folder(mode),"audit.txt");return File.Exists(path)?File.ReadAllLines(path):new string[0];}
 public static int Main(string[] args){
  Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
  if(args.Length>0&&args[0]=="app-server"){FakeServer();return 0;}
  if(args.Contains("--live-probe"))return LiveProbe();
  testRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"rpc-fixtures-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testRoot);
  try{
   using(var c=Create("basic")){
    c.RefreshAsync(true).GetAwaiter().GetResult();var s=c.Snapshot;
    Check(s.Connected&&s.Authenticated,"actual stdio handshake reaches an authenticated fake account");
    Check(Audit("basic").Contains("HTTP_CHILD_OVERRIDE")&&Audit("basic").Count(a=>a=="initialize")==2,"HTTP transport override targets the discovered provider in an owned child restart only");
    Check(s.Primary.RemainingPercent==73&&s.Secondary.RemainingPercent==81,"live quota uses used-percent inversion for both windows");
    Check(s.Primary.WindowMinutes==300&&s.Secondary.WindowMinutes==10080&&s.QuotaObservedUtc.HasValue,"quota keeps server window durations, reset and observation time");
    Check(s.Models.Count==2&&s.Models[1].Id=="model-two"&&s.Models[0].Efforts.SequenceEqual(new[]{"low","high"}),"model catalog follows pagination and preserves exact model IDs/efforts");
    s.Primary.UsedPercent=99;s.Models[0].Efforts.Clear();s.Models.Clear();s.Messages.Add(new LiveChatMessage{Text="modified"});
    Check(c.Snapshot.Primary.UsedPercent==27&&c.Snapshot.Models.Count==2&&c.Snapshot.Models[0].Efforts.Count==2&&c.Snapshot.Messages.Count==0,"snapshot is fully detached from live mutable state");
    Check(!Audit("basic").Any(a=>a=="thread/start"||a=="turn/start"),"startup and quota refresh never create a conversation or model turn");
    int quotaBefore=Audit("basic").Count(a=>a=="account/rateLimits/read");c.RefreshAsync(false).GetAwaiter().GetResult();
    Check(Audit("basic").Count(a=>a=="account/rateLimits/read")==quotaBefore,"background refresh obeys the thirty-second quota interval");
    bool rejected=false;try{c.SendAsync(" ","model-one","low",null).GetAwaiter().GetResult();}catch(ArgumentException){rejected=true;}
    Check(rejected&&!Audit("basic").Contains("turn/start"),"empty input is rejected before any request is sent");
    c.SendAsync("你好🌸","model-two","high",null).GetAwaiter().GetResult();Wait(()=>!c.Snapshot.IsSending,"stream did not complete");s=c.Snapshot;
    Check(s.Messages.Count==2&&s.Messages[0].Role=="user"&&s.Messages[1].Role=="assistant"&&s.Messages[1].Text=="你好🌸，连接正常。","UTF-8 Chinese and emoji stream correctly and final message replaces deltas without duplication");
    Check(s.SelectedModel=="model-two"&&s.SelectedEffort=="high"&&s.ChatThreadId=="owned-thread"&&s.Status=="回复完成","the exact user-selected model and effort reach a completed owned chat");
    Check(Audit("basic").Contains("READONLY_UNTRUSTED")&&Audit("basic").Contains("MODEL_EFFORT_UNCHANGED"),"new chat uses read-only/untrusted policy and never silently substitutes its model");
    Check(s.Primary.UsedPercent==31&&s.Secondary.UsedPercent==19&&s.Primary.WindowMinutes==300&&s.Primary.ResetsAtUtc.HasValue,"sparse quota notifications merge with existing window metadata and secondary quota");
    c.SendAsync("第二句","model-two","low",s.ChatThreadId).GetAwaiter().GetResult();Wait(()=>!c.Snapshot.IsSending,"second stream did not complete");
    Check(Audit("basic").Count(a=>a=="thread/start")==1,"subsequent messages reuse the same owned conversation");
   }
   using(var c=Create("history")){
    c.LoadChatAsync("saved-owned-thread").GetAwaiter().GetResult();var s=c.Snapshot;
    Check(s.Messages.Count==2&&s.Messages[0].Text=="历史问题"&&s.Messages[1].Text=="历史回答","history pages are reversed to chronological order and contain only public user/assistant text");
    Check(!Audit("history").Contains("thread/resume")&&!Audit("history").Contains("turn/start"),"opening stored chat is read-only and does not resume or send");
    c.SendAsync("接着聊","model-one","low","saved-owned-thread").GetAwaiter().GetResult();Wait(()=>!c.Snapshot.IsSending,"resume stream did not complete");
    Check(Audit("history").Contains("thread/resume")&&!Audit("history").Contains("thread/start"),"a user send resumes the saved companion chat rather than creating another thread");
   }
   using(var c=Create("hold")){
    c.SendAsync("等待中","model-one","low",null).GetAwaiter().GetResult();Wait(()=>c.Snapshot.ActiveTurnId!=null,"no active turn");
    Check(c.Snapshot.IsSending,"a started turn remains busy until a terminal event");c.InterruptAsync().GetAwaiter().GetResult();Wait(()=>!c.Snapshot.IsSending,"interrupt did not complete");
    Check(c.Snapshot.Status=="回复已停止"&&Audit("hold").Contains("turn/interrupt"),"interrupt requests the owned active turn and distinguishes interruption from success");
   }
   using(var c=Create("approvals")){
    c.SendAsync("测试权限","model-one","low",null).GetAwaiter().GetResult();Wait(()=>!c.Snapshot.IsSending,"approval stream did not complete");
    Check(Audit("approvals").Contains("APPROVAL_DECLINED")&&Audit("approvals").Contains("PERMISSIONS_EMPTY"),"server command and permission requests never receive automatic authorization");
   }
   using(var c=Create("failure")){
    c.SendAsync("失败状态","model-one","low",null).GetAwaiter().GetResult();Wait(()=>!c.Snapshot.IsSending,"failure not terminal");
    Check(c.Snapshot.Status.Contains("回复失败")&&!c.Snapshot.Status.Contains("Bearer secret-value"),"failed turns clear busy state and redact credential-like error text");
   }
   using(var c=Create("crash")){
    bool failed=false;try{c.SendAsync("崩溃测试","model-one","low",null).GetAwaiter().GetResult();}catch(IOException){failed=true;}
    Wait(()=>!c.Snapshot.IsSending,"crash left busy state");Check(failed&&!c.Snapshot.Connected,"child death fails the pending request and releases busy state");
   }
   using(var c=Create("login")){
    var url=c.BeginLoginAsync().GetAwaiter().GetResult();Check(url.Host=="auth.openai.com"&&Audit("login").Contains("account/login/start")&&!Audit("login").Contains("turn/start"),"explicit login returns an official browser URL without sending model input");
   }
   using(var c=Create("weekly")){
    c.RefreshAsync(true).GetAwaiter().GetResult();Check(c.Snapshot.Primary.WindowMinutes==10080&&c.Snapshot.Secondary==null,"weekly-only server data remains weekly and does not invent a short window");
   }
   using(var c=Create("quota-failure")){
    c.RefreshAsync(true).GetAwaiter().GetResult();c.RefreshAsync(true).GetAwaiter().GetResult();var snapshot=c.Snapshot;
    Check(snapshot.Connected&&snapshot.QuotaRefreshFailed&&snapshot.Primary.RemainingPercent==73,"a failed quota refresh retains cached data and marks it stale independently of connection state");
    int before=Audit("quota-failure").Count(a=>a=="account/rateLimits/read");c.RefreshAsync(false).GetAwaiter().GetResult();
    Check(Audit("quota-failure").Count(a=>a=="account/rateLimits/read")==before&&c.Snapshot.QuotaRefreshFailed,"quota failure backoff prevents retrying the backend on every UI poll");
    c.RefreshAsync(true).GetAwaiter().GetResult();Check(!c.Snapshot.QuotaRefreshFailed,"a later successful quota fetch clears the cached-data failure marker");
   }
   var disposed=Create("disposed");disposed.Dispose();disposed.RefreshAsync(true).GetAwaiter().GetResult();Check(!disposed.Snapshot.Connected,"disposing prevents an implicit child restart");
   var parsed=new LiveConnectionSnapshot{Primary=new UsageWindow{UsedPercent=50}};
   CodexConnection.ApplyQuota(parsed,Obj("rateLimitsByLimitId",Obj("unrelated-model",Obj("primary",Obj("usedPercent",20,"windowDurationMins",300)))),false);
   Check(parsed.Primary==null&&parsed.Secondary==null&&!parsed.QuotaObservedUtc.HasValue,"a model-specific bucket is never mislabeled as general account quota");
   CodexConnection.ApplyQuota(parsed,Obj("rateLimits",Obj("primary",Obj("usedPercent",101,"windowDurationMins",300),"secondary",Obj("usedPercent",-1,"windowDurationMins",10080))),false);
   Check(parsed.Primary.RemainingPercent==null&&parsed.Secondary.RemainingPercent==null,"out-of-range server percentages remain unknown rather than being clamped to full or empty");
   var visible=new List<LiveChatMessage>();
   CodexConnection.ReadMessage(Obj("type","userMessage","id","public","content",new object[]{Obj("type","text","text","<environment_context>hidden</environment_context>\n可见问题")}),visible);
   Check(visible.Count==1&&visible[0].Text=="可见问题","history strips hidden environment context before presenting the user message");
   CodexConnection.ReadMessage(Obj("type","agentMessage","id","hidden","phase","analysis","text","not visible"),visible);
   Check(visible.Count==1,"history excludes assistant analysis even if it appears in a message item");
   Console.WriteLine("ALL "+checks+" CODEX CONNECTION CHECKS PASSED");return 0;
  }catch(Exception e){Console.WriteLine("FAILED "+e);return 1;}
 }
 static int LiveProbe(){
  using(var c=new CodexConnection(null,new DataBridge().CodexHome,Directory.GetCurrentDirectory())){
   c.RefreshAsync(true).GetAwaiter().GetResult();var s=c.Snapshot;
   Console.WriteLine(json.Serialize(new{connected=s.Connected,authenticated=s.Authenticated,status=s.Status,models=s.Models.Count,selectedModel=s.SelectedModel,quotaObservedUtc=s.QuotaObservedUtc,primaryRemaining=s.Primary==null?null:s.Primary.RemainingPercent,secondaryRemaining=s.Secondary==null?null:s.Secondary.RemainingPercent,inferenceStarted=false,credentialsReadByClient=false}));
   return s.Connected&&s.Authenticated&&s.QuotaObservedUtc.HasValue?0:1;
  }
 }
 static Dictionary<string,object> Obj(params object[] args){var map=new Dictionary<string,object>();for(int i=0;i<args.Length;i+=2)map[(string)args[i]]=args[i+1];return map;}
 static Dictionary<string,object> Map(object v){return v as Dictionary<string,object>;}
 static object Get(Dictionary<string,object> d,string k){object v;return d!=null&&d.TryGetValue(k,out v)?v:null;}
 static string Str(Dictionary<string,object> d,string k){return Get(d,k) as string;}
 static void Out(object value){Console.WriteLine(json.Serialize(value));Console.Out.Flush();}
 static void Reply(object id,object result){Out(Obj("id",id,"result",result));}
 static void Note(string method,object args){Out(Obj("method",method,"params",args));}
 static void FakeServer(){
  string home=Environment.GetEnvironmentVariable("CODEX_HOME"),mode=File.ReadAllText(Path.Combine(home,"mode.txt"));string audit=Path.Combine(home,"audit.txt");bool initialized=false;string thread="owned-thread";int number=0,quotaReads=0;string heldTurn=null;
  Action<string> log=delegate(string m){File.AppendAllText(audit,m+Environment.NewLine);};
  bool httpOverride=Environment.GetCommandLineArgs().Contains("model_providers.custom.supports_websockets=false");if(httpOverride)log("HTTP_CHILD_OVERRIDE");
  long future=(long)(DateTime.UtcNow.AddHours(3)-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
  string line;while((line=Console.ReadLine())!=null){
   var msg=json.Deserialize<Dictionary<string,object>>(line);var method=Str(msg,"method");var id=Get(msg,"id");var args=Map(Get(msg,"params"));
   if(method==null){var result=Map(Get(msg,"result"));if(Convert.ToString(id)=="command"&&Str(result,"decision")=="decline")log("APPROVAL_DECLINED");if(Convert.ToString(id)=="permission"&&Map(Get(result,"permissions")).Count==0)log("PERMISSIONS_EMPTY");continue;}
   log(method);
   if(method=="initialize"){Note("remoteControl/status/changed",Obj("status","disabled"));Reply(id,Obj("userAgent","fake-test","codexHome",home));continue;}
   if(method=="initialized"){initialized=true;continue;}
   if(!initialized){Out(Obj("id",id,"error",Obj("code",-32600,"message","Not initialized")));continue;}
   if(method=="config/read"){Reply(id,Obj("config",Obj("model_provider","custom","model_providers",Obj("custom",Obj("wire_api","responses","supports_websockets",!httpOverride)))));continue;}
   if(method=="account/rateLimits/read"){
    if(mode=="quota-failure"&&++quotaReads==2){Out(Obj("id",id,"error",Obj("code",-32603,"message","Temporary usage backend failure")));continue;}
    object primary=Obj("usedPercent",27,"windowDurationMins",mode=="weekly"?10080:300,"resetsAt",future),secondary=mode=="weekly"?null:Obj("usedPercent",19,"windowDurationMins",10080,"resetsAt",future+86400);
    Reply(id,Obj("rateLimits",Obj("primary",Obj("usedPercent",99)),"rateLimitsByLimitId",Obj("codex",Obj("primary",primary,"secondary",secondary,"planType","plus"))));
   }else if(method=="account/read")Reply(id,Obj("account",Obj("type","chatgpt","planType","plus"),"requiresOpenaiAuth",true));
   else if(method=="model/list"){
    bool second=Get(args,"cursor")!=null;Reply(id,Obj("data",new object[]{Obj("id",second?"model-two":"model-one","model",second?"model-two":"model-one","displayName","Synthetic model","isDefault",!second,"supportedReasoningEfforts",new object[]{Obj("reasoningEffort","low"),Obj("reasoningEffort","high")})},"nextCursor",second?null:"page2"));
   }else if(method=="thread/read")Reply(id,Obj("thread",Obj("id",Get(args,"threadId"),"model","model-one","reasoningEffort","low","turns",new object[0])));
   else if(method=="thread/items/list")Reply(id,Obj("data",new object[]{Obj("item",Obj("id","answer-old","type","agentMessage","text","历史回答")),Obj("item",Obj("id","reasoning-old","type","reasoning","text","不应显示")),Obj("item",Obj("id","user-old","type","userMessage","content",new object[]{Obj("type","text","text","历史问题")}))},"nextCursor",null));
   else if(method=="thread/start"||method=="thread/resume"){
    if(Str(args,"sandbox")=="read-only"&&Str(args,"approvalPolicy")=="untrusted")log("READONLY_UNTRUSTED");else throw new Exception("Unsafe sandbox requested");
    if(method=="thread/resume")thread=Str(args,"threadId");Reply(id,Obj("thread",Obj("id",thread)));
   }else if(method=="turn/start"){
    if(mode=="crash")return;
    var input=((System.Collections.IList)Get(args,"input"))[0] as Dictionary<string,object>;string text=Str(input,"text");
    if(Str(args,"model")=="model-two"&&(Str(args,"effort")=="high"||Str(args,"effort")=="low"))log("MODEL_EFFORT_UNCHANGED");
    if(text==null||text.Contains("�"))throw new Exception("Invalid UTF-8 user input");
    string turn="turn-"+(++number);heldTurn=turn;
    Note("turn/started",Obj("threadId",thread,"turn",Obj("id",turn,"status","inProgress")));Reply(id,Obj("turn",Obj("id",turn,"status","inProgress")));
    if(mode=="hold")continue;
    if(mode=="approvals"){Out(Obj("id","command","method","item/commandExecution/requestApproval","params",Obj("threadId",thread)));Out(Obj("id","permission","method","item/permissions/requestApproval","params",Obj("threadId",thread)));}
    string item="agent-"+number;
    Note("item/agentMessage/delta",Obj("threadId",thread,"turnId",turn,"itemId",item,"delta","你好🌸"));
    Note("item/agentMessage/delta",Obj("threadId",thread,"turnId",turn,"itemId",item,"delta","，连接正常。"));
    Note("item/completed",Obj("threadId",thread,"turnId",turn,"item",Obj("id",item,"type","agentMessage","text","你好🌸，连接正常。")));
    Note("account/rateLimits/updated",Obj("rateLimits",Obj("primary",Obj("usedPercent",31))));
    Note("turn/completed",Obj("threadId",thread,"turn",Obj("id",turn,"status",mode=="failure"?"failed":"completed","error",mode=="failure"?Obj("message","Bearer secret-value invalid"):null)));
   }else if(method=="turn/interrupt"){Reply(id,Obj());Note("turn/completed",Obj("threadId",thread,"turn",Obj("id",heldTurn,"status","interrupted")));}
   else if(method=="account/login/start")Reply(id,Obj("type","chatgpt","loginId","synthetic-login","authUrl","https://auth.openai.com/oauth/authorize?test=synthetic"));
   else Out(Obj("id",id,"error",Obj("code",-32601,"message","Unsupported test method")));
  }
 }
}
