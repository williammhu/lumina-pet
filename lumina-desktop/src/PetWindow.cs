using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace LuminaDesktop
{
    public class AlphaImage:FrameworkElement
    {
        byte[] hitPixels;int pw,ph,stride;
        public NativeSpriteLayer Layer;public PetWindow Owner;
        public void Present(int width,int height,byte[] premultiplied)
        {
            pw=width;ph=height;stride=pw*4;hitPixels=premultiplied;
            RefreshLayer();
        }
        public void PresentStatic(BitmapSource bitmap)
        {
            if(ReferenceEquals(Tag,bitmap))return;Tag=bitmap;pw=bitmap.PixelWidth;ph=bitmap.PixelHeight;stride=pw*4;
            BitmapSource converted=bitmap.Format==PixelFormats.Pbgra32?bitmap:new FormatConvertedBitmap(bitmap,PixelFormats.Pbgra32,null,0);
            hitPixels=new byte[stride*ph];converted.CopyPixels(hitPixels,stride,0);
            RefreshLayer();
        }
        public void RefreshLayer(){if(Layer==null||Owner==null||!Owner.IsLoaded||!Owner.IsVisible||hitPixels==null||ActualWidth<=0||ActualHeight<=0)return;var origin=PointToScreen(new Point(0,0));var dpi=VisualTreeHelper.GetDpi(this);Layer.Present(hitPixels,pw,ph,(int)Math.Round(origin.X),(int)Math.Round(origin.Y),(int)Math.Round(ActualWidth*dpi.DpiScaleX),(int)Math.Round(ActualHeight*dpi.DpiScaleY));}
        protected override Size MeasureOverride(Size available){return new Size(Double.IsNaN(Width)?0:Width,Double.IsNaN(Height)?0:Height);}
        protected override Size ArrangeOverride(Size finalSize){return finalSize;}
        protected override HitTestResult HitTestCore(PointHitTestParameters p)
        {
            if(hitPixels==null||ActualWidth<=0||ActualHeight<=0)return null;
            double s=Math.Min(ActualWidth/pw,ActualHeight/ph),ox=(ActualWidth-pw*s)/2,oy=(ActualHeight-ph*s)/2;
            int x=(int)((p.HitPoint.X-ox)/s),y=(int)((p.HitPoint.Y-oy)/s);
            if(p.HitPoint.X<ox||p.HitPoint.Y<oy||x<0||y<0||x>=pw||y>=ph||hitPixels[y*stride+x*4+3]<20)return null;
            return new PointHitTestResult(this,p.HitPoint);
        }
    }
    public class PetWindow:Window
    {
        public const string WindowTitle="露米娜伴行";
        public Settings Config;public ActivityMonitor Monitor;public MonitorSnapshot Snapshot;
        public double VisibleCharacterHeight{get{return image.Height*.875;}}
        public string AnimationState{get{return player.State;}}
        public bool ClickThrough{get{return through;}}
        public bool StatusBarVisible{get{return bubble.Visibility==Visibility.Visible;}}
        public string StatusText{get{return title.Text;}}
        public double StatusBarGap{get{return image.Height*45/1536;}}
        public int TargetFps{get{return player.Fps;}}
        public int FrameBufferBytes{get{return player.Pixels==null?0:player.Pixels.Length;}}
        public int CompressedBufferBytes{get{return player.CompressedBufferBytes;}}
        public bool NativeSurfaceReady{get{return image.Layer!=null&&image.Layer.Ready;}}
        public object SurfaceDiagnostic{get{return new{owner_loaded=IsLoaded,owner_visible=IsVisible,actual_image_width=image.ActualWidth,actual_image_height=image.ActualHeight,layer_exists=image.Layer!=null,layer_handle=image.Layer==null?0:image.Layer.Handle.ToInt64(),presents=image.Layer==null?0:image.Layer.SuccessfulPresents,window_handle=hwnd.ToInt64()};}}
        public bool HotkeysAvailable=true;
        readonly string assets;readonly FrameStream player=new FrameStream();
        readonly Dictionary<string,string> paths=new Dictionary<string,string>();
        readonly List<PetAction> actions=new List<PetAction>();
        readonly ActionDirector director;
        public string[] ActionStates{get{return actions.Select(a=>a.State).ToArray();}}
        public int ActionCount{get{return actions.Count;}}
        readonly bool tests;
        Grid root;AlphaImage image;Border bubble,dot;TextBlock title;DispatcherTimer controller;
        Forms.NotifyIcon tray;SettingsWindow settingsWindow;IntPtr hwnd;
        BitmapSource portrait;int portraitHeight;
        bool through,pressing,dragging,subscribed;Native.Point down;double startX,startY;
        string manual="";DateTime manualUntil=DateTime.MinValue,lastDiagnostic=DateTime.MinValue;
        long stateStart,renderStart,renderTotal;long presented;int displayed=-1;
        System.Threading.Timer nativeTicker;int renderQueued;
        public double MeasuredFps{get{double seconds=(renderTotal+(subscribed?Stopwatch.GetTimestamp()-renderStart:0))/(double)Stopwatch.Frequency;return seconds>0?presented/seconds:0;}}

        public PetWindow(Settings config,bool testMode)
        {
            tests=testMode;Config=config;Title=WindowTitle;assets=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"assets");
            var manifest=Json.Parse(File.ReadAllText(Path.Combine(assets,"animations.json")));
            foreach(var p in Json.Obj(Json.Get(manifest,"states")))paths[p.Key]=Json.Str(Json.Obj(p.Value),"path");
            var states=Json.Obj(Json.Get(manifest,"states"));
            int reserve=0;foreach(var p in states){int n;Int32.TryParse(Json.Str(Json.Obj(p.Value),"max_compressed_bytes"),out n);reserve=Math.Max(reserve,n);}if(reserve>0)player.ReserveCompressed(reserve);
            foreach(var a in Json.Arr(Json.Get(manifest,"actions"))) {
                var p=Json.Obj(a);string state=Json.Str(p,"state");if(!paths.ContainsKey(state)||actions.Any(x=>x.State==state))continue;
                var metadata=Json.Obj(states[state]);int weight;Int32.TryParse(Json.Str(p,"ambient_weight"),out weight);
                string category=Json.Str(p,"category");
                actions.Add(new PetAction{State=state,Label=Json.Str(p,"label"),Category=category.Length==0?"基础":category,Weight=weight,Click=Json.Str(p,"click")=="True",Seconds=Convert.ToDouble(Json.Get(metadata,"frames"))/Convert.ToDouble(Json.Get(metadata,"fps"))});
            }
            director=new ActionDirector(actions,DateTime.UtcNow,Environment.TickCount);
            WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=null;ShowInTaskbar=false;Topmost=Config.Topmost;ShowActivated=false;UseLayoutRounding=true;SnapsToDevicePixels=true;FontFamily=new FontFamily("Microsoft YaHei UI");
            BuildView();
            SourceInitialized+=(s,e)=>{hwnd=new WindowInteropHelper(this).Handle;HwndSource.FromHwnd(hwnd).AddHook(Hook);Native.SetWindowLong(hwnd,-20,Native.GetWindowLong(hwnd,-20)|0x08000000|0x80);image.Layer=new NativeSpriteLayer(hwnd,this);if(!tests){bool p=Native.RegisterHotKey(hwnd,1,0x4003,0x50),l=Native.RegisterHotKey(hwnd,2,0x4003,0x4c);HotkeysAvailable=p&&l;}};
            Loaded+=(s,e)=>{ApplyLayout();if(Config.LayoutVersion<4){if(!Double.IsNaN(Config.Left))Config.Left+=(image.Height*720/1056-image.Width)/2;Config.LayoutVersion=4;}if(Double.IsNaN(Config.Left)||Double.IsNaN(Config.Top))ResetPosition();else{Left=Config.Left;Top=Config.Top;ClampToScreen();}SavePosition();UpdateController();};
            IsVisibleChanged+=(s,e)=>{if(image.Layer!=null)image.Layer.SetVisible(IsVisible);SyncRendering();};LocationChanged+=(s,e)=>image.RefreshLayer();SizeChanged+=(s,e)=>image.RefreshLayer();Closed+=(s,e)=>Cleanup();
            InputBindings.Add(new KeyBinding(new SimpleCommand(ShowSettings),new KeyGesture(Key.OemComma,ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new SimpleCommand(()=>SetHeight(Config.Height+50)),new KeyGesture(Key.Add,ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new SimpleCommand(()=>SetHeight(Config.Height-50)),new KeyGesture(Key.Subtract,ModifierKeys.Control)));
            Monitor=new ActivityMonitor(Config.Home){Pinned=Config.Pinned};Monitor.Changed+=s=>{if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(()=>ApplySnapshot(s)));};
            if(!tests){CreateTray();Monitor.Start();}
            controller=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};controller.Tick+=(s,e)=>UpdateController();controller.Start();SetAnimation("idle");
        }
        void BuildView()
        {
            root=new Grid{Background=null};root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(34)});Content=root;
            image=new AlphaImage{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom,Cursor=Cursors.Hand};AutomationProperties.SetName(image,"露米娜，拖动移动，滚轮缩放，右键设置");root.Children.Add(image);
            image.Owner=this;
            bubble=new Border{Height=28,MinWidth=92,MaxWidth=190,Padding=new Thickness(12,0,12,0),CornerRadius=new CornerRadius(14),BorderBrush=Theme.Brush("#DCDCDC"),BorderThickness=new Thickness(1),Background=Theme.Brush("#F9F9FA"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top};
            var row=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};dot=new Border{Width=6,Height=6,CornerRadius=new CornerRadius(3),Background=Theme.Violet,Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center};row.Children.Add(dot);
            title=Theme.Text("连接中",12,Theme.Ink);title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;row.Children.Add(title);bubble.Child=row;Grid.SetRow(bubble,1);root.Children.Add(bubble);
            image.MouseLeftButtonDown+=Down;image.MouseMove+=Move;image.MouseLeftButtonUp+=Up;
            image.MouseWheel+=(s,e)=>{SetHeight(Config.Height+(e.Delta>0?40:-40));e.Handled=true;};image.MouseRightButtonUp+=(s,e)=>{OpenMenu();e.Handled=true;};
            bubble.MouseRightButtonUp+=(s,e)=>OpenMenu();bubble.MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==2)OpenCodex();};
        }
        public void ApplyLayout()
        {
            if(hwnd==IntPtr.Zero)new WindowInteropHelper(this).EnsureHandle();
            var screen=Forms.Screen.FromHandle(hwnd);double dpi=VisualTreeHelper.GetDpi(this).DpiScaleY;
            double bar=Config.ShowBubble?34:0,available=screen.WorkingArea.Height/dpi-bar-12,h=Math.Max(180,Math.Min(Config.Height/.875,available));
            root.RowDefinitions[0].Height=new GridLength(h);root.RowDefinitions[1].Height=new GridLength(bar);
            bubble.Visibility=Config.ShowBubble?Visibility.Visible:Visibility.Collapsed;image.Height=h;image.Width=h*1280/1536;Width=Math.Max(Config.ShowBubble?140:100,image.Width);Height=h+bar;
        }
        public void SetHeight(double value)
        {
            double bottom=Top+image.Height,center=Left+Width/2;Config.Height=Math.Max(280,Math.Min(1200,value));ApplyLayout();
            if(!Double.IsNaN(bottom))Top=bottom-image.Height;if(!Double.IsNaN(center))Left=center-Width/2;ClampToScreen();SavePosition();UpdateController();
        }
        public void ResetPosition(){ApplyLayout();UpdateLayout();var a=Forms.Screen.FromHandle(hwnd).WorkingArea;var dpi=VisualTreeHelper.GetDpi(this);Native.SetWindowPos(hwnd,IntPtr.Zero,a.Right-(int)(Width*dpi.DpiScaleX)-20,a.Bottom-(int)(Height*dpi.DpiScaleY)-12,0,0,0x15);SavePosition();}
        public void ClampToScreen(){if(hwnd==IntPtr.Zero)return;Native.Rect r;Native.GetWindowRect(hwnd,out r);var a=Forms.Screen.FromHandle(hwnd).WorkingArea;int x=Math.Max(a.Left,Math.Min(r.Left,a.Right-(r.Right-r.Left))),y=Math.Max(a.Top,Math.Min(r.Top,a.Bottom-(r.Bottom-r.Top)));Native.SetWindowPos(hwnd,IntPtr.Zero,x,y,0,0,0x15);}
        void SavePosition(){if(tests)return;Config.Left=Left;Config.Top=Top;Config.Save();}
        void Down(object sender,MouseButtonEventArgs e){if(e.ClickCount==2){OpenCodex();e.Handled=true;return;}Native.GetCursorPos(out down);startX=Left;startY=Top;pressing=true;dragging=false;image.CaptureMouse();e.Handled=true;}
        void Move(object sender,MouseEventArgs e){if(!pressing)return;Native.Point p;Native.GetCursorPos(out p);double dx=p.X-down.X,dy=p.Y-down.Y;if(Math.Abs(dx)+Math.Abs(dy)>6)dragging=true;if(dragging){var dpi=VisualTreeHelper.GetDpi(this);Left=startX+dx/dpi.DpiScaleX;Top=startY+dy/dpi.DpiScaleY;manual=dx>=0?"running-right":"running-left";manualUntil=DateTime.UtcNow.AddMilliseconds(300);}}
        void Up(object sender,MouseButtonEventArgs e){if(!pressing)return;pressing=false;image.ReleaseMouseCapture();if(dragging){ClampToScreen();SavePosition();}else Play(director.ClickResponse());dragging=false;if(e!=null)e.Handled=true;}
        internal void NativeDown(bool twice){if(twice){OpenCodex();return;}Native.GetCursorPos(out down);startX=Left;startY=Top;pressing=true;dragging=false;}
        internal void NativeMove(){Move(null,null);}internal void NativeUp(){Up(null,null);}internal void NativeMenu(){OpenMenu();}
        public void Play(string state){Config.Portrait=false;Config.Animate=true;Config.Save();SetAnimation(state);manual=player.State;manualUntil=DateTime.UtcNow.AddSeconds(player.Count/(double)player.Fps);director.Reset(manualUntil);SyncRendering();}
        internal void ExerciseState(string state,double seconds){SetAnimation(state);manual=state;manualUntil=DateTime.UtcNow.AddSeconds(seconds);SyncRendering();}
        public int ValidateAssets(){int count=0;foreach(var p in paths){player.Open(p.Key,AssetPath(p.Value));for(int i=0;i<player.Count;i++){player.Read(i);count++;}}return count;}
        internal int[] InspectFrameBounds(string state,int index)
        {
            SetAnimation(state);PresentFrame(index);int left=player.Width,top=player.Height,right=-1,bottom=-1;
            for(int y=0;y<player.Height;y++)for(int x=0;x<player.Width;x++)if(player.Pixels[(y*player.Width+x)*4+3]>32){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
            return new[]{left,top,right-left+1,bottom-top+1};
        }
        string AssetPath(string relative){string path=Path.GetFullPath(Path.Combine(assets,relative.Replace('/',Path.DirectorySeparatorChar)));if(!path.StartsWith(assets+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Asset path");return path;}
        public void SetAnimation(string state)
        {
            if(!paths.ContainsKey(state))state="idle";if(player.State==state)return;
            player.Open(state,AssetPath(paths[state]));stateStart=Stopwatch.GetTimestamp();displayed=-1;
            if(!Config.Portrait)PresentFrame(0);
        }
        void PresentFrame(int index){if(displayed==index&&image.Tag==null)return;image.Tag=null;player.Read(index);image.Present(player.Width,player.Height,player.Pixels);displayed=index;}
        void PresentPortrait()
        {
            int desired=Math.Min(1659,Math.Max(480,(int)Math.Ceiling(image.Height*VisualTreeHelper.GetDpi(this).DpiScaleY)));
            if(portrait==null||Math.Abs(desired-portraitHeight)>80){using(var stream=File.OpenRead(Path.Combine(assets,"portrait.png"))){var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.DecodePixelHeight=desired;b.StreamSource=stream;b.EndInit();b.Freeze();portrait=b;portraitHeight=desired;}}
            image.PresentStatic(portrait);displayed=-1;
        }
        internal static string WorkAnimation(Activity a,DateTime now)
        {
            if(a.State=="waiting")return "waiting";if(a.State=="failed")return "failed";
            if(a.Busy)return a.State=="editing"?"writing":a.State=="searching"?"reading":a.State=="drawing"?"magic":"running";
            return a.State=="ready"&&now-a.Updated<TimeSpan.FromSeconds(12)?"review":"idle";
        }
        void UpdateController()
        {
            if(!IsVisible){SyncRendering();return;}string state="idle";
            if(manualUntil>DateTime.UtcNow)state=manual;
            else {
                if(Snapshot!=null)state=WorkAnimation(Snapshot.Current,DateTime.UtcNow);
                state=director.Select(state,Config.RandomActions&&Config.Animate&&!Config.Portrait&&SystemParameters.ClientAreaAnimation,DateTime.UtcNow);
            }
            if(state=="idle"&&Config.FollowMouse&&!Config.Portrait&&Config.Animate){Native.Point p;Native.GetCursorPos(out p);var center=PointToScreen(new Point(Width/2,image.Height*.23));double dx=p.X-center.X,dy=p.Y-center.Y;if(dx*dx+dy*dy>4900){int i=((int)Math.Round((Math.Atan2(dx,-dy)*180/Math.PI+360)%360/22.5))%16;SetAnimation(i<8?"look-row-9":"look-row-10");PresentFrame(i%8);SyncRendering();return;}}
            SetAnimation(state);if(Config.Portrait)PresentPortrait();else if(!Config.Animate||!SystemParameters.ClientAreaAnimation)PresentFrame(0);SyncRendering();
        }
        void SyncRendering()
        {
            bool enabled=IsVisible&&Config.Animate&&!Config.Portrait&&SystemParameters.ClientAreaAnimation&&!player.State.StartsWith("look-");
            if(enabled==subscribed)return;subscribed=enabled;
            if(enabled){
                renderStart=Stopwatch.GetTimestamp();
                nativeTicker=new System.Threading.Timer(_=>{
                    if(Dispatcher.HasShutdownStarted||Interlocked.CompareExchange(ref renderQueued,1,0)!=0)return;
                    try{Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{try{if(subscribed)Render(null,null);}finally{Interlocked.Exchange(ref renderQueued,0);}}));}catch(InvalidOperationException){Interlocked.Exchange(ref renderQueued,0);}
                },null,0,15);
            }
            else{renderTotal+=Stopwatch.GetTimestamp()-renderStart;if(nativeTicker!=null){nativeTicker.Dispose();nativeTicker=null;}}
        }
        void Render(object sender,EventArgs e)
        {
            if(!subscribed||player.Count==0)return;double seconds=(Stopwatch.GetTimestamp()-stateStart)/(double)Stopwatch.Frequency;
            int index=(int)(seconds*player.Fps)%player.Count;if(index==displayed)return;PresentFrame(index);presented++;
        }
        public void ApplySnapshot(MonitorSnapshot snapshot)
        {
            Snapshot=snapshot;var a=snapshot.Current;string text=ActivityParser.Label(a.State);if(title.Text!=text)title.Text=text;
            dot.Background=a.State=="waiting"?Theme.Amber:a.State=="failed"?Theme.Red:a.Busy?Theme.Green:Theme.Violet;AutomationProperties.SetName(bubble,text);
            if(settingsWindow!=null)settingsWindow.RefreshSessions();UpdateController();
            if(!tests&&DateTime.UtcNow-lastDiagnostic>TimeSpan.FromSeconds(5)){lastDiagnostic=DateTime.UtcNow;WriteDiagnostic(Path.Combine(Settings.DataDir,"runtime.json"));}
        }
        public void WriteDiagnostic(string path)
        {
            try{var a=Snapshot==null?new Activity():Snapshot.Current;using(var process=Process.GetCurrentProcess())Json.Save(path,new{version="4.0.0",pid=process.Id,state=a.State,animation=player.State,action_count=ActionCount,random_actions=Config.RandomActions,thread_id=a.Id,codex_running=Snapshot!=null&&Snapshot.CodexRunning,active_count=Snapshot==null?0:Snapshot.ActiveCount,updated_utc=a.Updated,height_setting=Config.Height,actual_window_height=Height,visible_character_height=VisibleCharacterHeight,click_through=through,hotkeys_available=HotkeysAvailable,status_bar_visible=StatusBarVisible,status_bar_placement="below feet",status_bar_text=StatusText,target_fps=30,measured_fps=Math.Round(MeasuredFps,2),decoded_frames=player.DecodedFrames,frame_buffer_bytes=FrameBufferBytes,compressed_frame_buffer_bytes=CompressedBufferBytes,codec=player.Codec,cached_animation_frames=1,animation_size=new[]{player.Width,player.Height},working_set_mb=Math.Round(process.WorkingSet64/1048576.0,1),private_memory_mb=Math.Round(process.PrivateMemorySize64/1048576.0,1),native_surface=SurfaceDiagnostic,source="bounded read-only local Codex events"});}catch{}
        }
        public void OpenCodex(){foreach(string name in new[]{"ChatGPT","Codex"})foreach(var p in Process.GetProcessesByName(name)){using(p){try{if(p.MainWindowHandle!=IntPtr.Zero){Native.SetForegroundWindow(p.MainWindowHandle);return;}}catch{}}}try{Process.Start(new ProcessStartInfo("codex://"){UseShellExecute=true});}catch{}}
        public void ShowSettings(){if(settingsWindow!=null){settingsWindow.Activate();return;}settingsWindow=new SettingsWindow(this);settingsWindow.Closed+=(s,e)=>settingsWindow=null;settingsWindow.Show();}
        public void ToggleVisible(){if(IsVisible)Hide();else{Show();ClampToScreen();UpdateController();}}
        public void SetClickThrough(bool value){through=value;int style=Native.GetWindowLong(hwnd,-20);Native.SetWindowLong(hwnd,-20,value?style|0x20:style&~0x20);if(image.Layer!=null)image.Layer.SetThrough(value);}
        IntPtr Hook(IntPtr h,int msg,IntPtr w,IntPtr l,ref bool handled){if(msg==0x312){if(w.ToInt32()==1)ToggleVisible();if(w.ToInt32()==2)SetClickThrough(!through);handled=true;}if(msg==0x8000+44){if(w.ToInt32()==2)Close();else{SetClickThrough(false);Show();ClampToScreen();if(w.ToInt32()==1)ShowSettings();}handled=true;}return IntPtr.Zero;}
        MenuItem Item(string text,Action action){var i=new MenuItem{Header=text};i.Click+=(s,e)=>action();return i;}
        public MenuItem CreateActionMenu()
        {
            var poses=new MenuItem{Header="动作"};
            foreach(var group in actions.GroupBy(a=>a.Category)){
                var category=new MenuItem{Header=group.Key};
                foreach(var action in group){string state=action.State;category.Items.Add(Item(action.Label,()=>Play(state)));}
                poses.Items.Add(category);
            }
            poses.Items.Add(new Separator());
            var auto=new MenuItem{Header="空闲时随机动作",IsCheckable=true,IsChecked=Config.RandomActions};
            auto.Click+=(s,e)=>{Config.RandomActions=auto.IsChecked;director.Reset(DateTime.UtcNow);Config.Save();};poses.Items.Add(auto);return poses;
        }
        void OpenMenu()
        {
            var menu=new ContextMenu{FontFamily=FontFamily,FontSize=13};menu.Items.Add(Item("设置",ShowSettings));menu.Items.Add(Item("回到 Codex",OpenCodex));menu.Items.Add(CreateActionMenu());
            var hide=new MenuItem{Header="隐藏任务栏",IsCheckable=true,IsChecked=!Config.ShowBubble};hide.Click+=(s,e)=>{Config.ShowBubble=!hide.IsChecked;ApplyLayout();Config.Save();};menu.Items.Add(hide);
            var click=new MenuItem{Header="鼠标穿透",IsCheckable=true,IsChecked=through};click.Click+=(s,e)=>SetClickThrough(click.IsChecked);menu.Items.Add(click);menu.Items.Add(new Separator());menu.Items.Add(Item("隐藏桌宠",Hide));menu.Items.Add(Item("退出",Close));menu.IsOpen=true;
        }
        void CreateTray()
        {
            tray=new Forms.NotifyIcon{Icon=new System.Drawing.Icon(Path.Combine(assets,"lumina.ico")),Text="露米娜伴行",Visible=true};var menu=new Forms.ContextMenuStrip();
            menu.Items.Add("显示 / 隐藏",null,(s,e)=>Dispatcher.Invoke(new Action(ToggleVisible)));menu.Items.Add("设置",null,(s,e)=>Dispatcher.Invoke(new Action(ShowSettings)));menu.Items.Add("恢复鼠标操作",null,(s,e)=>Dispatcher.Invoke(new Action(()=>{SetClickThrough(false);Show();})));menu.Items.Add("退出",null,(s,e)=>Dispatcher.Invoke(new Action(Close)));tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>Dispatcher.Invoke(new Action(()=>{SetClickThrough(false);Show();ClampToScreen();}));
        }
        public void Capture(string file){
            UpdateLayout();image.RefreshLayer();var dpi=VisualTreeHelper.GetDpi(this);int w=(int)Math.Ceiling(ActualWidth*dpi.DpiScaleX),h=(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY);
            var chrome=new RenderTargetBitmap(w,h,96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);chrome.Render(this);
            var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){
                dc.DrawImage(chrome,new Rect(0,0,ActualWidth,ActualHeight));
                if(image.Layer!=null){var native=image.Layer.Snapshot(dpi.DpiScaleX,dpi.DpiScaleY);if(native!=null){Point origin=image.TranslatePoint(new Point(0,0),this);dc.DrawImage(native,new Rect(origin.X,origin.Y,image.ActualWidth,image.ActualHeight));}}
            }
            var combined=new RenderTargetBitmap(w,h,96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);combined.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(combined));Directory.CreateDirectory(Path.GetDirectoryName(file));using(var f=File.Create(file))encoder.Save(f);
        }
        void Cleanup(){subscribed=false;if(nativeTicker!=null){nativeTicker.Dispose();nativeTicker=null;}controller.Stop();Monitor.Dispose();if(image.Layer!=null)image.Layer.Dispose();player.Dispose();if(tray!=null){tray.Visible=false;tray.Dispose();}if(settingsWindow!=null)settingsWindow.Close();if(hwnd!=IntPtr.Zero){Native.UnregisterHotKey(hwnd,1);Native.UnregisterHotKey(hwnd,2);}SavePosition();}
    }
    public class SimpleCommand:ICommand{readonly Action action;public SimpleCommand(Action a){action=a;}public bool CanExecute(object p){return true;}public void Execute(object p){action();}public event EventHandler CanExecuteChanged{add{}remove{}}}
}
