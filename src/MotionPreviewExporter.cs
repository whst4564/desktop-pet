using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace CodexCompanion {
 public static class MotionPreviewExporter {
  public static void Export(string folder){
   Directory.CreateDirectory(folder);
   var viewport=new PetViewport(208);
   var sprite=new SpriteSurface(Program.LoadAtlas()){Width=192,Height=208,ClipToBounds=false};
   var stage=new Canvas{Width=viewport.WindowWidth,Height=viewport.WindowHeight,ClipToBounds=false};
   Canvas.SetLeft(sprite,viewport.Padding);Canvas.SetTop(sprite,viewport.Padding);stage.Children.Add(sprite);
   stage.Measure(new Size(stage.Width,stage.Height));stage.Arrange(new Rect(0,0,stage.Width,stage.Height));stage.UpdateLayout();
   var pose=new AnimationPose{ScaleX=1,ScaleY=1};
   int noGhostChecks;var noGhostDetails=ExportGhosting(folder,sprite,stage,pose,out noGhostChecks);
   sprite.SetPose("idle",0,pose,208,null);sprite.SetCarry(0,.5,.25,0,0);
   Save(stage,Path.Combine(folder,"standing-reference.png"));
   for(int i=0;i<64;i++){
    double angle=i*Math.PI*2/64;
    sprite.SetPose("idle",0,pose,208,null);sprite.SetCarry(0,.5,.25,0,0);
    for(int step=0;step<8;step++)sprite.SetContinuousLook(true,Math.Sin(angle)*700,-Math.Cos(angle)*700,1.0/60,1);
    Save(stage,Path.Combine(folder,"gaze-"+i.ToString("00")+".png"));
   }
   sprite.SetContinuousLook(false,0,0,.016,1);
   var cases=new List<object>();
   cases.Add(ExportCarry(folder,"top-slow",.5,.12,35,0,sprite,stage,viewport,pose));
   cases.Add(ExportCarry(folder,"top-fast",.5,.12,350,0,sprite,stage,viewport,pose));
   cases.Add(ExportCarry(folder,"side-fast",.73,.42,300,90,sprite,stage,viewport,pose));
   cases.Add(ExportCarry(folder,"lower-fast",.5,.84,350,0,sprite,stage,viewport,pose));
   sprite.SetCarry(0,.5,.25,0,0);
   for(int i=0;i<128;i++){
    sprite.SetPose(sprite.HasClip("thinking-calm")?"thinking-calm":"running",i,pose,208,null);
    Save(stage,Path.Combine(folder,"thinking-"+i.ToString("000")+".png"));
   }
   File.WriteAllText(Path.Combine(folder,"renderer-info.json"),new JavaScriptSerializer().Serialize(new {
    source="production enhanced WPF renderer; simulated pointer input, not a desktop recording",
    gazeFrames=64,thinkingFrames=128,carryCases=cases,accountData=false,
    noGhostInterpolatedCels=true,noGhostCelChecks=noGhostChecks,noGhostDetails=noGhostDetails,
    imageWidth=192,imageHeight=208,padding=viewport.Padding,width=(int)Math.Ceiling(stage.Width),height=(int)Math.Ceiling(stage.Height)
   }));
  }
  static List<object> ExportGhosting(string folder,SpriteSurface sprite,Canvas stage,AnimationPose pose,out int checks){
   checks=0;var details=new List<object>();string target=Path.Combine(folder,"no-ghost-cels");Directory.CreateDirectory(target);
   BitmapSource atlas=Program.LoadAtlas();
   if(atlas.Format!=PixelFormats.Bgra32){var converted=new FormatConvertedBitmap(atlas,PixelFormats.Bgra32,null,0);converted.Freeze();atlas=converted;}
   string[] ids={"idle","waving","jumping","failed","waiting","review"};int[] rows={0,3,4,5,6,8},counts={6,4,5,8,6,6};
   sprite.SetContinuousLook(false,0,0,1.0/60,0);sprite.SetCarry(0,.5,.25,0,0);
   for(int clip=0;clip<ids.Length;clip++)for(int frame=0;frame<counts[clip];frame++){
    var original=new CroppedBitmap(atlas,new Int32Rect(frame*192,rows[clip]*208,192,208));original.Freeze();string expected=Stamp(original);
    foreach(double blend in new[]{.125,.5,.875}){
     sprite.SetPose(ids[clip],frame,pose,208,null,blend);
     if(Stamp(sprite.CurrentFrame)!=expected)throw new InvalidOperationException("Animation introduces a mixed silhouette: "+ids[clip]+" frame "+frame+" sample "+blend);
     if(blend==.5)Save(stage,Path.Combine(target,ids[clip]+"-"+frame.ToString("000")+".png"));
     checks++;
    }
    details.Add(new{clip=ids[clip],frame=frame,samples=3,sourceCelSha256=expected});
   }
   if(checks!=105)throw new InvalidOperationException("Ghosting validation has an unexpected sample count.");
   return details;
  }
  // Synthetic layout review only: no account reads, network, message sends,
  // companion windows, native controls, or real conversation text.
  public static void ExportConnectionPreview(string folder){
   Directory.CreateDirectory(folder);DateTime now=DateTime.UtcNow;
   var history=new ConversationSnapshot{ThreadId="preview-current-thread",ObservedUtc=now,Messages=new List<ConversationMessage>{
    new ConversationMessage{Id="preview-user-1",Role="user",Text="我想查看当前聊天和剩余额度。",ObservedUtc=now},
    new ConversationMessage{Id="preview-assistant-1",Role="assistant",Text="桌宠可以显示当前聊天，额度会通过官方接口定期同步。\n\n这里是界面预览，内容不来自真实对话。",ObservedUtc=now},
    new ConversationMessage{Id="preview-user-2",Role="user",Text="还要可以直接发消息。",ObservedUtc=now},
    new ConversationMessage{Id="preview-assistant-2",Role="assistant",Text="切换到“和白璃聊”，选择模型后输入消息即可。",ObservedUtc=now}
   }};
   var live=new LiveConnectionSnapshot{Connected=true,Authenticated=true,Status="界面预览 · 不会发送消息",QuotaObservedUtc=now,SelectedModel="example-model",SelectedEffort="high",
    ChatThreadId="preview-direct-thread",Models=new List<LiveModel>{
     new LiveModel{Id="example-model",Name="示例模型",IsDefault=true,Efforts=new List<string>{"medium","high"}},
     new LiveModel{Id="example-model-2",Name="示例模型 2",Efforts=new List<string>{"low","medium","high"}}
    },Messages=new List<LiveChatMessage>{
     new LiveChatMessage{Id="preview-direct-user",Role="user",Text="你好，白璃。帮我整理一下今天要做的事情。"},
     new LiveChatMessage{Id="preview-direct-assistant",Role="assistant",Text="当然可以。把你的任务发给我，我会帮你排好顺序。\n\n这张图只展示布局，没有发送真实消息。"}
    }};
   var currentWindow=new ConversationWindow(null,null);currentWindow.SetSource(history.ThreadId,"example-model");currentWindow.SetReview(history,live,false);
   SaveLayout(currentWindow.ReviewSurface,420,550,Path.Combine(folder,"conversation-current-synthetic.png"));currentWindow.Close();
   var directWindow=new ConversationWindow(null,null);directWindow.SetSource(history.ThreadId,"example-model");directWindow.SetReview(history,live,true);
   SaveLayout(directWindow.ReviewSurface,420,550,Path.Combine(folder,"conversation-direct-synthetic.png"));directWindow.Close();
   var quotaWindow=new InfoWindow(null);quotaWindow.Update(new CompanionSnapshot{Model="示例模型",Effort="high",LimitsSource="app-server",LimitsObservedUtc=now,State=CompanionWorkState.Idle,
    Primary=new UsageWindow{UsedPercent=25,WindowMinutes=300,ResetsAtUtc=now.AddHours(2)},
    Secondary=new UsageWindow{UsedPercent=10,WindowMinutes=10080,ResetsAtUtc=now.AddDays(5)}});
   SaveLayout((FrameworkElement)quotaWindow.Content,120,86,Path.Combine(folder,"quota-synthetic.png"));quotaWindow.Close();
   File.WriteAllText(Path.Combine(folder,"connection-preview-info.json"),new JavaScriptSerializer().Serialize(new{source="actual WPF controls with synthetic example data",accountData=false,realConversationData=false,networkRequests=0,messagesSent=0,windowsShown=0,conversationWidth=420,conversationHeight=550,quotaWidth=120,quotaHeight=86}));
  }
  static void SaveLayout(FrameworkElement surface,double width,double height,string path){
   surface.Width=width;surface.Height=height;surface.Measure(new Size(width,height));surface.Arrange(new Rect(0,0,width,height));surface.UpdateLayout();Save(surface,path);
  }
  static object ExportCarry(string folder,string name,double gx,double gy,double vx,double vy,SpriteSurface sprite,Canvas stage,PetViewport viewport,AnimationPose pose){
   string target=Path.Combine(folder,name);Directory.CreateDirectory(target);
   var carry=new CarryMotion();carry.BeginGrab(gx,gy,gx,gy,0,0,208);
   double x=0,y=0,maxAngle=0,maxAnchorError=0,maxInverseError=0;bool sameCel=true,hitsMatch=true,contained=true;
   string originalCel=null;var frames=new List<object>();
   for(int i=0;i<264;i++){
    bool held=i<120;
    double direction=i>=12&&i<48?1:i>=72&&i<108?-1:0;
    if(held){x+=direction*vx/60;y+=direction*vy/60;}
    carry.Update(held,x,y,208,1.0/60);
    sprite.SetPose("idle",0,pose,208,null);sprite.SetCarry(carry.Angle,carry.GrabX,carry.GrabY,carry.OffsetX,carry.OffsetY);
    Matrix expected=Matrix.Identity;expected.RotateAt(carry.Angle,192*gx,208*gy);expected.Translate(carry.OffsetX*192,carry.OffsetY*208);
    Point anchor=expected.Transform(new Point(192*gx,208*gy));
    maxAnchorError=Math.Max(maxAnchorError,(anchor-new Point(192*gx,208*gy)).Length);
    Point opaque=expected.Transform(new Point(96,130)),clear=expected.Transform(new Point(5,190));
    hitsMatch&=sprite.HitsSprite(opaque)&&!sprite.HitsSprite(clear);
    maxInverseError=Math.Max(maxInverseError,(sprite.SourcePoint(opaque)-new Point(96,130)).Length);
    Rect bounds=sprite.PresentedBounds();bounds.Offset(viewport.Padding,viewport.Padding);
    contained&=bounds.Left>=0&&bounds.Top>=0&&bounds.Right<=stage.Width&&bounds.Bottom<=stage.Height;
    string cel=Stamp(sprite.CurrentFrame);if(originalCel==null)originalCel=cel;else sameCel&=originalCel==cel;
    maxAngle=Math.Max(maxAngle,Math.Abs(carry.Angle));
    Save(stage,Path.Combine(target,"carry-"+i.ToString("000")+".png"));
    frames.Add(new {index=i,held=held,cursorX=x,cursorY=y,angle=carry.Angle,angularVelocity=carry.AngularVelocity,
     grabX=carry.GrabX,grabY=carry.GrabY,offsetX=carry.OffsetX,offsetY=carry.OffsetY,active=carry.Active});
   }
   if(!sameCel||!hitsMatch||!contained||maxAnchorError>1e-8||maxInverseError>1e-8||carry.Active)
    throw new InvalidOperationException("Carry render validation failed: "+name);
   return new {name=name,frames=frames,maxAngle=maxAngle,maxAnchorError=maxAnchorError,maxInverseError=maxInverseError,
    sameSpriteForEveryCarryFrame=sameCel,transformedHitTestsMatched=hitsMatch,unclipped=contained,releasedAtRest=!carry.Active,spriteSha256=originalCel};
  }
  static string Stamp(BitmapSource image){byte[] bytes=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(bytes,image.PixelWidth*4,0);using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
  static void Save(FrameworkElement surface,string path){
   surface.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(surface.Width),(int)Math.Ceiling(surface.Height),96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
   var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);
  }
 }
}
