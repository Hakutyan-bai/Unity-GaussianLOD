using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GaussianSplatLOD.Editor
{
    public sealed class AholoConversionJob : IDisposable
    {
        public readonly ConcurrentQueue<string> Logs=new ConcurrentQueue<string>();
        public readonly string Source,Output,Staging;
        public readonly Task Work;
        public double ElapsedSeconds => watch.Elapsed.TotalSeconds;
        public bool Canceled {get;private set;}
        public float Progress {get;private set;}
        readonly Stopwatch watch=Stopwatch.StartNew();
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly object gate=new object();
        Process process;
        public static string Pipeline(string input,string staging,int levels,int maxPoints,bool generate)
        {
            if(!generate)levels=1;
            if(levels<1||levels>8||maxPoints<1)throw new ArgumentOutOfRangeException("LOD Levels / Max Chunk Points");
            float[] precision={1,.5f,.25f,.05f,.01f,.005f,.0025f,.001f};
            var lods=new JArray();for(int i=0;i<levels;i++)lods.Add(new JObject{["precision"]=precision[i],["scaleBoost"]=i<3?1:1+(i-2)*.01f,["merged"]=i>=3,["permanent"]=i>=3});
            return new JObject{["version"]=1,["cpuWorkers"]=Math.Max(1,Math.Min(8,Environment.ProcessorCount/2)),["tasks"]=new JArray(
                new JObject{["id"]="read",["type"]="Read",["config"]=new JObject{["inputs"]=new JArray(input),["output"]="source"}},
                new JObject{["id"]="lod",["type"]="AutoChunkLod",["config"]=new JObject{["input"]="source",["output"]="lod",["type"]="ply",["maxChunkCounts"]=maxPoints,["levels"]=lods},["release"]=new JArray("source")},
                new JObject{["id"]="write",["type"]="Write",["config"]=new JObject{["input"]="lod",["output"]=staging},["release"]=new JArray("lod")})}.ToString();
        }
        public AholoConversionJob(string executable,string source,string output,int levels,int maxPoints,bool generate)
        {
            Source=Path.GetFullPath(source);Output=Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(!File.Exists(Source)||!Source.EndsWith(".ply",StringComparison.OrdinalIgnoreCase))throw new IOException("Source PLY 不存在。");
            if(!File.Exists(executable))throw new FileNotFoundException("未找到 Aholo 原生转换器。",executable);
            if(Source.StartsWith(Output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Source==Output)throw new IOException("输入文件不能位于输出目录内。");
            if(Directory.Exists(Output)&&Directory.GetFileSystemEntries(Output).Length>0)throw new IOException("输出目录非空。为保护已有数据，请选择新的或空的目录；插件不会自动清空。");
            string parent=Path.GetDirectoryName(Output);if(string.IsNullOrEmpty(parent)||!Directory.Exists(parent))throw new IOException("输出父目录不存在。");
            Staging=Output+".partial-"+Guid.NewGuid().ToString("N");
            string pipeline=Pipeline(Source,Staging,levels,maxPoints,generate);
            Work=Task.Run(()=>Run(Path.GetFullPath(executable),pipeline));
        }
        void Line(string text)
        {
            if(text==null)return;Logs.Enqueue(text);
            if(text.Contains("generate lod"))Progress=.15f;
            if(text.Contains("writing bundle"))Progress=.8f;
            if(text.StartsWith("[Task:Read"))Progress=.05f;
        }
        void Run(string executable,string pipeline)
        {
            string pipelinePath=Staging+".pipeline.json";
            try
            {
                cancel.Token.ThrowIfCancellationRequested();
                File.WriteAllText(pipelinePath,pipeline,new UTF8Encoding(false));
                var start=new ProcessStartInfo(executable){Arguments="\""+pipelinePath+"\"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Path.GetDirectoryName(Source)};
                using(var p=new Process{StartInfo=start})
                {
                    lock(gate){cancel.Token.ThrowIfCancellationRequested();process=p;p.OutputDataReceived+=(s,e)=>Line(e.Data);p.ErrorDataReceived+=(s,e)=>Line(e.Data);p.Start();}
                    p.BeginOutputReadLine();p.BeginErrorReadLine();p.WaitForExit();
                    lock(gate)process=null;
                    cancel.Token.ThrowIfCancellationRequested();
                    if(p.ExitCode!=0)throw new IOException("Aholo 转换失败，退出码: "+p.ExitCode+"；请查看日志。临时目录保留供检查。");
                }
                var loader=new GaussianLODLoader(Path.Combine(Staging,"lod-meta.json"));loader.ValidateAllRanges();
                cancel.Token.ThrowIfCancellationRequested();
                // Commit only validated, complete data. Never recursively remove an existing directory.
                if(!Directory.Exists(Output))Directory.Move(Staging,Output);
                else {
                    if(Directory.GetFileSystemEntries(Output).Length!=0)throw new IOException("转换期间输出目录已出现文件，停止提交以避免覆盖。");
                    foreach(var path in Directory.GetFiles(Staging))if(Path.GetFileName(path)!="lod-meta.json")File.Move(path,Path.Combine(Output,Path.GetFileName(path)));
                    File.Move(Path.Combine(Staging,"lod-meta.json"),Path.Combine(Output,"lod-meta.json"));Directory.Delete(Staging,false);
                }
                File.Copy(pipelinePath,Path.Combine(Output,"conversion-pipeline.json"),false);
                Progress=1;Line("完成："+Output);
            }
            catch(OperationCanceledException){Canceled=true;Line("已取消；原始 PLY 和已有输出保持不变。临时文件保留于 "+Staging);}
            finally{watch.Stop();lock(gate)process=null;}
        }
        public void Cancel()
        {
            cancel.Cancel();lock(gate){if(process!=null)try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}}
        }
        public void Dispose(){if(!Work.IsCompleted)Cancel();/* task owns process until all redirected streams drain */}
    }
}
