using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using Forms=System.Windows.Forms;
namespace LuminaDesktop
{
    public class Settings
    {
        [System.Web.Script.Serialization.ScriptIgnore] public bool SaveEnabled=true;
        public double Height=480,Left=Double.NaN,Top=Double.NaN;
        public bool Topmost=true,Animate=true,Portrait=false,FollowMouse=false,ShowBubble=true,RandomActions=true;
        public string Home="",Pinned="";public int LayoutVersion=4;
        public static string DataDir{get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LuminaCompanion");}}
        public static string FilePath{get{return Path.Combine(DataDir,"settings.json");}}
        public static Settings Load()
        {
            Settings s=null;
            try{if(File.Exists(FilePath)){string text=File.ReadAllText(FilePath);s=Json.Serializer().Deserialize<Settings>(text);var old=Json.Parse(text);if(!old.ContainsKey("LayoutVersion")){s.LayoutVersion=1;if(s.ShowBubble&&!Double.IsNaN(s.Top))s.Top+=126;}}}catch{}
            if(s==null)s=new Settings();
            if(String.IsNullOrWhiteSpace(s.Home))s.Home=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            if(s.Pinned==null)s.Pinned="";s.Height=Math.Max(280,Math.Min(1200,Double.IsNaN(s.Height)?480:s.Height));return s;
        }
        public void Save(){if(!SaveEnabled)return;try{Json.Save(FilePath,this);}catch{}}
    }
    public static class Theme
    {
        public static Brush Ink=Brush("#303033"),Muted=Brush("#6B6B70"),Violet=Brush("#7558A3"),Paper=Brush("#FAFAFB"),Line=Brush("#DDDDDF"),Green=Brush("#28715A"),Amber=Brush("#9B651E"),Red=Brush("#A74754");
        public static Brush Brush(string s){var b=(SolidColorBrush)new BrushConverter().ConvertFromString(s);b.Freeze();return b;}
        public static TextBlock Text(string value,double size,Brush color){return new TextBlock{Text=value,FontSize=size,Foreground=color,FontFamily=new FontFamily("Microsoft YaHei UI"),TextWrapping=TextWrapping.Wrap};}
        public static Button Button(string label,Action action){var b=new Button{Content=label,MinHeight=30,Padding=new Thickness(10,4,10,4),Margin=new Thickness(0,4,8,4),Background=Brushes.White,Foreground=Ink,BorderBrush=Line,Cursor=System.Windows.Input.Cursors.Hand};AutomationProperties.SetName(b,label);b.Click+=(s,e)=>action();return b;}
    }
    public class SettingsWindow:Window
    {
        readonly PetWindow pet;TextBlock sizeText,connection;ComboBox sessions;bool populating;
        public SettingsWindow(PetWindow owner)
        {
            pet=owner;Title="露米娜 · 设置";Width=420;Height=530;MinWidth=360;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Theme.Paper;Foreground=Theme.Ink;FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=13;
            var panel=new StackPanel{Margin=new Thickness(22)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            panel.Children.Add(Theme.Text("设置",20,Theme.Ink));sizeText=Theme.Text("大小",13,Theme.Ink);sizeText.Margin=new Thickness(0,20,0,0);panel.Children.Add(sizeText);
            var slider=new Slider{Minimum=280,Maximum=1200,TickFrequency=20,IsSnapToTickEnabled=true,Value=pet.Config.Height,Margin=new Thickness(0,10,0,6),Height=26};AutomationProperties.SetName(slider,"角色大小");slider.ValueChanged+=(s,e)=>{pet.SetHeight(slider.Value);SizeLabel();};panel.Children.Add(slider);
            var presets=new WrapPanel{Margin=new Thickness(0,0,0,10)};foreach(int n in new[]{320,480,650,900,1200}){int height=n;presets.Children.Add(Theme.Button(n.ToString(),()=>slider.Value=height));}panel.Children.Add(presets);
            AddCheck(panel,"隐藏任务栏",!pet.Config.ShowBubble,v=>{pet.Config.ShowBubble=!v;pet.ApplyLayout();});
            AddCheck(panel,"始终置顶",pet.Config.Topmost,v=>{pet.Config.Topmost=v;pet.Topmost=v;});
            AddCheck(panel,"播放动画（30 FPS）",pet.Config.Animate,v=>pet.Config.Animate=v);
            AddCheck(panel,"空闲时随机动作",pet.Config.RandomActions,v=>pet.Config.RandomActions=v);
            AddCheck(panel,"高清静态立绘",pet.Config.Portrait,v=>pet.Config.Portrait=v);
            AddCheck(panel,"空闲时视线跟随鼠标",pet.Config.FollowMouse,v=>pet.Config.FollowMouse=v);
            var advanced=new StackPanel{Margin=new Thickness(0,10,0,10)};
            sessions=new ComboBox{MinHeight=32,Margin=new Thickness(0,0,0,8)};AutomationProperties.SetName(sessions,"绑定 Codex 会话");sessions.SelectionChanged+=(s,e)=>{if(populating)return;var item=sessions.SelectedItem as ComboBoxItem;if(item!=null){pet.Config.Pinned=Convert.ToString(item.Tag);pet.Monitor.Pinned=pet.Config.Pinned;pet.Config.Save();}};advanced.Children.Add(sessions);
            connection=Theme.Text("",11,Theme.Muted);advanced.Children.Add(connection);
            var row=new WrapPanel{Margin=new Thickness(0,6,0,0)};row.Children.Add(Theme.Button("选择数据目录",ChooseHome));row.Children.Add(Theme.Button("回到 Codex",pet.OpenCodex));advanced.Children.Add(row);
            panel.Children.Add(new Expander{Header="Codex 绑定",Content=advanced,Margin=new Thickness(0,14,0,8)});
            panel.Children.Add(Theme.Text("滚轮缩放 · 拖动移动 · 右键菜单",11,Theme.Muted));
            var footer=new WrapPanel{Margin=new Thickness(0,12,0,0)};footer.Children.Add(Theme.Button("重置位置",pet.ResetPosition));footer.Children.Add(Theme.Button("完成",Close));panel.Children.Add(footer);
            Closed+=(s,e)=>pet.Config.Save();SizeLabel();RefreshSessions();
        }
        void AddCheck(StackPanel panel,string label,bool value,Action<bool> action){var c=new CheckBox{Content=label,IsChecked=value,Margin=new Thickness(0,5,0,5),MinHeight=24,Foreground=Theme.Ink};AutomationProperties.SetName(c,label);c.Checked+=(s,e)=>{action(true);pet.Config.Save();};c.Unchecked+=(s,e)=>{action(false);pet.Config.Save();};panel.Children.Add(c);}
        void SizeLabel(){sizeText.Text="大小 · "+pet.Config.Height.ToString("0");}
        void ChooseHome(){using(var dialog=new Forms.FolderBrowserDialog{Description="选择 Codex 数据目录",SelectedPath=pet.Config.Home}){if(dialog.ShowDialog()==Forms.DialogResult.OK){pet.Config.Home=dialog.SelectedPath;pet.Monitor.Home=dialog.SelectedPath;pet.Config.Save();RefreshSessions();}}}
        public void RefreshSessions()
        {
            if(sessions.IsDropDownOpen)return;populating=true;sessions.Items.Clear();sessions.Items.Add(new ComboBoxItem{Content="自动跟随活跃会话",Tag=""});int selected=0;
            if(pet.Snapshot!=null)foreach(var a in pet.Snapshot.Sessions){sessions.Items.Add(new ComboBoxItem{Content=a.Title,Tag=a.Id});if(a.Id==pet.Config.Pinned)selected=sessions.Items.Count-1;}
            if(pet.Config.Pinned.Length>0&&selected==0){sessions.Items.Add(new ComboBoxItem{Content="已绑定的会话",Tag=pet.Config.Pinned});selected=sessions.Items.Count-1;}
            sessions.SelectedIndex=selected;connection.Text=pet.Config.Home;populating=false;SizeLabel();
        }
    }
}
