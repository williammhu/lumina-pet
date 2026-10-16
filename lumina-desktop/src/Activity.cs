using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace LuminaDesktop
{
    public static class Json
    {
        public static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=120};}
        public static Dictionary<string,object> Parse(string s){return Serializer().DeserializeObject(s) as Dictionary<string,object>;}
        public static Dictionary<string,object> Obj(object o){return o as Dictionary<string,object>??new Dictionary<string,object>();}
        public static object Get(Dictionary<string,object> o,string k){object v;return o.TryGetValue(k,out v)?v:null;}
        public static string Str(Dictionary<string,object> o,string k){object v=Get(o,k);return v==null?"":Convert.ToString(v);}
        public static object[] Arr(object o){return o as object[]??new object[0];}
        public static void Save(string path,object o){Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";File.WriteAllText(temp,Serializer().Serialize(o),new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
    }
    // Reads bounded scalar fields only. Never builds an object graph from a session log.
    public static class TinyJson
    {
        static readonly Dictionary<string,Regex> patterns=new Dictionary<string,Regex>();
        public static int ValueAt(string text,string key)
        {
            Regex pattern;lock(patterns){if(!patterns.TryGetValue(key,out pattern)){pattern=new Regex("(?<!\\\\)\""+Regex.Escape(key)+"\"\\s*:\\s*",RegexOptions.CultureInvariant);patterns[key]=pattern;}}
            var match=pattern.Match(text??"");return match.Success?match.Index+match.Length:-1;
        }
        public static string Object(string text,string key){int i=ValueAt(text,key);return i>=0&&i<text.Length&&text[i]=='{'?text.Substring(i+1):"";}
        public static string Get(string text,string key,int limit=4096)
        {
            int i=ValueAt(text,key);if(i<0||i>=text.Length||text[i]!='"')return "";
            var b=new StringBuilder(Math.Min(limit,128));
            for(i++;i<text.Length&&b.Length<limit;i++){
                char c=text[i];if(c=='"')break;
                if(c=='\\'&&i+1<text.Length){c=text[++i];switch(c){case 'n':c='\n';break;case 'r':c='\r';break;case 't':c='\t';break;case 'b':c='\b';break;case 'f':c='\f';break;case 'u':int code;if(i+4<text.Length&&Int32.TryParse(text.Substring(i+1,4),System.Globalization.NumberStyles.HexNumber,null,out code)){c=(char)code;i+=4;}break;}}
                b.Append(c);
            }
            return b.ToString();
        }
    }
    public class Activity
    {
        public string Id="",Project="",Title="",State="idle",Detail="",Source="",TurnId="";
        public bool Busy,Child,HasTurnStart;
        public DateTime Updated=DateTime.MinValue,Started=DateTime.MinValue;
        public Dictionary<string,string> Pending=new Dictionary<string,string>();
        public Activity Copy(){return (Activity)MemberwiseClone();}
    }
    public static class ActivityParser
    {
        public static string Clean(string text,int limit)
        {
            if(String.IsNullOrEmpty(text))return "";
            text=Regex.Replace(text,@"(?i)(?:sk-|ghp_|github_pat_|Bearer\s+)[A-Za-z0-9_\-\.]{8,}","[已隐藏]");
            text=Regex.Replace(text,@"(?i)(api[_-]?key|access[_-]?token|password|secret|authorization)\s*[:=]\s*[^\s,;]+","$1=[已隐藏]");
            text=Regex.Replace(text,@"https?://\S+","[链接]");text=Regex.Replace(text,@"[\x00-\x1f\x7f]+"," ").Trim();text=Regex.Replace(text,@"\s+"," ");return text.Length>limit?text.Substring(0,limit)+"…":text;
        }
        public static string ToolState(string name,string input)
        {
            // Lex JavaScript call names, skipping quoted patch content, including truncated strings.
            var names=new StringBuilder(name??"");string code=input??"";
            for(int i=0;i<code.Length;i++){
                char c=code[i];
                if(c=='"'||c=='\''||c=='`'){char quote=c;for(i++;i<code.Length;i++){if(code[i]=='\\'){i++;continue;}if(code[i]==quote)break;}continue;}
                if(c=='/'&&i+1<code.Length&&code[i+1]=='/'){while(i<code.Length&&code[i]!='\n')i++;continue;}
                if(c=='/'&&i+1<code.Length&&code[i+1]=='*'){i+=2;while(i+1<code.Length&&!(code[i]=='*'&&code[i+1]=='/'))i++;i++;continue;}
                if(i+6<code.Length&&code.Substring(i,6)=="tools."){int start=i+6,end=start;while(end<code.Length&&(Char.IsLetterOrDigit(code[end])||code[end]=='_'))end++;names.Append(' ').Append(code,start,end-start);i=end-1;}
            }
            string n=names.ToString().ToLowerInvariant();
            if(n.Contains("request_user_input")||n.Contains("request_permissions"))return "waiting";
            if(n.Contains("imagegen")||n.Contains("image_gen"))return "drawing";
            if(n.Contains("apply_patch"))return "editing";
            if(n.Contains("exec_command")||n.Contains("write_stdin")||n.EndsWith("shell"))return "command";
            if(n.Contains("web__run")||n.Contains("web.run")||n.Contains("search"))return "searching";
            if(n.Contains("view_image")||n.Contains("read_file"))return "reading";
            return "working";
        }
        public static string Label(string state)
        {
            switch(state){case "command":return "运行命令";case "editing":return "编辑文件";case "drawing":return "生成图像";case "searching":return "搜索资料";case "reading":return "读取文件";case "thinking":return "思考中";case "working":return "调用工具";case "waiting":return "等待确认";case "ready":return "已完成";case "failed":return "遇到问题";case "paused":return "已停止";case "stale":return "等待更新";case "offline":return "未连接";case "missing":return "未连接";default:return "空闲";}
        }
        public static void Apply(Activity a,string line){Apply(a,line,"");}
        public static void Apply(Activity a,string line,string suffix)
        {
            if(line.Length>16384){suffix=line.Substring(Math.Max(0,line.Length-4096));line=line.Substring(0,16384);}
            string type=TinyJson.Get(line,"type",40),p=TinyJson.Object(line,"payload"),pt=TinyJson.Get(p,"type",50);
            DateTime ts;if(!DateTime.TryParse(TinyJson.Get(line,"timestamp",64),null,System.Globalization.DateTimeStyles.AdjustToUniversal,out ts))ts=DateTime.UtcNow;ts=ts.ToUniversalTime();
            if(type=="session_meta"){
                a.Id=TinyJson.Get(p,"id",80);a.Project=TinyJson.Get(p,"cwd",1000);a.Source=TinyJson.Get(p,"originator",80);
                a.Child=TinyJson.Object(p,"source").Length>0&&Regex.IsMatch(p,@"""source""\s*:\s*\{\s*""subagent""");return;
            }
            if(type=="token_usage_record"){if(a.Updated==DateTime.MinValue&&DateTime.UtcNow-ts<TimeSpan.FromMinutes(2)){a.Busy=true;a.State="thinking";a.Updated=ts;}return;}
            if(type!="event_msg"&&type!="response_item")return;
            bool changed=false;
            if(type=="event_msg"){
                if(pt=="task_started"||pt=="turn_started"){a.Busy=true;a.HasTurnStart=true;a.Started=ts;a.State="thinking";a.Detail="";a.Pending.Clear();a.TurnId=TinyJson.Get(p,"turn_id",80);changed=true;}
                else if(pt=="task_complete"||pt=="turn_complete"||pt=="task_completed"){a.Busy=false;a.State="ready";a.Pending.Clear();changed=true;}
                else if(pt=="turn_aborted"||pt=="task_cancelled"){a.Busy=false;a.State="paused";a.Pending.Clear();changed=true;}
                else if(pt=="error"){a.Busy=false;a.State="failed";changed=true;}
                else if(pt=="item_completed"||pt=="item_started"){
                    string item=TinyJson.Object(p,"item"),it=TinyJson.Get(item,"type",40).ToLowerInvariant(),phase=TinyJson.Get(item,"phase",30);
                    if(phase.Length==0)phase=TinyJson.Get(suffix,"phase",30);
                    if(it=="agentmessage"&&phase=="commentary"){a.Detail=Clean(TinyJson.Get(item,"text",200),96);a.Busy=true;a.State="thinking";changed=true;}
                    else if(it=="agentmessage"&&phase=="final"){a.Busy=false;a.State="ready";a.Pending.Clear();changed=true;}
                    else if(it=="commandexecution"){a.Busy=true;a.State=pt=="item_started"||TinyJson.Get(item,"status",30)=="in_progress"?"command":"thinking";changed=true;}
                    else if(it=="filechange"){a.Busy=true;a.State="editing";changed=true;}
                    else if(it=="extension"){a.Busy=true;a.State="searching";changed=true;}
                }
            }else{
                if(pt=="function_call"||pt=="custom_tool_call"){
                    string state=ToolState(TinyJson.Get(p,"name",120),TinyJson.Get(p,pt=="function_call"?"arguments":"input",8192));
                    string id=TinyJson.Get(p,"call_id",120);if(a.Pending.Count>64)a.Pending.Clear();a.Pending[id]=state;a.Busy=true;a.State=state;changed=true;
                }else if(pt=="function_call_output"||pt=="custom_tool_call_output"){
                    string id=TinyJson.Get(p,"call_id",120),old;a.Pending.TryGetValue(id,out old);a.Pending.Remove(id);
                    if(a.Busy&&old!="waiting"){a.State=a.Pending.Count>0?a.Pending.Values.Last():"thinking";changed=true;}
                }else if(pt=="message"&&TinyJson.Get(p,"role",30)=="assistant"){
                    string phase=TinyJson.Get(p,"phase",30);if(phase.Length==0)phase=TinyJson.Get(suffix,"phase",30);
                    if(phase=="final"){a.Busy=false;a.State="ready";a.Pending.Clear();changed=true;}
                }
            }
            if(changed)a.Updated=ts;
        }
    }
    public class JsonTail
    {
        public string Path;public Activity Activity=new Activity();long offset;bool initialized;
        readonly byte[] prefix=new byte[16384],tail=new byte[4096],buffer=new byte[16384];
        int prefixCount,tailCount,tailIndex;bool overflow;
        public int BufferBytes{get{return prefix.Length+tail.Length+buffer.Length;}}
        public JsonTail(string path){Path=path;}
        void ResetLine(){prefixCount=tailCount=tailIndex=0;overflow=false;}
        void Finish()
        {
            if(prefixCount==0)return;string suffix="";
            if(overflow&&tailCount>0){byte[] ordered=new byte[tailCount];int start=tailCount==tail.Length?tailIndex:0;for(int j=0;j<tailCount;j++)ordered[j]=tail[(start+j)%tail.Length];suffix=Encoding.UTF8.GetString(ordered);}
            ActivityParser.Apply(Activity,Encoding.UTF8.GetString(prefix,0,prefixCount).TrimEnd('\r'),suffix);ResetLine();
        }
        public void Poll()
        {
            if(initialized&&Activity.Child)return;
            using(var stream=new FileStream(Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                if(stream.Length<offset){offset=0;initialized=false;ResetLine();Activity=new Activity();}
                if(!initialized){
                    int n=stream.Read(buffer,0,buffer.Length);ActivityParser.Apply(Activity,Encoding.UTF8.GetString(buffer,0,n));initialized=true;
                    if(Activity.Child){offset=stream.Length;return;}
                    stream.Position=Math.Max(0,stream.Length-256*1024);
                    if(stream.Position>0){int c;while((c=stream.ReadByte())>=0&&c!=10){}}
                    offset=stream.Position;
                }
                stream.Position=offset;int read,total=0;
                while(total<2*1024*1024&&(read=stream.Read(buffer,0,buffer.Length))>0){
                    offset+=read;total+=read;
                    for(int i=0;i<read;i++){
                        byte value=buffer[i];if(value==10){Finish();continue;}
                        if(prefixCount<prefix.Length)prefix[prefixCount++]=value;
                        else{overflow=true;tail[tailIndex]=value;tailIndex=(tailIndex+1)%tail.Length;if(tailCount<tail.Length)tailCount++;}
                    }
                }
            }
        }
    }
    public class MonitorSnapshot{public Activity Current;public List<Activity> Sessions=new List<Activity>();public int ActiveCount;public bool CodexRunning;public string Error="";}
    public class ActivityMonitor:IDisposable
    {
        public string Home,Pinned="";public event Action<MonitorSnapshot> Changed;
        readonly Dictionary<string,JsonTail> tails=new Dictionary<string,JsonTail>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> titles=new Dictionary<string,string>();
        List<FileInfo> files=new List<FileInfo>();DateTime discovery=DateTime.MinValue,indexTime=DateTime.MinValue;string discoveredHome="";Thread thread;volatile bool stop;
        public ActivityMonitor(string home){Home=home;}
        public void Start(){thread=new Thread(Run){IsBackground=true,Name="Codex bounded read-only monitor"};thread.Start();}
        static bool Running(){foreach(string name in new[]{"ChatGPT","Codex","codex"}){var list=Process.GetProcessesByName(name);bool any=list.Length>0;foreach(var p in list)p.Dispose();if(any)return true;}return false;}
        public MonitorSnapshot Scan()
        {
            var s=new MonitorSnapshot{CodexRunning=Running()};string root=System.IO.Path.Combine(Home,"sessions");
            if(!Directory.Exists(root)){s.Current=new Activity{State="missing"};return s;}
            if(Home!=discoveredHome||DateTime.UtcNow-discovery>TimeSpan.FromSeconds(4)){
                files=new DirectoryInfo(root).EnumerateFiles("*.jsonl",SearchOption.AllDirectories).OrderByDescending(f=>f.LastWriteTimeUtc).Take(24).ToList();
                if(Pinned.Length>0&&!files.Any(f=>f.Name.Contains(Pinned))){var found=new DirectoryInfo(root).EnumerateFiles("*"+Pinned+".jsonl",SearchOption.AllDirectories).FirstOrDefault();if(found!=null)files.Add(found);}
                discoveredHome=Home;discovery=DateTime.UtcNow;
            }
            string index=System.IO.Path.Combine(Home,"session_index.jsonl");
            try{if(File.Exists(index)&&File.GetLastWriteTimeUtc(index)!=indexTime){titles.Clear();foreach(string line in File.ReadLines(index,Encoding.UTF8)){string id=TinyJson.Get(line,"id",80),title=TinyJson.Get(line,"thread_name",80);if(id.Length>0)titles[id]=ActivityParser.Clean(title,40);}indexTime=File.GetLastWriteTimeUtc(index);}}catch(IOException){}
            var selected=files.Take(16).ToList();var pinned=files.FirstOrDefault(f=>Pinned.Length>0&&f.Name.Contains(Pinned));if(pinned!=null&&!selected.Contains(pinned))selected.Add(pinned);
            foreach(var f in selected){
                JsonTail journal;if(!tails.TryGetValue(f.FullName,out journal)){journal=new JsonTail(f.FullName);tails[f.FullName]=journal;}
                try{journal.Poll();}catch(IOException){continue;}catch(UnauthorizedAccessException){continue;}
                var a=journal.Activity.Copy();if(a.Child||a.Id.Length==0)continue;
                string title;if(titles.TryGetValue(a.Id,out title))a.Title=title;if(a.Title.Length==0)a.Title=System.IO.Path.GetFileName(a.Project.TrimEnd('\\','/'));if(a.Title.Length==0)a.Title="Codex";
                if(a.Updated==DateTime.MinValue)a.Updated=f.LastWriteTimeUtc;
                if(!s.CodexRunning){a.Busy=false;a.State="offline";}else if(DateTime.UtcNow-a.Updated>TimeSpan.FromMinutes(15)){a.Busy=false;a.State="idle";}else if(a.Busy&&DateTime.UtcNow-a.Updated>TimeSpan.FromSeconds(120))a.State="stale";
                s.Sessions.Add(a);
            }
            foreach(string key in tails.Keys.Where(k=>!selected.Any(f=>f.FullName.Equals(k,StringComparison.OrdinalIgnoreCase))).ToArray())tails.Remove(key);
            s.ActiveCount=s.Sessions.Count(a=>a.Busy);s.Current=s.Sessions.FirstOrDefault(a=>a.Id==Pinned);
            if(s.Current==null&&Pinned.Length>0)s.Current=new Activity{Id=Pinned,State="missing"};
            if(s.Current==null)s.Current=s.Sessions.OrderByDescending(a=>a.State=="waiting"&&a.Busy?3:a.Busy?2:1).ThenByDescending(a=>a.Updated).FirstOrDefault();
            if(s.Current==null)s.Current=new Activity{State=s.CodexRunning?"missing":"offline"};return s;
        }
        void Run(){while(!stop){try{var s=Scan();var h=Changed;if(h!=null)h(s);}catch(Exception e){var h=Changed;if(h!=null)h(new MonitorSnapshot{Current=new Activity{State="missing"},Error=e.GetType().Name});}for(int i=0;i<10&&!stop;i++)Thread.Sleep(100);}}
        public void Dispose(){stop=true;if(thread!=null)thread.Join(2000);}
    }
}
