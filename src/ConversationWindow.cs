using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexCompanion {
 public sealed class ConversationWindow:Window {
  readonly CodexConnection connection;
  readonly ConversationBridge history;
  readonly Border surface;
  readonly TextBox transcript=new TextBox(),input=new TextBox();
  readonly TextBlock status=new TextBlock(),sourceLabel=new TextBlock();
  readonly ComboBox models=new ComboBox(),efforts=new ComboBox();
  readonly RadioButton currentMode=new RadioButton(),directMode=new RadioButton();
  readonly Button send=new Button(),stop=new Button(),login=new Button(),openSource=new Button();
  readonly Grid modelRow=new Grid(),composer=new Grid();
  readonly DispatcherTimer timer=new DispatcherTimer();
  string sourceId,sourceModel,catalogKey,lastTranscript="",localStatus;
  bool reading,closed,submitting,review;
  DateTime lastRead=DateTime.MinValue;
  ConversationSnapshot current;
  LiveConnectionSnapshot live;
  public ConversationWindow(CodexConnection connection,ConversationBridge history){
   this.connection=connection;this.history=history;
   Title="白璃 · 对话";Width=420;Height=550;MinWidth=340;MinHeight=380;
   WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=null;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=Program.Config==null||Program.Config.Topmost;WindowStartupLocation=WindowStartupLocation.CenterScreen;
   FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=12;UseLayoutRounding=true;
   surface=new Border{Background=Brushes.White,BorderBrush=ColorBrush(215,225,224),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(14)};Content=surface;
   var grid=new Grid();surface.Child=grid;
   foreach(var h in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto,GridLength.Auto})grid.RowDefinitions.Add(new RowDefinition{Height=h});
   var header=new DockPanel{Height=30};
   var close=Button("×",delegate{Close();});close.Width=28;close.FontSize=18;DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
   header.Children.Add(new TextBlock{Text="对话",FontSize=16,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Foreground=ColorBrush(36,61,57)});
   header.MouseLeftButtonDown+=delegate(object s,MouseButtonEventArgs e){if(e.OriginalSource is TextBlock)DragMove();};Add(grid,header,0);
   var tabs=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,8)};
   currentMode.Content="当前聊天";directMode.Content="和白璃聊";currentMode.GroupName=directMode.GroupName="conversation-mode";currentMode.IsChecked=true;
   currentMode.Margin=new Thickness(0,0,22,0);tabs.Children.Add(currentMode);tabs.Children.Add(directMode);Add(grid,tabs,1);
   currentMode.Checked+=delegate{ModeChanged();};directMode.Checked+=delegate{ModeChanged();};
   modelRow.Margin=new Thickness(0,0,0,8);modelRow.ColumnDefinitions.Add(new ColumnDefinition());modelRow.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(85)});
   models.DisplayMemberPath="Name";models.SelectedValuePath="Id";models.MinHeight=28;models.ToolTip="发送下一条消息时使用的模型";models.SelectionChanged+=delegate{UpdateEfforts();};modelRow.Children.Add(models);
   efforts.MinHeight=28;efforts.Margin=new Thickness(7,0,0,0);efforts.ToolTip="思考强度";Grid.SetColumn(efforts,1);modelRow.Children.Add(efforts);Add(grid,modelRow,2);
   transcript.IsReadOnly=true;transcript.AcceptsReturn=true;transcript.TextWrapping=TextWrapping.Wrap;transcript.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;transcript.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
   transcript.BorderThickness=new Thickness(0);transcript.Background=ColorBrush(248,250,249);transcript.Padding=new Thickness(10);transcript.FontSize=12;transcript.Foreground=ColorBrush(38,51,48);Add(grid,transcript,3);
   var footer=new DockPanel{Margin=new Thickness(0,7,0,7)};
   openSource=Button("打开原对话",delegate{if(!String.IsNullOrEmpty(sourceId))Program.Open("codex://threads/"+Uri.EscapeDataString(sourceId));});DockPanel.SetDock(openSource,Dock.Right);footer.Children.Add(openSource);
   sourceLabel.FontSize=10;sourceLabel.Foreground=ColorBrush(111,127,122);sourceLabel.VerticalAlignment=VerticalAlignment.Center;sourceLabel.TextTrimming=TextTrimming.CharacterEllipsis;footer.Children.Add(sourceLabel);Add(grid,footer,4);
   input.AcceptsReturn=true;input.TextWrapping=TextWrapping.Wrap;input.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;input.Height=74;input.Padding=new Thickness(8);input.FontSize=12;
   input.ToolTip="输入消息，Ctrl+Enter 发送";input.PreviewKeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){e.Handled=true;Send();}};
   composer.Children.Add(input);Add(grid,composer,5);
   var actions=new DockPanel{Margin=new Thickness(0,8,0,0)};
   send=Button("发送",Send);send.Width=57;DockPanel.SetDock(send,Dock.Right);actions.Children.Add(send);
   stop=Button("停止",Stop);stop.Width=52;stop.Margin=new Thickness(0,0,6,0);DockPanel.SetDock(stop,Dock.Right);actions.Children.Add(stop);
   login=Button("登录 Codex",Login);DockPanel.SetDock(login,Dock.Left);login.Margin=new Thickness(0,0,6,0);actions.Children.Add(login);
   status.FontSize=10;status.Foreground=ColorBrush(103,121,115);status.VerticalAlignment=VerticalAlignment.Center;status.TextTrimming=TextTrimming.CharacterEllipsis;actions.Children.Add(status);Add(grid,actions,6);
   timer.Interval=TimeSpan.FromMilliseconds(200);timer.Tick+=delegate{Tick();};
   Loaded+=delegate{timer.Start();ReadHistory();LoadSavedChat();};
   Closed+=delegate{closed=true;timer.Stop();};KeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Escape){e.Handled=true;Close();}};
   ModeChanged();
  }
  static SolidColorBrush ColorBrush(byte r,byte g,byte b){return new SolidColorBrush(Color.FromRgb(r,g,b));}
  static Button Button(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(7,3,7,3),MinHeight=26};b.Click+=delegate{action();};return b;}
  static void Add(Grid parent,UIElement child,int row){Grid.SetRow(child,row);parent.Children.Add(child);}
  public void SetSource(string threadId,string model){if(sourceId!=threadId){sourceId=threadId;lastRead=DateTime.MinValue;current=null;}sourceModel=model;ChooseInitialModel();}
  public void UpdateConnection(LiveConnectionSnapshot state){
   live=state;
   string key=state.Models==null?"":String.Join("|",state.Models.Select(m=>m.Id));
   if(catalogKey!=key){string selected=models.SelectedValue as string;catalogKey=key;models.ItemsSource=state.Models;models.SelectedValue=selected;ChooseInitialModel();}
   Render();
  }
  void ChooseInitialModel(){
   if(models.SelectedItem!=null||live==null||live.Models==null)return;
   var candidate=live.Models.FirstOrDefault(m=>m.Id==sourceModel)??live.Models.FirstOrDefault(m=>m.IsDefault)??live.Models.FirstOrDefault();
   if(candidate!=null)models.SelectedValue=candidate.Id;
  }
  void UpdateEfforts(){
   string previous=efforts.SelectedItem as string;var model=models.SelectedItem as LiveModel;
   var values=model==null?null:model.Efforts;efforts.ItemsSource=values;
   if(values!=null&&values.Count>0)efforts.SelectedItem=values.Contains(previous??"")?previous:values.Contains("medium")?"medium":values[0];
  }
  void ModeChanged(){
   bool direct=directMode.IsChecked==true;modelRow.Visibility=composer.Visibility=direct?Visibility.Visible:Visibility.Collapsed;
   send.Visibility=stop.Visibility=direct?Visibility.Visible:Visibility.Collapsed;openSource.Visibility=direct?Visibility.Collapsed:Visibility.Visible;Render();
  }
  void Tick(){if(closed||review)return;if(connection!=null)UpdateConnection(connection.Snapshot);if(currentMode.IsChecked==true&&(DateTime.UtcNow-lastRead).TotalSeconds>=1)ReadHistory();}
  async void ReadHistory(){
   if(reading||history==null||String.IsNullOrEmpty(sourceId)||closed)return;reading=true;lastRead=DateTime.UtcNow;string id=sourceId;
   try{var value=await Task.Run(()=>history.Read(id));if(!closed&&sourceId==id){current=value;Render();}}catch{if(!closed)localStatus="暂时无法读取对话";}finally{reading=false;}
  }
  async void LoadSavedChat(){
   if(connection==null||Program.Config==null||String.IsNullOrEmpty(Program.Config.ChatThreadId))return;
   try{await connection.LoadChatAsync(Program.Config.ChatThreadId);}catch{if(!closed)localStatus="上次对话暂未载入，发送时会重试";}
  }
  void Render(){
   if(surface==null)return;bool direct=directMode.IsChecked==true;var text=new StringBuilder();
   if(direct){
    if(live!=null&&live.Messages!=null)foreach(var message in live.Messages){text.AppendLine(message.Role=="user"?"你":"白璃");text.AppendLine(message.Text);text.AppendLine();}
    if(text.Length==0)text.Append("在这里和白璃对话，回复会逐步显示。\n\n此处使用单独的对话，不会向正在工作的原对话插入消息。");
    sourceLabel.Text="Ctrl+Enter 发送";sourceLabel.ToolTip=live==null?null:live.ChatThreadId;
   }else{
    if(current!=null&&current.Messages!=null)foreach(var message in current.Messages){text.AppendLine(message.Role=="user"?"你":"助手");text.AppendLine(message.Text);text.AppendLine();}
    if(text.Length==0)text.Append(String.IsNullOrEmpty(sourceId)?"等待当前聊天记录…":"等待可读取的对话内容…");
    sourceLabel.Text=current!=null&&current.Truncated?"显示最近对话 · 每秒更新":"跟随当前绑定聊天 · 每秒更新";sourceLabel.ToolTip=(current==null?"等待本地对话记录":current.Notice)+"\n"+sourceId;
   }
   string next=text.ToString();if(next!=lastTranscript){bool atBottom=Math.Max(0,transcript.ExtentHeight-transcript.ViewportHeight)-transcript.VerticalOffset<24;double offset=transcript.VerticalOffset;lastTranscript=next;transcript.Text=next;if(atBottom)transcript.ScrollToEnd();else transcript.ScrollToVerticalOffset(offset);}
   bool sending=live!=null&&live.IsSending;
   send.IsEnabled=!submitting&&!sending&&live!=null&&live.Authenticated;stop.IsEnabled=sending;models.IsEnabled=efforts.IsEnabled=!sending&&!submitting;
   login.Visibility=live!=null&&live.Authenticated?Visibility.Collapsed:Visibility.Visible;
   status.Text=localStatus??(sending?"正在回复…":live==null?"正在连接…":live.Status);status.ToolTip=status.Text;openSource.IsEnabled=!String.IsNullOrEmpty(sourceId);
  }
  async void Send(){
   if(connection==null||submitting||String.IsNullOrWhiteSpace(input.Text))return;
   string model=models.SelectedValue as string;if(String.IsNullOrEmpty(model)){localStatus="等待模型列表，或先登录 Codex";Render();return;}
   submitting=true;localStatus=null;string text=input.Text;Render();
   try{await connection.SendAsync(text,model,efforts.SelectedItem as string,Program.Config==null?null:Program.Config.ChatThreadId);if(input.Text==text)input.Clear();}
   catch(Exception error){localStatus=error.Message;}
   finally{submitting=false;if(!closed)UpdateConnection(connection.Snapshot);}
  }
  async void Stop(){if(connection==null)return;try{await connection.InterruptAsync();}catch(Exception error){localStatus=error.Message;}if(!closed)Render();}
  async void Login(){if(connection==null)return;try{localStatus=null;var url=await connection.BeginLoginAsync();if(url!=null)Program.Open(url.AbsoluteUri);}catch(Exception error){localStatus=error.Message;}if(!closed)Render();}
  internal FrameworkElement ReviewSurface{get{return surface;}}
  internal void SetReview(ConversationSnapshot source,LiveConnectionSnapshot state,bool direct){review=true;current=source;UpdateConnection(state);directMode.IsChecked=direct;currentMode.IsChecked=!direct;Render();}
 }
}
