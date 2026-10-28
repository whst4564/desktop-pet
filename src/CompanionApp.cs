using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace CodexCompanion {
 public sealed class CompanionConfig {
  public int SchemaVersion=1;
  public double PetHeight=224;
  public double Left=Double.NaN, Top=Double.NaN;
  public bool FollowNativeVisibility=false;
  public bool FollowNativeSize=true;
  public bool ShowTrayIcon=false;
  public bool SmoothAnimations=true;
  public bool AttachNativeControls=true;
  public bool Topmost=true;
  public string PreferredThreadId=null;
  public string CodexHome=null;
  public string CodexExecutable=null;
  public string ChatThreadId=null;
  public List<ExtraAction> Actions=new List<ExtraAction>();
  public List<MotionGroup> InteractionGroups=new List<MotionGroup>();
  public List<SpriteClip> SpriteClips=new List<SpriteClip>();
 }
 public sealed class ExtraAction { public string Label; public string Target; }
 public sealed class MotionGroup { public string Name; public List<MotionStep> Steps=new List<MotionStep>(); }
 public sealed class MotionStep { public string Clip; public double Seconds; }
 public sealed class SpriteClip { public string Id; public string File; public int Frames=1; public int CellWidth=192,CellHeight=208; }
 public static class Program {
  public static string Root=AppDomain.CurrentDomain.BaseDirectory;
  public static string ConfigPath { get { return Path.Combine(Root,"settings.json"); } }
  public static CompanionConfig Config;
  public static bool ReviewMode;
  static Mutex instance;
  [STAThread] public static void Main(string[] args) {
   try {
    ReviewMode=args.Contains("--ui-review");
    if(args.Contains("--check-chat")){CheckChat();return;}
    if(args.Contains("--check-connection")){CheckConnection();return;}
    if(args.Contains("--export-connection-preview")){MotionPreviewExporter.ExportConnectionPreview(Path.GetFullPath(Path.Combine(Root,"../artifacts/connection-preview")));return;}
    if(args.Contains("--check-assets")) { CheckAssets(); return; }
   if(args.Contains("--export-motion-preview")){MotionPreviewExporter.Export(Path.GetFullPath(Path.Combine(Root,"../artifacts/motion-preview")));return;}
    bool created; instance=new Mutex(true,"Local\\BailiCompanion.WhiteGirl.v1",out created);
    if(!created) return;
    Config=LoadConfig();
    Application app=new Application(); app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
    app.DispatcherUnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs e) {
     File.AppendAllText(Path.Combine(Root,"diagnostics.log"),DateTime.Now.ToString("s")+" "+e.Exception+Environment.NewLine);e.Handled=true;
    };
    CompanionWindow pet=new CompanionWindow(); app.Run();
   } catch(Exception e) { File.AppendAllText(Path.Combine(Root,"diagnostics.log"),e.ToString());if(args.Contains("--export-motion-preview")||args.Contains("--export-connection-preview")||args.Contains("--check-connection")||args.Contains("--check-chat")||args.Contains("--check-assets")){Environment.ExitCode=1;return;} Forms.MessageBox.Show(e.Message,"白璃桌宠"); }
  }
  public static CompanionConfig LoadConfig() {
   try { if(File.Exists(ConfigPath)) return new JavaScriptSerializer().Deserialize<CompanionConfig>(File.ReadAllText(ConfigPath))??new CompanionConfig(); } catch {}
   return new CompanionConfig();
  }
  public static void SaveConfig() {
   try { File.WriteAllText(ConfigPath,new JavaScriptSerializer().Serialize(Config),new System.Text.UTF8Encoding(false)); } catch {}
  }
  public static void Open(string uri) {
   try { Process.Start(new ProcessStartInfo(uri){UseShellExecute=true}); }
   catch(Exception e) { Forms.MessageBox.Show(e.Message,"无法打开入口"); }
  }
  public static void CheckAssets() {
   BitmapImage atlas=LoadAtlas();
   if(atlas.PixelWidth!=1536 || atlas.PixelHeight!=2288) throw new InvalidDataException("需要 v2 1536×2288 图集");
   File.WriteAllText(Path.Combine(Root,"asset-check.json"),"{\"ok\":true,\"width\":1536,\"height\":2288,\"frames\":73}");
  }
  public static BitmapImage LoadAtlas() {
   BitmapImage img=new BitmapImage();img.BeginInit();img.CacheOption=BitmapCacheOption.OnLoad;
   img.UriSource=new Uri(Path.Combine(Root,"assets","spritesheet.png"));img.EndInit();img.Freeze();return img;
  }
  static void CheckConnection(){
   Config=LoadConfig();var local=new DataBridge(Config.CodexHome,Config.PreferredThreadId);var observed=local.Poll();
   string thread=Config.PreferredThreadId??Environment.GetEnvironmentVariable("CODEX_THREAD_ID")??observed.SourceThreadId;
   var messages=new ConversationBridge(local.CodexHome).Read(thread);
   using(var service=new CodexConnection(Config.CodexExecutable,local.CodexHome,Path.Combine(Root,"chat-workspace"))){
    service.RefreshAsync(true).GetAwaiter().GetResult();var state=service.Snapshot;
    File.WriteAllText(Path.Combine(Root,"connection-check.json"),new JavaScriptSerializer().Serialize(new {
     checkedUtc=DateTime.UtcNow.ToString("o"),connected=state.Connected,authenticated=state.Authenticated,
     quotaObservedUtc=state.QuotaObservedUtc,primaryRemaining=state.Primary==null?null:state.Primary.RemainingPercent,
     secondaryRemaining=state.Secondary==null?null:state.Secondary.RemainingPercent,models=state.Models.Select(m=>m.Id).ToArray(),
     currentConversationFound=messages.Messages!=null&&messages.Messages.Count>0,
     visibleMessages=messages.Messages==null?0:messages.Messages.Count,historyTruncated=messages.Truncated,
     historyNotice=messages.Notice,status=state.Status,inferencePerformed=false,rawConversationStored=false
    }));
    if(!state.Connected||!state.Authenticated||!state.QuotaObservedUtc.HasValue||state.Models.Count==0)Environment.ExitCode=1;
   }
  }
  static void CheckChat(){
   Config=LoadConfig();var local=new DataBridge(Config.CodexHome,Config.PreferredThreadId);var observed=local.Poll();
   using(var service=new CodexConnection(Config.CodexExecutable,local.CodexHome,Path.Combine(Root,"chat-workspace"))){
    service.RefreshAsync(true).GetAwaiter().GetResult();var initial=service.Snapshot;
    if(!initial.Authenticated)throw new InvalidOperationException("尚未登录 Codex。");
    string model=observed.Model??initial.Models.Where(m=>m.IsDefault).Select(m=>m.Id).FirstOrDefault();
    service.SendAsync("这是一条白璃桌宠的连接测试。不要调用工具，请只回复：白璃连接成功。",model,"low",Config.ChatThreadId).GetAwaiter().GetResult();
    var clock=Stopwatch.StartNew();while(service.Snapshot.IsSending&&clock.Elapsed.TotalSeconds<90)Thread.Sleep(100);
    var result=service.Snapshot;bool passed=!result.IsSending&&result.Status=="回复完成"&&result.Messages.Any(m=>m.Role=="assistant"&&m.Text.Contains("连接成功"));
    if(result.IsSending)service.InterruptAsync().GetAwaiter().GetResult();
    File.WriteAllText(Path.Combine(Root,"chat-check.json"),new JavaScriptSerializer().Serialize(new{checkedUtc=DateTime.UtcNow.ToString("o"),passed=passed,model=result.SelectedModel,effort=result.SelectedEffort,chatThreadId=result.ChatThreadId,status=result.Status,assistantMessages=result.Messages.Count(m=>m.Role=="assistant"),testReplyMatched=passed,privateConversationSent=false}));
    if(passed){Config.ChatThreadId=result.ChatThreadId;SaveConfig();}else Environment.ExitCode=1;
   }
  }
 }
 public sealed class CompanionWindow:Window {
  [StructLayout(LayoutKind.Sequential)] struct CursorPoint { public int X,Y; }
  [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);
  DataBridge bridge;
  AnimationController animator;
  readonly SpriteSurface sprite;
  readonly Canvas stage;
  PetViewport viewport;
  bool viewportChanging;
  double PetWidth {get{return viewport.ImageWidth;}}
  double PetHeight {get{return viewport.ImageHeight;}}
  double ImageLeft {get{return Left+viewport.Padding;}}
  double ImageTop {get{return Top+viewport.Padding;}}
  readonly DispatcherTimer dataTimer=new DispatcherTimer();
  readonly DispatcherTimer connectionUiTimer=new DispatcherTimer();
  int connectionDirty;
  bool directChatWorking;
  bool connectionRefreshing;
  TimeSpan lastPresented=TimeSpan.MinValue;
  readonly Stopwatch clock=Stopwatch.StartNew();
  readonly Forms.NotifyIcon tray=new Forms.NotifyIcon();
  CompanionSnapshot snapshot;
  InfoWindow info;
  double lastTick;
  bool polling, dragging, pressed, manualVisible=true, shuttingDown, manualPanelOverride;
  bool? lastKnownNativeVisible;
  readonly CarryMotion carry=new CarryMotion();
  readonly NativeControlsBridge nativeControls=new NativeControlsBridge();
  double? nativeAnchorX,nativeAnchorY;
  readonly GazeTracker gaze=new GazeTracker();
  double? lastNativeWidth;
  SettingsWindow settings;
  ConversationWindow conversationWindow;
  ConversationBridge conversations;
  CodexConnection connection;
  OutsideClickObserver outsideClicks;
  Point pressScreen, origin;
  string previewClip=null;
  DateTime previewUntil;
  public CompanionWindow() {
   Title="白璃 · 独立增强桌宠";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=null;
   ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=Program.Config.Topmost;ShowActivated=false;
   sprite=new SpriteSurface(Program.LoadAtlas()){ClipToBounds=false};stage=new Canvas{ClipToBounds=false};stage.Children.Add(sprite);Content=stage;
   LoadExtensions();
   bridge=new DataBridge(Program.Config.CodexHome,Program.Config.PreferredThreadId);
   SetupConnection();
   connectionUiTimer.Interval=TimeSpan.FromMilliseconds(150);connectionUiTimer.Tick+=delegate{if(Interlocked.Exchange(ref connectionDirty,0)!=0)ApplyConnectionUpdate();};connectionUiTimer.Start();
   ApplySize();
   Rect area=SystemParameters.WorkArea;
   Left=(Double.IsNaN(Program.Config.Left)?area.Right-PetWidth-48:Program.Config.Left)-viewport.Padding;
   Top=(Double.IsNaN(Program.Config.Top)?area.Bottom-PetHeight-48:Program.Config.Top)-viewport.Padding;
   ClampPosition();
   SavePetPosition();
   MouseLeftButtonDown+=OnDown;MouseMove+=OnMove;MouseLeftButtonUp+=OnUp;
   LocationChanged+=delegate{PositionInfo();};SizeChanged+=delegate{PositionInfo();};
   LostMouseCapture+=delegate{pressed=false;dragging=false;nativeControls.EndDrag();};
   MouseRightButtonUp+=delegate { MakeContextMenu().IsOpen=true; };
   MouseWheel+=delegate(object s,MouseWheelEventArgs e) { if((Keyboard.Modifiers&ModifierKeys.Control)!=0) { SetSize(Program.Config.PetHeight+e.Delta/12);e.Handled=true; } };
   KeyDown+=delegate(object s,KeyEventArgs e) { if(e.Key==Key.Escape && info!=null) info.Close(); };
   Closed+=delegate { if(!shuttingDown) ShutDown(); };
   string iconPath=Path.Combine(Program.Root,"assets","pet.ico");
   tray.Icon=File.Exists(iconPath)?new System.Drawing.Icon(iconPath):System.Drawing.SystemIcons.Information;tray.Text="白璃 · 左键查看额度与模型";
   Forms.ContextMenuStrip tm=new Forms.ContextMenuStrip();
   tm.Items.Add("显示桌宠",null,delegate { Dispatcher.Invoke(new Action(delegate { manualVisible=true;Program.Config.FollowNativeVisibility=false;Program.SaveConfig();Show(); })); });
   tm.Items.Add("隐藏桌宠",null,delegate { Dispatcher.Invoke(new Action(delegate { manualVisible=false;Hide();if(info!=null) info.Hide(); })); });
   tm.Items.Add("跟随 GPT 桌宠显隐",null,delegate { Dispatcher.Invoke(new Action(delegate { manualVisible=true;Program.Config.FollowNativeVisibility=true;Program.SaveConfig();UpdateVisibility(); })); });
   tm.Items.Add("额度与模型",null,delegate { Dispatcher.Invoke(new Action(delegate{ShowInfoCore(true);})); });
   tm.Items.Add("退出",null,delegate { Dispatcher.Invoke(new Action(ShutDown)); });
   tray.ContextMenuStrip=tm;tray.DoubleClick+=delegate { Dispatcher.Invoke(new Action(delegate{ShowInfoCore(true);})); };tray.Visible=Program.Config.ShowTrayIcon;
   CompositionTarget.Rendering+=OnFrame;
   dataTimer.Interval=TimeSpan.FromSeconds(2);dataTimer.Tick+=delegate { PollData(); };dataTimer.Start();
   PollData();RefreshConnection(false);
  }
  void ApplySize() {
   double h=Program.Config.PetHeight;
   if(Double.IsNaN(h)||Double.IsInfinity(h)) h=224;
   h=Math.Max(80.0*208/192,Math.Min(416,h));Program.Config.PetHeight=h;
   SetViewport(new PetViewport(h));
  }
  void SetViewport(PetViewport next){
   viewport=next;Width=next.WindowWidth;Height=next.WindowHeight;
   stage.Width=Width;stage.Height=Height;sprite.Width=next.ImageWidth;sprite.Height=next.ImageHeight;
   Canvas.SetLeft(sprite,next.Padding);Canvas.SetTop(sprite,next.Padding);
  }
  void SavePetPosition(){Program.Config.Left=ImageLeft;Program.Config.Top=ImageTop;}
  void ResizeKeepingFeet(double height){
   var next=new PetViewport(height);Point position=viewport.ResizeKeepingFeet(Left,Top,next);
   Program.Config.PetHeight=next.ImageHeight;viewportChanging=true;
   try{SetViewport(next);Left=position.X;Top=position.Y;}finally{viewportChanging=false;}
   ClampPosition();SavePetPosition();PositionInfo();
  }
  void FitCarryViewport(){
   Rect bounds=sprite.PresentedBounds();double required=Math.Max(PetHeight*.6,Math.Max(Math.Max(-bounds.Left,bounds.Right-PetWidth),Math.Max(-bounds.Top,bounds.Bottom-PetHeight))+2);
   if(pressed||carry.Active)required=Math.Max(required,viewport.Padding);
   if(Math.Abs(required-viewport.Padding)<.5)return;
   double imageX=ImageLeft,imageY=ImageTop,delta=viewport.Padding-required;
   viewportChanging=true;
   try{SetViewport(new PetViewport(PetHeight,required));Left=imageX-viewport.Padding;Top=imageY-viewport.Padding;
    if(pressed)origin=new Point(origin.X+delta,origin.Y+delta);
   }finally{viewportChanging=false;}
   PositionInfo();
  }
  void LoadExtensions() {
   animator=new AnimationController();
   sprite.LoadExtraClips(Program.Config.SpriteClips);
   foreach(MotionGroup group in Program.Config.InteractionGroups??new List<MotionGroup>()) {
    try {if(group.Steps==null||group.Steps.Any(s=>!sprite.HasClip(s.Clip)))throw new InvalidDataException("动作引用了不存在的图集");animator.AddInteraction(group.Name,group.Steps.Select(s=>new AnimationStep(s.Clip,s.Seconds)).ToArray());}
    catch(Exception e){File.AppendAllText(Path.Combine(Program.Root,"diagnostics.log"),"扩展动作："+e.Message+Environment.NewLine);}
   }
  }
  void ClampPosition() {
   Point p=viewport.ClampWindowOrigin(Left,Top,new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight));
   Left=p.X;Top=p.Y;
  }
  Point CursorScreen() { CursorPoint p;return GetCursorPos(out p)?new Point(p.X,p.Y):new Point(0,0); }
  void OnDown(object s,MouseButtonEventArgs e) {
   pressed=true;dragging=false;pressScreen=CursorScreen();origin=new Point(Left,Top);
   Point displayed=e.GetPosition(sprite),grab=sprite.SourcePoint(displayed);
   double drawnWidth=sprite.ActualWidth>0?sprite.ActualWidth:PetWidth;
   carry.BeginGrab(grab.X/drawnWidth,grab.Y/PetHeight,displayed.X/drawnWidth,displayed.Y/PetHeight,pressScreen.X,pressScreen.Y,PhysicalPetHeight(),sprite.DisplayAngle);
   sprite.SetContinuousLook(false,0,0,.016,0);sprite.SetCarry(carry.Angle,carry.GrabX,carry.GrabY,carry.OffsetX,carry.OffsetY);
   if(Program.Config.AttachNativeControls&&snapshot!=null&&snapshot.OverlayVisible==true)nativeControls.BeginDrag();CaptureMouse();e.Handled=true;
  }
  double PhysicalPetHeight(){var source=PresentationSource.FromVisual(this);return PetHeight*(source!=null&&source.CompositionTarget!=null?source.CompositionTarget.TransformToDevice.M22:1);}
  void OnMove(object s,MouseEventArgs e) {
   if(!pressed) return;
   // Captured MouseUp owns the click/drag completion. A queued MouseMove may
   // observe the released button before MouseUp and must not discard a click.
   var presentation=PresentationSource.FromVisual(this);if(presentation==null||presentation.CompositionTarget==null)return;
   Point p=CursorScreen();var dpi=presentation.CompositionTarget.TransformFromDevice;
   Vector d=dpi.Transform(p-pressScreen);
   if(d.Length>5) dragging=true;
   if(dragging) {Left=origin.X+d.X;Top=origin.Y+d.Y;ClampPosition();if(Program.Config.AttachNativeControls)nativeControls.DragBy(presentation.CompositionTarget.TransformToDevice.Transform(new Vector(Left-origin.X,Top-origin.Y)));PositionInfo();}
  }
  void OnUp(object s,MouseButtonEventArgs e) {
   if(!pressed)return;bool moved=dragging;pressed=false;ReleaseMouseCapture();
   if(moved){ClampPosition();SavePetPosition();Program.SaveConfig();}
   else ShowInfo();dragging=false;e.Handled=true;
  }
  void PollData() {
   if(polling)return;polling=true;
   Task.Run(delegate { return bridge.Poll(); }).ContinueWith(delegate(Task<CompanionSnapshot> t) {
    Dispatcher.BeginInvoke(new Action(delegate {
     polling=false;if(shuttingDown)return;
     if(t.Status==TaskStatus.RanToCompletion) snapshot=t.Result;
     SyncNativeSize();
     SyncNativeAnchor();
     UpdateVisibility();if(info!=null)info.Update(DisplaySnapshot());
     if(conversationWindow!=null)conversationWindow.SetSource(snapshot==null?null:snapshot.SourceThreadId,snapshot==null?null:snapshot.Model);
     RefreshConnection(false);
    }));
   });
  }
  void UpdateVisibility() {
   if(snapshot!=null && snapshot.OverlayVisible.HasValue) lastKnownNativeVisible=snapshot.OverlayVisible;
   bool visible=manualVisible;
   if(Program.Config.FollowNativeVisibility && lastKnownNativeVisible.HasValue) visible &=lastKnownNativeVisible.Value;
   if(visible){if(!IsVisible) Show();}else {Hide();if(info!=null && !manualPanelOverride) info.Hide();}
  }
  void OnFrame(object sender,EventArgs e) {
   var presentation=e as RenderingEventArgs;
   if(presentation!=null){if(presentation.RenderingTime==lastPresented)return;lastPresented=presentation.RenderingTime;}
   double now=clock.Elapsed.TotalSeconds,dt=Math.Min(.1,Math.Max(.001,now-lastTick));lastTick=now;
   if(!IsVisible)return;
   Point local;
   Point screen=CursorScreen();
   try{local=sprite.PointFromScreen(screen);}catch{return;}
   double dx=local.X-PetWidth/2,dy=local.Y-PetHeight*.42;
   // Use the actual moving anchor. At a screen edge the window stops even
   // if the mouse continues, so the suspension should brake there too.
   Point suspension=screen;
   if(dragging)suspension=sprite.PointToScreen(new Point((carry.GrabX+carry.OffsetX)*sprite.ActualWidth,(carry.GrabY+carry.OffsetY)*PetHeight));
   if(!(pressed&&!dragging))carry.Update(dragging,suspension.X,suspension.Y,PhysicalPetHeight(),dt);
   bool carrying=pressed||dragging||carry.Active;
   double distance=Math.Sqrt(dx*dx+dy*dy);
   CompanionWorkState work=directChatWorking?CompanionWorkState.Working:snapshot==null?CompanionWorkState.Unknown:snapshot.State;
   double rate=animator.Current.Family=="interaction"?.85:work==CompanionWorkState.Working?.7:.55;
   AnimationPose pose=animator.Tick(dt*rate,distance*208/Program.Config.PetHeight,dragging||pressed,info!=null && info.IsVisible,work);
   string clip=pose.Clip;double frameBlend;int frame=NativeFrame(clip,pose.ClipSeconds,out frameBlend);
   bool continuous=false;
   if(carrying){clip="idle";frame=0;frameBlend=0;}
   else if(previewClip!=null && DateTime.UtcNow<previewUntil){clip=previewClip;frame=NativeFrame(clip,now*.65,out frameBlend);}
   else if(pose.Family=="base"&&gaze.Update(screen,now,dx,dy,Program.Config.PetHeight,pose.Family,dragging||pressed)) {
    continuous=true;clip="idle";frame=0;frameBlend=0;
   }
   // Keep one continuous thoughtful pose while working. Group changes and
   // mouse movement must not restart the clip or replace its head with idle.
   // Legacy direction IDs remain available to extension/interaction groups,
   // but are fixed standing poses. No old leg animation can be re-entered.
   if(clip=="running-right"||clip=="running-left"){frame=0;frameBlend=0;}
   if(clip=="running"&&sprite.HasClip("thinking-calm")){
    clip="thinking-calm";frame=(int)(now/6.4*128)%128;frameBlend=0;
   }
   sprite.SetPose(clip,frame,pose,Program.Config.PetHeight,dragging?"被你带走啦":pose.DisplayName,Program.Config.SmoothAnimations?frameBlend:0);
   sprite.SetCarry(carry.Angle,carry.GrabX,carry.GrabY,carry.OffsetX,carry.OffsetY);
   sprite.SetContinuousLook(continuous,dx,dy,dt,now);
   FitCarryViewport();
  }
  static int NativeFrame(string clip,double seconds) {double ignored;return NativeFrame(clip,seconds,out ignored);}
  static int NativeFrame(string clip,double seconds,out double blend) {
   blend=0;
   int[] durations;
   switch(clip){case "idle":durations=new[]{280,110,110,140,140,320};break;
    case "running-right":case "running-left":durations=new[]{120,120,120,120,120,120,120,120};break;
    case "waving":durations=new[]{140,140,140,280};break;
    case "jumping":durations=new[]{140,140,140,140,280};break;
    case "failed":durations=new[]{140,140,140,140,140,140,140,240};break;
    case "waiting":durations=new[]{150,150,150,150,150,260};break;
    case "running":durations=new[]{120,120,120,120,120,220};break;
    case "review":durations=new[]{150,150,150,150,150,280};break;
    default:return (int)(seconds/.12);
   }
   double t=(seconds*1000)%durations.Sum();for(int i=0;i<durations.Length;i++){if(t<durations[i]){blend=t/durations[i];return i;}t-=durations[i];}return durations.Length-1;
  }
  public void ShowInfo() {ShowInfoCore(false);}
  void ShowInfoCore(bool independent) {
   if(!independent && info!=null && info.IsVisible){info.Close();return;}
   manualVisible=true;
   manualPanelOverride=independent;
   if(info==null){info=new InfoWindow(this);info.Closed+=delegate{info=null;manualPanelOverride=false;if(outsideClicks!=null){outsideClicks.Dispose();outsideClicks=null;}};}
   info.Update(DisplaySnapshot());
   PositionInfo();
   info.Show();info.Activate();PollData();RefreshConnection(true);
   if(outsideClicks==null)outsideClicks=new OutsideClickObserver(Dispatcher,DismissInfoAt);
  }
  void PositionInfo(){
   if(info==null||viewportChanging||info.IsVisible)return;
   Point position=InfoPosition(viewport.ImageRect(Left,Top),new Size(info.Width,info.Height),InfoWorkArea());
   info.Left=position.X;info.Top=position.Y;
  }
  void DismissInfoAt(Point screen){
   if(info==null||!info.IsVisible)return;
   Point p=info.PointFromScreen(screen);
   if(new Rect(0,0,info.Width,info.Height).Contains(p))return;
   // Keep the popup while the character is clicked/dragged; OnUp toggles it.
   if(IsVisible && sprite.InputHitTest(sprite.PointFromScreen(screen))!=null)return;
   info.Close();
  }
  void SyncNativeSize(){
   if(pressed||dragging||carry.Active)return;
   if(snapshot==null||!snapshot.OverlayReadSucceeded)return;
   if(snapshot.PetSize.HasValue&&snapshot.PetSizeKey!="avatar-overlay-mascot-width-px")return;
   double width=snapshot.PetSize??112; // Native setting's documented-in-code default.
   if(width<80||width>224)return;
   bool changed=!lastNativeWidth.HasValue||lastNativeWidth.Value!=width;lastNativeWidth=width;
   if(Program.Config.FollowNativeSize&&changed){ResizeKeepingFeet(width*208/192);Program.SaveConfig();if(settings!=null)settings.Sync();}
  }
  void SyncNativeAnchor(){
   if(!Program.Config.AttachNativeControls||pressed||dragging||carry.Active||snapshot==null||snapshot.OverlayVisible!=true||snapshot.OverlayBounds==null||!nativeControls.Attach())return;
   var bounds=snapshot.OverlayBounds;if(!bounds.X.HasValue||!bounds.Y.HasValue)return;
   if(nativeAnchorX==bounds.X&&nativeAnchorY==bounds.Y)return;
   nativeAnchorX=bounds.X;nativeAnchorY=bounds.Y;
   double nativeWidth=snapshot.PetSize??112;
   Point p=viewport.WindowOrigin(bounds.X.Value+(nativeWidth-PetWidth)/2,bounds.Y.Value+nativeWidth*208/192-PetHeight);
   Left=p.X;Top=p.Y;ClampPosition();PositionInfo();SavePetPosition();Program.SaveConfig();
  }
  void ShowSettings(){
   if(info!=null)info.Close();
   if(settings==null){settings=new SettingsWindow(this);settings.Closed+=delegate{settings=null;};}
   settings.Show();settings.Activate();
  }
  Rect InfoWorkArea() {
   try {
    var presentation=PresentationSource.FromVisual(this);
    if(presentation!=null && presentation.CompositionTarget!=null) {
     Point center=PointToScreen(new Point(Width/2,Height/2));
     var screen=Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X,(int)center.Y));
     var bounds=screen.WorkingArea;Matrix transform=presentation.CompositionTarget.TransformFromDevice;
     return new Rect(transform.Transform(new Point(bounds.Left,bounds.Top)),transform.Transform(new Point(bounds.Right,bounds.Bottom)));
    }
   }catch{}
   return SystemParameters.WorkArea;
  }
  static Point InfoPosition(Rect pet,Size panel,Rect area) {
   double x=pet.Right+10;
   if(x+panel.Width>area.Right) x=pet.Left-panel.Width-10;
   return new Point(Math.Max(area.Left,Math.Min(x,area.Right-panel.Width)),Math.Max(area.Top,Math.Min(pet.Bottom-panel.Height,area.Bottom-panel.Height)));
  }
  MenuItem Item(string name,Action action) {MenuItem m=new MenuItem{Header=name};m.Click+=delegate{action();};return m;}
  ContextMenu MakeContextMenu() {
   ContextMenu m=new ContextMenu();
   m.Items.Add(Item("对话与模型…",ShowConversation));
   m.Items.Add(Item("设置",ShowSettings));
   m.Items.Add(Item("额度与模型",ShowInfo));m.Items.Add(Item("刷新额度",RefreshNow));m.Items.Add(Item("打开 Codex",delegate{Program.Open("codex://launch");}));
   m.Items.Add(Item("在 Codex 新建聊天",delegate{Program.Open("codex://threads/new");}));
   m.Items.Add(Item("回到原生桌宠的位置",delegate{
    if(snapshot==null||snapshot.OverlayBounds==null||!snapshot.OverlayBounds.X.HasValue||!snapshot.OverlayBounds.Y.HasValue)return;
    double nativeWidth=snapshot.PetSize??112;Point p=viewport.WindowOrigin(snapshot.OverlayBounds.X.Value+(nativeWidth-PetWidth)/2,snapshot.OverlayBounds.Y.Value+nativeWidth*208/192-PetHeight);Left=p.X;Top=p.Y;ClampPosition();SavePetPosition();Program.SaveConfig();
   }));
   MenuItem size=new MenuItem{Header="调整大小"};
   size.Items.Add(Item("大小滑条…",ShowSettings));
   foreach(int value in new int[]{128,160,192,224,280,336}) {int copy=value;size.Items.Add(Item(value+" px",delegate {SetSize(copy);}));}m.Items.Add(size);
   MenuItem demo=new MenuItem{Header="预览动作"};
   string[] demoIds={"idle","waving","jumping","running-right","running-left","waiting","running","review","failed"};
   string[] demoNames={"呼吸与眨眼","挥手招呼","轻快蹦跳","向右移动","向左移动","等你回应","专心工作","认真检查","失落表情"};
   for(int i=0;i<demoIds.Length;i++) {string copy=demoIds[i];demo.Items.Add(Item(demoNames[i],delegate{previewClip=copy;previewUntil=DateTime.UtcNow.AddSeconds(5);}));}m.Items.Add(demo);
   MenuItem sync=new MenuItem{Header="跟随 GPT 桌宠显隐",IsCheckable=true,IsChecked=Program.Config.FollowNativeVisibility};
   sync.Click+=delegate{Program.Config.FollowNativeVisibility=sync.IsChecked;Program.SaveConfig();UpdateVisibility();};m.Items.Add(sync);
   MenuItem attached=new MenuItem{Header="与原生白条一起移动",IsCheckable=true,IsChecked=Program.Config.AttachNativeControls};
   attached.Click+=delegate{Program.Config.AttachNativeControls=attached.IsChecked;nativeAnchorX=nativeAnchorY=null;SyncNativeAnchor();Program.SaveConfig();};m.Items.Add(attached);
   m.Items.Add(Item("重新加载扩展设置",delegate {Program.Config=Program.LoadConfig();bridge=new DataBridge(Program.Config.CodexHome,Program.Config.PreferredThreadId);SetupConnection();snapshot=null;lastKnownNativeVisible=null;LoadExtensions();ResizeKeepingFeet(Program.Config.PetHeight);Topmost=Program.Config.Topmost;PollData();}));
   foreach(ExtraAction value in Program.Config.Actions ?? new List<ExtraAction>()) {
    ExtraAction action=value;if(action==null||String.IsNullOrWhiteSpace(action.Label)||String.IsNullOrWhiteSpace(action.Target))continue;
    m.Items.Add(Item(action.Label,delegate{Program.Open(action.Target);}));
   }
   m.Items.Add(Item("打开设置文件",delegate{if(!File.Exists(Program.ConfigPath))Program.SaveConfig();Process.Start("notepad.exe","\""+Program.ConfigPath+"\"");}));
   m.Items.Add(Item("退出增强版",ShutDown));return m;
  }
  public void SetSize(double value){Program.Config.FollowNativeSize=false;ResizeKeepingFeet(value);Program.SaveConfig();}
  public void FollowSize(bool value){Program.Config.FollowNativeSize=value;if(value){lastNativeWidth=null;SyncNativeSize();}Program.SaveConfig();}
  public void RefreshNow(){PollData();RefreshConnection(true);}
  void SetupConnection(){
   if(conversationWindow!=null)conversationWindow.Close();
   if(connection!=null){connection.Changed-=ConnectionChanged;connection.Dispose();}
   conversations=new ConversationBridge(bridge.CodexHome);
   connection=new CodexConnection(Program.Config.CodexExecutable,bridge.CodexHome,Path.Combine(Program.Root,"chat-workspace"));connection.Changed+=ConnectionChanged;
  }
  void ShowConversation(){
   if(info!=null)info.Close();
   if(conversationWindow==null){conversationWindow=new ConversationWindow(connection,conversations);conversationWindow.Closed+=delegate{conversationWindow=null;};}
   conversationWindow.SetSource(snapshot==null?Program.Config.PreferredThreadId:snapshot.SourceThreadId,snapshot==null?null:snapshot.Model);
   conversationWindow.Show();conversationWindow.Activate();RefreshConnection(true);
  }
  async void RefreshConnection(bool force){if(connectionRefreshing||shuttingDown)return;connectionRefreshing=true;try{await connection.RefreshAsync(force);}catch{}finally{connectionRefreshing=false;}ConnectionChanged();}
  void ConnectionChanged(){
   if(!shuttingDown)Interlocked.Exchange(ref connectionDirty,1);
  }
  void ApplyConnectionUpdate(){
    if(shuttingDown)return;var state=connection.Snapshot;directChatWorking=state.IsSending;
    if(!String.IsNullOrEmpty(state.ChatThreadId)&&Program.Config.ChatThreadId!=state.ChatThreadId){Program.Config.ChatThreadId=state.ChatThreadId;Program.SaveConfig();}
    if(info!=null)info.Update(DisplaySnapshot());
    if(conversationWindow!=null)conversationWindow.UpdateConnection(state);
  }
  CompanionSnapshot DisplaySnapshot(){
   CompanionSnapshot result=snapshot??new CompanionSnapshot();var live=connection.Snapshot;
   if(live.QuotaObservedUtc.HasValue){
    result.Primary=live.Primary;result.Secondary=live.Secondary;result.LimitsObservedUtc=live.QuotaObservedUtc;
    result.LimitsSource=live.Connected&&!live.QuotaRefreshFailed?"app-server":"cached-app-server";
   }else result.LimitsSource="local-log";
   result.ConnectionNotice=live.Status;
   return result;
  }
  void ShutDown(){if(shuttingDown)return;shuttingDown=true;CompositionTarget.Rendering-=OnFrame;dataTimer.Stop();connectionUiTimer.Stop();if(outsideClicks!=null)outsideClicks.Dispose();if(conversationWindow!=null)conversationWindow.Close();connection.Changed-=ConnectionChanged;connection.Dispose();tray.Visible=false;tray.Dispose();SavePetPosition();Program.SaveConfig();Application.Current.Shutdown();}
 }
 // Facing depends on cursor travel, never on window movement/reentrant events.
 public sealed class DragFacing {
  double extreme;bool established;
  public int Facing{get;private set;}
  public void Begin(double x){extreme=x;established=false;if(Facing==0)Facing=1;}
  public int Move(double x){
   if(!established){if(Math.Abs(x-extreme)>=3){Facing=x<extreme?-1:1;established=true;extreme=x;}return Facing;}
   if(Facing>0){if(x>extreme)extreme=x;else if(extreme-x>=7){Facing=-1;extreme=x;}}
   else {if(x<extreme)extreme=x;else if(x-extreme>=7){Facing=1;extreme=x;}}
   return Facing;
  }
 }
 public sealed class GazeTracker {
  Point last;bool seen;double movedAt=-1000;
  public int Direction{get;private set;}
  public bool Update(Point screen,double now,double dx,double dy,double height,string family,bool dragging){
   if(!seen||(screen-last).LengthSquared>=1){last=screen;seen=true;movedAt=now;}
   if(dragging){Direction=-1;return false;}
   double hold=family=="interaction"?.8:2.2;
   if((family!="base"&&now-movedAt>hold)||dx*dx+dy*dy<Math.Pow(12*height/208,2)){Direction=-1;return false;}
   double degree=Math.Atan2(dx,-dy)*180/Math.PI;if(degree<0)degree+=360;
   int candidate=((int)Math.Floor((degree+11.25)/22.5))%16;
   double delta=Direction<0?180:Math.Abs((degree-Direction*22.5+540)%360-180);
   if(Direction<0||delta>15)Direction=candidate;
   return true;
  }
 }
 // One stride follows actual horizontal travel rather than wall-clock time.
 // A held cursor stops on a planted-foot cel instead of pedalling in the air.
 public sealed class WalkClock {
  double previous,travel,visual,lastMove;
  public void Begin(double x,double now){previous=x;travel=visual=0;lastMove=now;}
  public void Move(double x,double now,double physicalHeight){
   double distance=Math.Abs(x-previous);
   if(distance<.01)return;
   if(distance<.6)return;
   if(now-lastMove>.16)travel=visual;
   lastMove=now;previous=x;
   // Bound the backlog so a quick drag never causes frantic pedalling.
   travel=Math.Min(visual+.35,travel+distance/Math.Max(24,physicalHeight*.42));
  }
  public int Frame(double now){
   int frame=(int)Math.Floor(travel*8)%8;
   if(now-lastMove>.16)return frame<4?0:4;
   return frame;
  }
  public double Phase(double now,double dt){
   bool stopped=now-lastMove>.16;
   double target=stopped?Math.Ceiling((visual-.000001)*2)/2:travel;
   double delta=Math.Max(0,target-visual)*(1-Math.Exp(-Math.Max(0,dt)/.045));
   visual+=Math.Min(Math.Max(0,dt)*1.7,delta);
   if(stopped&&target-visual<.0001){visual=target;travel=visual;}
   return visual%1;
  }
 }
 public sealed class ContinuousLook {
  public double X{get;private set;}public double Y{get;private set;}
  public void Update(double dx,double dy,double height,double dt){
   double soft=80*height/208;double length=Math.Sqrt(dx*dx+dy*dy+soft*soft);
   double blend=1-Math.Exp(-Math.Max(0,dt)/.022);
   X+=(dx/Math.Max(1,length)-X)*blend;Y+=(dy/Math.Max(1,length)-Y)*blend;
  }
 }
 // This observes mouse-down only while the panel exists. Input continues
 // unchanged to its original destination; nothing is captured or recorded.
 sealed class OutsideClickObserver:IDisposable {
  [StructLayout(LayoutKind.Sequential)]struct HookData {public int X,Y;public uint MouseData,Flags,Time;public UIntPtr ExtraInfo;}
  delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
  [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int id,HookProc proc,IntPtr module,uint thread);
  [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
  [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string name);
  readonly HookProc callback;IntPtr hook;
  public OutsideClickObserver(Dispatcher dispatcher,Action<Point> onDown){
   callback=delegate(int code,IntPtr message,IntPtr data){
    long kind=message.ToInt64();
    if(code>=0&&(kind==0x201||kind==0x204||kind==0x207||kind==0x20B)){
     HookData point=(HookData)Marshal.PtrToStructure(data,typeof(HookData));
     dispatcher.BeginInvoke(new Action(delegate{onDown(new Point(point.X,point.Y));}));
    }
    return CallNextHookEx(hook,code,message,data);
   };
   hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);
   if(hook==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法监听额度框外的鼠标点击");
  }
  public void Dispose(){if(hook!=IntPtr.Zero){UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}GC.KeepAlive(callback);}
 }
 public sealed class SettingsWindow:Window {
  readonly CompanionWindow pet;readonly Slider size;readonly CheckBox follow;readonly TextBlock value;
  bool syncing;
  public SettingsWindow(CompanionWindow owner){
   pet=owner;Title="白璃 · 设置";Width=310;Height=190;ResizeMode=ResizeMode.NoResize;Topmost=true;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Microsoft YaHei UI");Background=Brushes.White;
   StackPanel panel=new StackPanel{Margin=new Thickness(18)};Content=panel;
   panel.Children.Add(new TextBlock{Text="桌宠大小",FontSize=14,FontWeight=FontWeights.SemiBold});
   Grid row=new Grid{Margin=new Thickness(0,12,0,12)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(46)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(45)});
   size=new Slider{Minimum=80,Maximum=224,TickFrequency=1,IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center,SmallChange=1,LargeChange=10};
   System.Windows.Automation.AutomationProperties.SetName(size,"桌宠大小");row.Children.Add(size);
   value=new TextBlock{VerticalAlignment=VerticalAlignment.Center,TextAlignment=TextAlignment.Center,FontSize=11};Grid.SetColumn(value,1);row.Children.Add(value);
   Button reset=new Button{Content="重置",Padding=new Thickness(4,2,4,2)};Grid.SetColumn(reset,2);row.Children.Add(reset);panel.Children.Add(row);
   follow=new CheckBox{Content="跟随 Codex“我的桌宠”大小",Margin=new Thickness(0,0,0,8)};panel.Children.Add(follow);
   panel.Children.Add(new TextBlock{Text="滑动即生效；默认宽度 112。",FontSize=10,Foreground=Brushes.Gray});
   size.ValueChanged+=delegate{if(syncing)return;pet.SetSize(size.Value*208/192);syncing=true;follow.IsChecked=false;syncing=false;value.Text=((int)size.Value)+" px";};
   reset.Click+=delegate{pet.SetSize(112.0*208/192);Sync();};
   follow.Click+=delegate{if(!syncing){pet.FollowSize(follow.IsChecked==true);Sync();}};
   KeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Escape)Close();};Sync();
  }
  public void Sync(){syncing=true;size.Value=Math.Max(80,Math.Min(224,Program.Config.PetHeight*192/208));value.Text=((int)size.Value)+" px";follow.IsChecked=Program.Config.FollowNativeSize;syncing=false;}
 }
 public sealed class SpriteSurface:FrameworkElement {
  readonly Dictionary<string,BitmapSource[]> clips=new Dictionary<string,BitmapSource[]>();
  readonly Dictionary<string,BitmapSource[]> smoothClips=new Dictionary<string,BitmapSource[]>();
  readonly HashSet<string> extraIds=new HashSet<string>();
  Matrix poseMatrix=Matrix.Identity,carryMatrix=Matrix.Identity;
  BitmapSource current;AnimationPose pose;double petHeight;
  readonly ContinuousLook look=new ContinuousLook();bool continuousLook;double blink;
  internal BitmapSource CurrentFrame { get { return current; } }
  static readonly Geometry leftEye=EyeOpening(71.5,90.5,7.2,8.6),rightEye=EyeOpening(105.7,90.5,7.7,8.7);
  static readonly Geometry leftLash=EyeLash(71.5,90.5,7.2,8.6),rightLash=EyeLash(105.7,90.5,7.7,8.7);
  static readonly Brush sclera=FrozenBrush(Color.FromRgb(250,250,245)),skin=FrozenBrush(Color.FromRgb(255,239,224)),pupil=FrozenBrush(Color.FromArgb(170,39,51,60)),glint=FrozenBrush(Color.FromArgb(230,248,255,253)),smallGlint=FrozenBrush(Color.FromArgb(210,238,251,247)),iris=IrisBrush();
  static readonly Pen lashPen=MakeLashPen();
  public SpriteSurface(BitmapSource atlas) {
   RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);UseLayoutRounding=true;SnapsToDevicePixels=true;
   if(atlas.Format!=PixelFormats.Bgra32){var converted=new FormatConvertedBitmap(atlas,PixelFormats.Bgra32,null,0);converted.Freeze();atlas=converted;}
   string[] names={"idle","running-right","running-left","waving","jumping","failed","waiting","running","review","look-a","look-b"};
   int[] counts={6,8,8,4,5,8,6,6,6,8,8};
   for(int row=0;row<11;row++){BitmapSource[] frames=new BitmapSource[counts[row]];for(int col=0;col<frames.Length;col++){var b=new CroppedBitmap(atlas,new Int32Rect(col*192,row*208,192,208));b.Freeze();frames[col]=b;}clips[names[row]]=frames;}
   current=clips["idle"][0];
   LoadBuiltinClip("thinking-calm","thinking-calm.png",128);
   // Preserve authored cels. Never blend two distinct silhouettes into a
   // double outline; rigid drag physics and vector eyes remain continuous.
   var sources=clips.Where(p=>!p.Key.StartsWith("look-")&&!p.Key.StartsWith("cute-")&&p.Key!="thinking-calm"&&p.Key!="running"&&p.Key!="running-right"&&p.Key!="running-left").ToArray();
   foreach(var source in sources)smoothClips[source.Key]=MotionInterpolation.Build(source.Value);
  }
  void LoadBuiltinClip(string id,string filename,int count){
   string path=Path.Combine(Program.Root,"assets",filename);if(!File.Exists(path))return;
   try{
    var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();
    int columns=image.PixelWidth/192;if(columns<1||image.PixelHeight<((count+columns-1)/columns)*208)throw new InvalidDataException("Unexpected animation grid");
    var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);converted.Freeze();
    var frames=new BitmapSource[count];for(int i=0;i<count;i++){var b=new CroppedBitmap(converted,new Int32Rect((i%columns)*192,(i/columns)*208,192,208));b.Freeze();frames[i]=b;}clips[id]=frames;
   }catch(Exception e){File.AppendAllText(Path.Combine(Program.Root,"diagnostics.log"),"内置动作资源："+e.Message+Environment.NewLine);}
  }
  public void LoadExtraClips(List<SpriteClip> extra) {
   foreach(string id in extraIds)clips.Remove(id);extraIds.Clear();
   foreach(SpriteClip clip in extra??new List<SpriteClip>()) {
    try {
     if(String.IsNullOrWhiteSpace(clip.Id)||clips.ContainsKey(clip.Id)||String.IsNullOrWhiteSpace(clip.File)||clip.Frames<1||clip.Frames>100||clip.CellWidth<1||clip.CellHeight<1)continue;
     var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(Path.GetFullPath(Path.Combine(Program.Root,clip.File)));image.EndInit();image.Freeze();
     if(image.PixelWidth<clip.CellWidth*clip.Frames||image.PixelHeight<clip.CellHeight)continue;
     var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);converted.Freeze();
     var frames=new BitmapSource[clip.Frames];for(int i=0;i<clip.Frames;i++){var b=new CroppedBitmap(converted,new Int32Rect(i*clip.CellWidth,0,clip.CellWidth,clip.CellHeight));b.Freeze();frames[i]=b;}clips[clip.Id]=frames;extraIds.Add(clip.Id);
    }catch(Exception e){File.AppendAllText(Path.Combine(Program.Root,"diagnostics.log"),"扩展图集："+e.Message+Environment.NewLine);}
   }
  }
  public bool HasClip(string id){return id!=null && clips.ContainsKey(id);}
  public void SetPose(string clip,int frame,AnimationPose animation,double height,string text){SetPose(clip,frame,animation,height,text,0);}
  public void SetPose(string clip,int frame,AnimationPose animation,double height,string text,double blend){
   BitmapSource[] frames;if(!clips.TryGetValue(clip??"idle",out frames))frames=clips["idle"];
   BitmapSource next=frames[((frame%frames.Length)+frames.Length)%frames.Length];
   BitmapSource[] smooth;
   if(blend>0&&smoothClips.TryGetValue(clip??"idle",out smooth))next=smooth[(((frame%frames.Length)+frames.Length)%frames.Length)*MotionInterpolation.Samples+Math.Min(MotionInterpolation.Samples-1,(int)(blend*MotionInterpolation.Samples))];
   bool changed=!Object.ReferenceEquals(current,next)||petHeight!=height||!poseMatrix.IsIdentity;
   current=next;pose=animation;petHeight=height;
   // The drawn cels already contain the motion. Fractional raster transforms
   // make antialiased hair edges shimmer, including during work animations.
   poseMatrix=Matrix.Identity;
   if(changed)InvalidateVisual();
  }
  public void SetContinuousLook(bool enabled,double dx,double dy,double dt,double now){
   bool changed=enabled!=continuousLook;continuousLook=enabled;
   if(enabled){look.Update(dx,dy,petHeight,dt);double phase=now%4.8;blink=phase>4.64?Math.Sin((phase-4.64)/.16*Math.PI):0;changed=true;}
   if(changed)InvalidateVisual();
  }
  public void SetCarry(double lift,double angle){
   SetCarry(angle,.5,.25,0,-lift/208);
  }
  public void SetCarry(double angle,double grabX,double grabY,double offsetX,double offsetY){
   double h=petHeight>0?petHeight:ActualHeight,w=ActualWidth;
   Matrix next=Matrix.Identity;next.RotateAt(angle,w*grabX,h*grabY);next.Translate(offsetX*w,offsetY*h);
   if(next!=carryMatrix){carryMatrix=next;InvalidateVisual();}
  }
  Matrix DisplayMatrix(){Matrix result=Matrix.Identity;if(continuousLook)result.RotateAt(look.X*.65,ActualWidth/2,petHeight*.7);result.Append(poseMatrix);result.Append(carryMatrix);return result;}
  internal double DisplayAngle{get{Matrix m=DisplayMatrix();return Math.Atan2(m.M12,m.M11)*180/Math.PI;}}
  internal Point SourcePoint(Point p){Matrix m=DisplayMatrix();if(!m.HasInverse)return p;m.Invert();return m.Transform(p);}
  internal Rect PresentedBounds(){return Rect.Transform(new Rect(0,0,ActualWidth,petHeight>0?petHeight:ActualHeight),DisplayMatrix());}
  internal bool HitsSprite(Point p){return HitTestCore(new PointHitTestParameters(p))!=null;}
  protected override HitTestResult HitTestCore(PointHitTestParameters test) {
   Point p=test.HitPoint;double h=petHeight>0?petHeight:ActualHeight;
   Matrix inverse=DisplayMatrix();if(!inverse.HasInverse)return null;inverse.Invert();p=inverse.Transform(p);
   if(current==null||p.X<0||p.Y<0||p.X>=ActualWidth||p.Y>=h)return null;
   int x=Math.Min(current.PixelWidth-1,Math.Max(0,(int)(p.X/ActualWidth*current.PixelWidth))),y=Math.Min(current.PixelHeight-1,Math.Max(0,(int)(p.Y/h*current.PixelHeight)));
   byte[] pixel=new byte[4];current.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);return pixel[3]>24?new PointHitTestResult(this,p):null;
  }
  protected override void OnRender(DrawingContext dc) {
   base.OnRender(dc);double h=petHeight>0?petHeight:ActualHeight;
   dc.PushTransform(new MatrixTransform(DisplayMatrix()));
   dc.DrawImage(current,new Rect(0,0,ActualWidth,h));
   if(continuousLook){
    // Vector irises move continuously within fixed eye openings. The sprite
    // itself never switches between 22.5-degree look images while following.
    dc.PushTransform(new ScaleTransform(ActualWidth/192,h/208));
    DrawEye(dc,71.5,90.5,7.2,8.6);DrawEye(dc,105.7,90.5,7.7,8.7);
    dc.Pop();
   }
   dc.Pop();
  }
  void DrawEye(DrawingContext dc,double x,double y,double rx,double ry){
   Geometry opening=x<90?leftEye:rightEye;
   dc.PushClip(opening);
   dc.DrawGeometry(sclera,null,opening);
   double cx=x+look.X*3.0,cy=y+look.Y*2.3;
   dc.DrawEllipse(iris,null,new Point(cx,cy),5.6,8.1);
   dc.DrawEllipse(pupil,null,new Point(cx,cy-1.6),2.6,4.3);
   dc.DrawEllipse(glint,null,new Point(cx-2.1,cy-4),1.15,1.45);
   dc.DrawEllipse(smallGlint,null,new Point(cx+2.0,cy-2),.8,1.0);
   if(blink>.01)dc.DrawRectangle(skin,null,new Rect(x-rx-1,y-ry-1,rx*2+2,(ry*2+2)*blink));
   dc.Pop();
   dc.DrawGeometry(null,lashPen,x<90?leftLash:rightLash);
  }
  static Brush FrozenBrush(Color color){var brush=new SolidColorBrush(color);brush.Freeze();return brush;}
  static Brush IrisBrush(){var brush=new LinearGradientBrush();brush.StartPoint=new Point(0,0);brush.EndPoint=new Point(0,1);brush.GradientStops.Add(new GradientStop(Color.FromRgb(56,69,78),0));brush.GradientStops.Add(new GradientStop(Color.FromRgb(108,137,137),.58));brush.GradientStops.Add(new GradientStop(Color.FromRgb(188,209,193),1));brush.Freeze();return brush;}
  static Pen MakeLashPen(){var pen=new Pen(FrozenBrush(Color.FromRgb(65,52,57)),1.3);pen.Freeze();return pen;}
  static Geometry EyeOpening(double x,double y,double rx,double ry){
   var geometry=new StreamGeometry();using(var c=geometry.Open()){
    c.BeginFigure(new Point(x-rx,y-3),true,true);
    c.BezierTo(new Point(x-rx*.55,y-ry*1.05),new Point(x+rx*.65,y-ry*1.05),new Point(x+rx,y-2),true,false);
    c.BezierTo(new Point(x+rx*.95,y+ry*.9),new Point(x+rx*.35,y+ry*1.1),new Point(x,y+ry),true,false);
    c.BezierTo(new Point(x-rx*.9,y+ry*.95),new Point(x-rx,y+ry*.25),new Point(x-rx,y-3),true,false);
   }geometry.Freeze();return geometry;
  }
  static Geometry EyeLash(double x,double y,double rx,double ry){
   var geometry=new StreamGeometry();using(var c=geometry.Open()){
    c.BeginFigure(new Point(x-rx-.4,y-3),false,false);
    c.BezierTo(new Point(x-rx*.55,y-ry*1.05),new Point(x+rx*.65,y-ry*1.05),new Point(x+rx+.4,y-2),true,false);
   }geometry.Freeze();return geometry;
  }
 }
 public sealed class InfoWindow:Window {
  readonly TextBlock model=new TextBlock(),primaryLabel=new TextBlock(),primary=new TextBlock(),primaryReset=new TextBlock(),secondaryLabel=new TextBlock(),secondary=new TextBlock(),secondaryReset=new TextBlock(),freshness=new TextBlock();
  readonly Border frame;
  readonly CompanionWindow pet;
  public InfoWindow(CompanionWindow owner) {
   pet=owner;Title="白璃 · 剩余用量";Width=120;Height=86;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=null;
   ResizeMode=ResizeMode.NoResize;Topmost=true;ShowInTaskbar=false;FontFamily=new FontFamily("Microsoft YaHei UI");
   UseLayoutRounding=true;SnapsToDevicePixels=true;
   frame=new Border{CornerRadius=new CornerRadius(7),BorderThickness=new Thickness(1),BorderBrush=Brush(222,226,229),Background=Brush(249,250,251),Padding=new Thickness(5,4,5,4)};Content=frame;
   StackPanel panel=new StackPanel();frame.Child=panel;
   model.Height=14;model.VerticalAlignment=VerticalAlignment.Center;model.TextTrimming=TextTrimming.CharacterEllipsis;model.Foreground=Brush(43,51,56);panel.Children.Add(model);
   panel.Children.Add(new Border{Height=1,Background=Brush(225,229,232),Margin=new Thickness(0,2,0,2)});
   panel.Children.Add(new TextBlock{Text="剩余用量",FontSize=8,Height=11,Foreground=Brush(103,113,120)});
   AddUsage(panel,primaryLabel,primary,primaryReset);AddUsage(panel,secondaryLabel,secondary,secondaryReset);
   freshness.FontSize=8;freshness.Height=11;freshness.Foreground=Brush(135,143,149);freshness.Margin=new Thickness(0,1,0,0);panel.Children.Add(freshness);
   KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){e.Handled=true;Close();}};
   Update(null);
  }
  static SolidColorBrush Brush(byte red,byte green,byte blue){return new SolidColorBrush(Color.FromRgb(red,green,blue));}
  static void AddUsage(StackPanel panel,TextBlock label,TextBlock amount,TextBlock reset){
   Grid row=new Grid{Height=16};
   row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(30)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(29)});row.ColumnDefinitions.Add(new ColumnDefinition());
   label.FontSize=9;label.Foreground=Brush(55,63,69);amount.FontSize=9;amount.FontWeight=FontWeights.SemiBold;amount.TextAlignment=TextAlignment.Right;
   reset.FontSize=8.5;reset.Foreground=Brush(117,126,133);reset.TextAlignment=TextAlignment.Right;
   foreach(TextBlock text in new[]{label,amount,reset})text.VerticalAlignment=VerticalAlignment.Center;
   Grid.SetColumn(amount,1);Grid.SetColumn(reset,2);row.Children.Add(label);row.Children.Add(amount);row.Children.Add(reset);panel.Children.Add(row);
  }
  public void Update(CompanionSnapshot s){
   model.Inlines.Clear();
   model.Inlines.Add(new System.Windows.Documents.Run(s==null?"读取中…":String.IsNullOrWhiteSpace(s.Model)?"模型未知":s.Model){FontSize=9,FontWeight=FontWeights.SemiBold});
   if(s!=null&&!String.IsNullOrEmpty(s.Effort))model.Inlines.Add(new System.Windows.Documents.Run(" · "+s.Effort){FontSize=8,Foreground=Brush(115,125,133)});
   SetUsage(primaryLabel,primary,primaryReset,s==null?null:s.Primary,"短期");SetUsage(secondaryLabel,secondary,secondaryReset,s==null?null:s.Secondary,"长期");
   bool live=s!=null&&s.LimitsSource=="app-server"&&s.LimitsObservedUtc.HasValue&&(DateTime.UtcNow-s.LimitsObservedUtc.Value).TotalMinutes<2;
   string source=live?"已同步 ":s!=null&&s.LimitsSource=="cached-app-server"?"缓存 ":"记录 ";
   freshness.Text=s!=null&&s.LimitsObservedUtc.HasValue?source+s.LimitsObservedUtc.Value.ToLocalTime().ToString("HH:mm:ss"):"等待额度同步";
   string status=s==null?"读取中":s.State==CompanionWorkState.Working?"任务进行中":s.State==CompanionWorkState.Idle?"最近任务已结束":s.State==CompanionWorkState.Interrupted?"最近任务已中断":"任务状态未知";
   string explanation=(s!=null&&s.LimitsObservedUtc.HasValue?"额度更新于 "+s.LimitsObservedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+"。":"尚无可用额度。")+"\n"+(live?"通过 Codex 官方接口读取；打开面板时刷新，后台约30秒同步，收到额度通知时更新。":"当前显示缓存或本地日志记录；连接成功后自动更新。")+"\n模型取自当前绑定任务的运行记录，可能与尚未发送的选择不同。\n"+status+(s==null||String.IsNullOrEmpty(s.ConnectionNotice)?"":"\n"+s.ConnectionNotice)+"\n右键人物 → 对话与模型，可以查看或发送消息。";
   freshness.ToolTip=explanation;frame.ToolTip=explanation;
   model.ToolTip="最近任务模型："+(s==null||String.IsNullOrWhiteSpace(s.Model)?"未知":s.Model)+(s==null||String.IsNullOrEmpty(s.Effort)?"":" · "+s.Effort)+"\n"+explanation;
  }
  static void SetUsage(TextBlock label,TextBlock amount,TextBlock reset,UsageWindow window,string fallback){
   label.Text=WindowLabel(window==null?null:window.WindowMinutes,fallback);
   if(window==null||!window.RemainingPercent.HasValue){amount.Text=window!=null&&window.IsExpired?"已重置":"未知";amount.Foreground=Brush(135,143,149);reset.Text="待更新";amount.ToolTip=reset.ToolTip=window!=null&&window.IsExpired?"窗口已重置，等待新的用量记录。":"尚无可用的用量记录。";return;}
   amount.Text=window.RemainingPercent.Value.ToString("0.#")+"%";amount.Foreground=Brush(55,63,69);
   reset.Text="—";
   if(window.ResetsAtUtc.HasValue){DateTime time=window.ResetsAtUtc.Value.ToLocalTime();bool longWindow=window.WindowMinutes.HasValue&&window.WindowMinutes.Value>=1440;reset.Text=longWindow?time.ToString("M/d"):time.Date==DateTime.Today?time.ToString("HH:mm"):time.Date==DateTime.Today.AddDays(1)?"明日":time.ToString("M/d");}
   string detail="剩余 "+amount.Text+(window.ResetsAtUtc.HasValue?"；重置时间 "+window.ResetsAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"；重置时间暂无记录");amount.ToolTip=reset.ToolTip=detail;
  }
  static string WindowLabel(int? minutes,string fallback){
   if(!minutes.HasValue)return fallback;
   if(minutes.Value%10080==0)return (minutes.Value/10080)+"周";
   if(minutes.Value%1440==0)return (minutes.Value/1440)+"天";
   return minutes.Value>=60?(minutes.Value/60.0).ToString("0.#")+"小时":minutes.Value+"分钟";
  }
 }
}
