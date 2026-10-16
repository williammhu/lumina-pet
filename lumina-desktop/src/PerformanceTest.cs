using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace LuminaDesktop
{
    public static class PerformanceTest
    {
        public static int Run(string[] args)
        {
            string output=Program.Option(args,"--out")??Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qa-v4");Directory.CreateDirectory(output);
            var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};var config=Settings.Load();config.SaveEnabled=false;
            double requested;if(Double.TryParse(Program.Option(args,"--height"),out requested))config.Height=Math.Max(280,Math.Min(1200,requested));config.Portrait=false;config.Animate=true;config.RandomActions=false;config.FollowMouse=false;
            var pet=new PetWindow(config,false);app.MainWindow=pet;var samples=new List<Dictionary<string,object>>();var watch=new Stopwatch();TimeSpan cpuStart=Process.GetCurrentProcess().TotalProcessorTime;
            string[] states=pet.ActionStates;int phase=-1;
            pet.Loaded+=(s,e)=>{
                watch.Start();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
                timer.Tick+=(sender,ev)=>{
                    int current=Math.Min(states.Length-1,(int)(watch.Elapsed.TotalSeconds/2));
                    if(current!=phase){phase=current;pet.ExerciseState(states[phase],4);}
                    using(var p=Process.GetCurrentProcess()){samples.Add(new Dictionary<string,object>{{"seconds",Math.Round(watch.Elapsed.TotalSeconds,2)},{"state",pet.AnimationState},{"working_set_mb",Math.Round(p.WorkingSet64/1048576.0,1)},{"private_mb",Math.Round(p.PrivateMemorySize64/1048576.0,1)},{"fps",Math.Round(pet.MeasuredFps,2)},{"native_surface_ready",pet.NativeSurfaceReady}});}
                    if(watch.Elapsed.TotalSeconds>=states.Length*2+1){
                        timer.Stop();pet.WriteDiagnostic(Path.Combine(output,"performance-live.json"));pet.Capture(Path.Combine(output,"performance-ui.png"));
                        using(var p=Process.GetCurrentProcess()){
                            double cpu=(p.TotalProcessorTime-cpuStart).TotalSeconds/watch.Elapsed.TotalSeconds/Environment.ProcessorCount*100;
                            double max=samples.Max(x=>Convert.ToDouble(x["working_set_mb"])),fps=pet.MeasuredFps;
                            bool visible=samples.All(x=>Convert.ToBoolean(x["native_surface_ready"]));var exercised=samples.Select(x=>Convert.ToString(x["state"])).Distinct().ToArray();
                            Json.Save(Path.Combine(output,"performance.json"),new{ok=fps>=27&&max<160&&visible&&exercised.Length==states.Length,target_fps=30,measured_fps=Math.Round(fps,2),height_setting=config.Height,visible_character_height=pet.VisibleCharacterHeight,peak_sampled_working_set_mb=max,last_working_set_mb=samples.Last()["working_set_mb"],last_private_mb=samples.Last()["private_mb"],cpu_percent_of_machine=Math.Round(cpu,2),samples=samples.ToArray(),states_exercised=exercised,action_count=states.Length,animation_buffers=1,animation_frame_bytes=pet.FrameBufferBytes,compressed_frame_buffer_bytes=pet.CompressedBufferBytes,real_codex_monitor=true,native_surface_visible_throughout=visible});
                        }
                        pet.Close();
                    }
                };timer.Start();
            };
            app.Run(pet);return Json.Str(Json.Parse(File.ReadAllText(Path.Combine(output,"performance.json"))),"ok")=="True"?0:1;
        }
    }
}
