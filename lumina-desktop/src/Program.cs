using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace LuminaDesktop
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try {
                if(args.Contains("--self-test"))return SelfTest.Run(args);
                if(args.Contains("--performance-test"))return PerformanceTest.Run(args);
                if(args.Contains("--diagnose")) {
                    var config=Settings.Load();var monitor=new ActivityMonitor(config.Home){Pinned=config.Pinned};var s=monitor.Scan();
                    string output=Option(args,"--out")??Path.Combine(Settings.DataDir,"diagnostic.json");
                    Json.Save(output,new{version="4.0.0",codex_running=s.CodexRunning,active_count=s.ActiveCount,state=s.Current.State,thread_id=s.Current.Id,sessions=s.Sessions.Select(a=>new{id=a.Id,state=a.State,updated=a.Updated,child=a.Child}).ToArray(),error=s.Error,read_only=true});return 0;
                }
                bool created;using(var single=new Mutex(true,"Local\\LuminaCompanion."+Environment.UserName,out created)) {
                    if(!created){IntPtr hwnd=Native.FindWindow(null,PetWindow.WindowTitle);Native.PostMessage(hwnd,0x8000+44,new IntPtr(args.Contains("--exit")?2:args.Contains("--settings")?1:0),IntPtr.Zero);return 0;}
                    if(args.Contains("--exit"))return 0;
                    var app=new Application {ShutdownMode=ShutdownMode.OnMainWindowClose};
                    app.DispatcherUnhandledException+=(s,e)=>{Error(e.Exception);e.Handled=true;app.Shutdown(1);};
                    var pet=new PetWindow(Settings.Load(),false);app.MainWindow=pet;
                    pet.Loaded+=(s,e)=>{
                        if(args.Contains("--settings"))pet.ShowSettings();
                        string qa=Option(args,"--capture-live");if(qa!=null){var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(4)};timer.Tick+=(sender,ev)=>{timer.Stop();pet.Capture(Path.Combine(qa,"live-pet.png"));pet.WriteDiagnostic(Path.Combine(qa,"live-binding.json"));};timer.Start();}
                    };
                    app.Run(pet);single.ReleaseMutex();return 0;
                }
            } catch(Exception ex){Error(ex);return 1;}
        }
        static void Error(Exception e)
        {
            try{Directory.CreateDirectory(Settings.DataDir);File.AppendAllText(Path.Combine(Settings.DataDir,"errors.log"),DateTime.UtcNow.ToString("o")+" "+e.GetType().FullName+" "+ActivityParser.Clean(e.Message,180)+Environment.NewLine);}catch{}
            MessageBox.Show("露米娜暂时无法启动。请保留整个程序文件夹，再重新运行。\n\n错误类型："+e.GetType().Name,"露米娜伴行",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        public static string Option(string[] args,string key){int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
    }
    public static class SelfTest
    {
        static List<string> passed=new List<string>();
        static void Check(bool condition,string name){if(!condition)throw new Exception("Test failed: "+name);passed.Add(name);}
        static string Event(string type,object payload){return Json.Serializer().Serialize(new{timestamp=DateTime.UtcNow.ToString("o"),type=type,payload=payload});}
        static void Feed(Activity a,string kind){ActivityParser.Apply(a,Event("event_msg",new{type=kind,turn_id="test-turn"}));}
        public static int Run(string[] args)
        {
            string qa=Program.Option(args,"--out")??Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qa-v4");Directory.CreateDirectory(qa);
            var a=new Activity();Feed(a,"task_started");Check(a.Busy&&a.State=="thinking","turn starts");
            ActivityParser.Apply(a,Event("response_item",new{type="custom_tool_call",name="exec",call_id="a",input="text(await tools.exec_command({cmd:'test'}));"}));Check(a.State=="command","wrapped command begins before output");
            ActivityParser.Apply(a,Event("response_item",new{type="custom_tool_call",name="exec",call_id="b",input="text(await tools.apply_patch('test'));"}));Check(a.State=="editing"&&a.Pending.Count==2,"parallel tool tracking");
            ActivityParser.Apply(a,Event("response_item",new{type="custom_tool_call_output",call_id="b",output="ignored"}));Check(a.State=="command"&&a.Pending.Count==1,"finishing one tool retains the other");
            Feed(a,"task_complete");Check(!a.Busy&&a.State=="ready"&&a.Pending.Count==0,"completion clears pending work");
            Feed(a,"task_started");Check(a.Busy&&a.State=="thinking","next turn resets complete state");
            Feed(a,"turn_aborted");Check(!a.Busy&&a.State=="paused","interrupted turn stops");
            Feed(a,"error");Check(a.State=="failed","turn error state");
            string before=a.State;ActivityParser.Apply(a,"{broken");Check(a.State==before,"malformed journal line ignored");
            ActivityParser.Apply(a,Event("response_item",new{type="reasoning",summary="PRIVATE_REASONING"}));Check(a.State==before&&!a.Detail.Contains("PRIVATE"),"reasoning never displayed");
            ActivityParser.Apply(a,Event("session_meta",new{id="child",cwd="test",source=new{subagent=new{parent_thread_id="main"}}}));Check(a.Child,"subagents excluded");
            string clean=ActivityParser.Clean("api_key=123456 password=private Bearer abcdefghijklmnop https://example.test/?secret=x",200);Check(!clean.Contains("123456")&&!clean.Contains("private")&&!clean.Contains("abcdefghijklmnop")&&!clean.Contains("secret=x"),"secret and URL masking");
            Check(ActivityParser.ToolState("exec","text(await tools.apply_patch('request_user_input inside source text')); ")=="editing","code literals do not fake waiting state");
            Check(ActivityParser.ToolState("exec","await Promise.allSettled([tools.exec_command({cmd:'test'})]);")=="command","batched commands detected");
            Check(ActivityParser.ToolState("exec","await tools.apply_patch(\"tools.request_user_input({})\");")=="editing","tool call text in patch ignored");
            Check(ActivityParser.ToolState("request_user_input","{}")=="waiting","input request state");
            Check(ActivityParser.ToolState("exec","await tools.image_gen__imagegen({});")=="drawing","image tool state");
            Check(ActivityParser.ToolState("exec","await tools.web__run({});")=="searching","search tool state");
            string temp=Path.Combine(qa,"temporary-tail.jsonl");var encoding=new UTF8Encoding(false);
            string header=Event("session_meta",new{id="main",cwd="C:\\test",source="vscode"})+"\n";
            File.WriteAllText(temp,header,encoding);var tail=new JsonTail(temp);tail.Poll();Check(tail.Activity.Id=="main","journal metadata");
            string unicode=Event("event_msg",new{type="item_completed",item=new{type="AgentMessage",phase="commentary",content=new[]{new{type="Text",text="正在检查银龙的窗口"}}}})+"\n";
            byte[] bytes=encoding.GetBytes(unicode);int split=Array.FindIndex(bytes,b=>b>=0xe0)+1;
            using(var f=new FileStream(temp,FileMode.Append)){f.Write(bytes,0,split);}tail.Poll();Check(!tail.Activity.Detail.Contains("正在检查"),"partial UTF8 line waits");
            using(var f=new FileStream(temp,FileMode.Append)){f.Write(bytes,split,bytes.Length-split);}tail.Poll();Check(tail.Activity.Detail=="正在检查银龙的窗口","UTF8 split chunks recovered");
            File.WriteAllText(temp,header,encoding);tail.Poll();Check(tail.Activity.Id=="main"&&!tail.Activity.Busy,"truncation resets journal");File.Delete(temp);
            File.WriteAllText(temp,header,encoding);var bounded=new JsonTail(temp);bounded.Poll();
            File.AppendAllText(temp,Event("event_msg",new{type="task_started"})+"\n"+Event("response_item",new{type="custom_tool_call_output",call_id="large",output=new string('x',600000)})+"\n"+Event("event_msg",new{type="item_completed",item=new{type="AgentMessage",content=new[]{new{type="Text",text=new string('x',300000)}},phase="final"}})+"\n",encoding);
            bounded.Poll();Check(bounded.Activity.State=="ready","long final message suffix retained");Check(bounded.BufferBytes<=40000,"journal buffering stays bounded on large records");File.Delete(temp);
            string fakeHome=Path.Combine(qa,"test-codex-home");string oldFolder=Path.Combine(fakeHome,"sessions","2021","01","01");Directory.CreateDirectory(oldFolder);
            File.WriteAllText(Path.Combine(oldFolder,"rollout-test-old-main.jsonl"),Event("session_meta",new{id="old-main",cwd="C:\\test",source="vscode"})+"\n"+Event("event_msg",new{type="task_complete"})+"\n",encoding);
            var oldMonitor=new ActivityMonitor(fakeHome){Pinned="old-main"};Check(oldMonitor.Scan().Current.Id=="old-main","resumed old-date journal discovered");
            DateTime clock=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
            var sampleActions=new[]{new PetAction{State="tea",Seconds=2,Weight=1,Click=true},new PetAction{State="reading",Seconds=2,Weight=1,Click=true},new PetAction{State="bowing",Seconds=2,Weight=1}};
            var director=new ActionDirector(sampleActions,clock,42);
            Check(director.Select("idle",true,clock)=="idle","ambient motions leave an initial quiet interval");
            string first=director.Select("idle",true,clock.AddSeconds(20));
            Check(first!="idle"&&director.Select("idle",true,clock.AddSeconds(21))==first,"ambient action plays for its full duration");
            Check(director.Select("running",true,clock.AddSeconds(21))=="running"&&director.Select("waiting",true,clock.AddSeconds(22))=="waiting","Codex work and waiting immediately interrupt ambient motion");
            Check(director.Select("idle",false,clock.AddMinutes(5))=="idle","random animation toggle is respected");
            string click1=director.ClickResponse(),click2=director.ClickResponse();Check(click1!=click2&&new[]{"tea","reading"}.Contains(click2),"click response varies without immediate repeats");
            var reached=new HashSet<string>();string previous="";bool repeats=false;
            for(int i=1;i<=120;i++){DateTime tick=clock.AddMinutes(6).AddSeconds(i*20);string selected=director.Select("idle",true,tick);if(selected!="idle"){reached.Add(selected);if(previous==selected)repeats=true;previous=selected;}director.Select("idle",true,tick.AddSeconds(3));}
            Check(reached.Count==3&&!repeats,"all ambient actions are reachable without immediate repeats");
            Check(PetWindow.WorkAnimation(new Activity{State="editing",Busy=true},clock)=="writing"&&PetWindow.WorkAnimation(new Activity{State="searching",Busy=true},clock)=="reading"&&PetWindow.WorkAnimation(new Activity{State="drawing",Busy=true},clock)=="magic","Codex editing search and drawing use the new relevant motions");
            // Delete only this explicitly created test fixture inside the QA directory.
            File.Delete(Path.Combine(oldFolder,"rollout-test-old-main.jsonl"));Directory.Delete(oldFolder);Directory.Delete(Path.GetDirectoryName(oldFolder));Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(oldFolder)));Directory.Delete(Path.Combine(fakeHome,"sessions"));Directory.Delete(fakeHome);
            var app=new Application {ShutdownMode=ShutdownMode.OnMainWindowClose};
            var settings=new Settings { Home=Settings.Load().Home,Height=650,SaveEnabled=false };var pet=new PetWindow(settings,true);app.MainWindow=pet;
            pet.Loaded+=(s,e)=>pet.Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>{
                try {
                    pet.UpdateLayout();
                    Check(pet.ActionCount>=21&&pet.ActionStates.Distinct().Count()==pet.ActionCount,"at least 21 independently addressable actions");
                    Check(pet.ValidateAssets()==1340,"all 1340 streaming animation frames decode");
                    using(var streaming=new FrameStream()){
                        string motion=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"assets","motion-1k");
                        streaming.Open("tea",Path.Combine(motion,"tea.luma"));streaming.Read(0);byte[] buffer=streaming.Pixels;
                        streaming.Open("reading",Path.Combine(motion,"reading.luma"));streaming.Read(1);
                        Check(Object.ReferenceEquals(buffer,streaming.Pixels),"switching distinct actions reuses the same decoded pixel buffer");
                        Check(streaming.Codec=="windows-xpress"&&streaming.Pixels.Length==1280*1536*4&&streaming.CompressedBufferBytes>0,"native full-resolution frame decoding uses bounded reusable buffers");
                    }
                    var actionMenu=pet.CreateActionMenu();var groups=actionMenu.Items.OfType<System.Windows.Controls.MenuItem>().Where(m=>m.HasItems).ToArray();
                    Check(groups.Length==4&&groups.Sum(m=>m.Items.Count)==pet.ActionCount,"four action groups expose every action in the native menu");
                    pet.Config.Portrait=true;pet.Config.Animate=false;pet.Play("tea");
                    Check(!pet.Config.Portrait&&pet.Config.Animate&&pet.AnimationState=="tea","point-play visibly enables selected animation");
                    pet.Capture(Path.Combine(qa,"ui-tea.png"));
                    Check(pet.TargetFps==30&&pet.FrameBufferBytes==1280*1536*4,"1280x1536 at 30 FPS with one reusable pixel buffer");
                    int[] idleBox=pet.InspectFrameBounds("idle",0);double fixedWidth=pet.Width,fixedHeight=pet.Height;
                    int[] magicBox=pet.InspectFrameBounds("magic",36);pet.Capture(Path.Combine(qa,"ui-magic-1k.png"));
                    int[] wingsBox=pet.InspectFrameBounds("wings",36);pet.Capture(Path.Combine(qa,"ui-wings-1k.png"));
                    Check(Math.Abs(magicBox[3]-idleBox[3])<idleBox[3]*.02,"star spell retains idle character height within two percent");
                    Check(Math.Abs(wingsBox[3]-idleBox[3])<idleBox[3]*.02,"wing stretch retains idle character height within two percent");
                    Check(Math.Abs(magicBox[1]+magicBox[3]-idleBox[1]-idleBox[3])<=3&&Math.Abs(wingsBox[1]+wingsBox[3]-idleBox[1]-idleBox[3])<=3,"wide actions preserve the feet baseline");
                    Check(pet.Width==fixedWidth&&pet.Height==fixedHeight&&magicBox[0]>6&&magicBox[0]+magicBox[2]<1274&&wingsBox[0]>6&&wingsBox[0]+wingsBox[2]<1274,"wide actions fit without resizing the window or clipping wings");
                    Json.Save(Path.Combine(qa,"scale-check.json"),new{idle=idleBox,magic=magicBox,wings=wingsBox,window_width=fixedWidth,window_height=fixedHeight});
                    pet.ApplySnapshot(new MonitorSnapshot { Current=new Activity {State="command",Detail="界面检查 · 正在运行命令",Title="露米娜桌宠",Busy=true},CodexRunning=true,ActiveCount=1 });pet.SetAnimation("running");pet.Capture(Path.Combine(qa,"ui-running.png"));
                    Check(pet.StatusText=="运行命令"&&pet.StatusBarGap<30,"compact status stays beside feet");
                    pet.WriteDiagnostic(Path.Combine(qa,"native-surface-test.json"));
                    Check(pet.NativeSurfaceReady,"native transparent surface is visible and successfully presented");
                    pet.Config.ShowBubble=false;pet.ApplyLayout();Check(!pet.StatusBarVisible,"hide taskbar setting");pet.Capture(Path.Combine(qa,"ui-hidden-taskbar.png"));pet.Config.ShowBubble=true;pet.ApplyLayout();
                    double large=pet.Height;pet.SetHeight(300);double small=pet.Height;Check(large>small+100,"large size exceeds compact size");pet.Capture(Path.Combine(qa,"ui-small.png"));
                    pet.SetHeight(1200);Check(pet.Height<=System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(pet).Handle).WorkingArea.Height/System.Windows.Media.VisualTreeHelper.GetDpi(pet).DpiScaleY+1,"size fits display");pet.SetHeight(650);
                    pet.ApplySnapshot(new MonitorSnapshot {Current=new Activity {State="waiting",Detail="界面检查 · 请回到 Codex 确认",Title="露米娜桌宠"}});pet.SetAnimation("waiting");pet.Capture(Path.Combine(qa,"ui-waiting.png"));
                    pet.ApplySnapshot(new MonitorSnapshot {Current=new Activity {State="ready",Detail="界面检查 · 本轮任务已完成",Title="露米娜桌宠"}});pet.Play("waving");pet.Capture(Path.Combine(qa,"ui-complete.png"));
                    pet.SetClickThrough(true);Check(pet.ClickThrough,"click through enabled");pet.SetClickThrough(false);Check(!pet.ClickThrough,"click through recoverable");
                    var sw=new SettingsWindow(pet);sw.Show();sw.UpdateLayout();var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)sw.ActualWidth,(int)sw.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(sw);var enc=new System.Windows.Media.Imaging.PngBitmapEncoder();enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(qa,"ui-settings.png")))enc.Save(f);sw.Close();
                    Json.Save(Path.Combine(qa,"tests.json"),new{ok=true,checks=passed.ToArray(),count=passed.Count});pet.Close();
                }catch(Exception ex){Json.Save(Path.Combine(qa,"tests.json"),new{ok=false,error=ex.Message,checks=passed.ToArray()});pet.Close();}
            }));
            app.Run(pet);return Json.Str(Json.Parse(File.ReadAllText(Path.Combine(qa,"tests.json"))),"ok")=="True"?0:1;
        }
    }
}
