using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GaussianSplatLOD.Editor
{
    /// <summary>Isolated, timestamp-based SUIT replay. Does not save or replace user scenes.</summary>
    [InitializeOnLoad]
    public static class GaussianStreamingBenchmark
    {
        public const string Dataset = "Assets/StreamingAssets/GaussianSplatLOD/SUIT-20261027/lod-meta.json";
        const int Width=1280,Height=720;
        static Scene scene;
        static Camera camera;
        static GaussianLODRenderer model;
        static RenderTexture texture;
        static RenderPipelineAsset previousPipeline,previousQuality;
        static readonly List<GaussianLODRenderer> suspended=new List<GaussianLODRenderer>();
        static readonly List<Sample> samples=new List<Sample>(10000);
        static readonly List<object> summaries=new List<object>();
        static readonly FrameTiming[] timings=new FrameTiming[1];
        static readonly Process benchmarkProcess=Process.GetCurrentProcess();
        static double nextMemorySample;
        static long processPrivateBytes,graphicsDriverBytes;
        static string label,reportPath,fingerprint;
        static bool useCache,savedPipeline;
        static int trial,totalTrials;
        static double duration,began,firstCoverage,lastFrame;
        static double lastProgress;
        static bool aborted;
        public static bool Running => model!=null;
        public static string Status {get;private set;}="Idle";

        [Serializable] public struct Sample
        {
            public double seconds,frameMs,tickMs,renderSubmitMs,cpuFrameMs,gpuFrameMs,uploadMs;
            public int visible,covered,pending,resident,transitions;public bool editorFocused;
            public long cpuBytes,gpuBytes,processBytes,privateBytes,graphicsDriverBytes,reads;
        }

        static GaussianStreamingBenchmark(){EditorApplication.update+=Tick;AssemblyReloadEvents.beforeAssemblyReload+=Shutdown;EditorApplication.quitting+=Shutdown;}

        [MenuItem("Tools/Gaussian Splat LOD/Performance/Run SUIT PLY Replay (3 x 30 seconds)")]
        public static void RunPLY()=>Begin("optimized-ply",false,30,3);
        [MenuItem("Tools/Gaussian Splat LOD/Performance/Run SUIT Cache Replay (3 x 30 seconds)")]
        public static void RunCache()=>Begin("optimized-cache",true,30,3);
        [MenuItem("Tools/Gaussian Splat LOD/Performance/Run SUIT 20 Minute Soak")]
        public static void RunSoak()=>Begin("soak-cache",true,1200,1);
        [MenuItem("Tools/Gaussian Splat LOD/Performance/Cancel Replay")]
        public static void Cancel(){aborted=true;Finish("cancelled");}

        public static string Begin(string runLabel,bool runtimeCache=false,double seconds=30,int trials=3)
        {
            if(Running)throw new InvalidOperationException("A benchmark is already running.");
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("Exit Play Mode before running the isolated Editor replay.");
            if(!File.Exists(Dataset))throw new FileNotFoundException(Dataset);
            label=runLabel;useCache=runtimeCache;duration=Math.Max(10,seconds);totalTrials=Math.Max(1,trials);trial=0;aborted=false;summaries.Clear();
            Directory.CreateDirectory("Reports/PICO");reportPath="Reports/PICO/"+label+".json";
            using(var hash=SHA256.Create())fingerprint=BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(Dataset))).Replace("-","").ToLowerInvariant();
            File.WriteAllText(reportPath,JsonConvert.SerializeObject(new{status="running",label,trials=totalTrials,seconds=duration,dataset=Dataset,metadataSHA256=fingerprint},Formatting.Indented));
            previousPipeline=GraphicsSettings.defaultRenderPipeline;previousQuality=QualitySettings.renderPipeline;savedPipeline=true;
            foreach(var other in UnityEngine.Object.FindObjectsOfType<GaussianLODRenderer>())if(other.enabled){suspended.Add(other);other.enabled=false;}
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/GaussianSplatLOD/URP14.asset");
            if(pipeline==null){Cleanup();throw new InvalidOperationException("URP14 validation asset is missing.");}
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=null;
            scene=EditorSceneManager.NewPreviewScene();
            var go=new GameObject("SUIT Benchmark Camera");SceneManager.MoveGameObjectToScene(go,scene);go.layer=30;
            camera=go.AddComponent<Camera>();camera.enabled=false;camera.cullingMask=1<<30;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.05f);camera.nearClipPlane=.3f;camera.farClipPlane=5000;
            texture=new RenderTexture(Width,Height,24,RenderTextureFormat.ARGB32){hideFlags=HideFlags.HideAndDontSave};texture.Create();camera.targetTexture=texture;
            go=new GameObject("SUIT Benchmark Model");SceneManager.MoveGameObjectToScene(go,scene);go.layer=30;
            model=go.AddComponent<GaussianLODRenderer>();model.Unload();model.loadOnEnable=false;model.dataLocation=GaussianDataLocation.ExternalDirectory;model.jsonPath=Path.GetFullPath(Dataset);model.targetCamera=camera;
            // Match the existing campus handedness correction. Never alter source PLY or user scenes.
            model.transform.rotation=Quaternion.Euler(180,0,0);model.transform.localScale=new Vector3(1,1,-1);
            model.maxVisibleSplats=1000000;model.maxGPUMemory=640;model.maxCPUCache=384;model.preloadDistance=20;model.uploadSplatsPerFrame=16384;model.maxConcurrentLoads=2;model.loadChunksPerFrame=2;
            model.showStatistics=model.showLoadingStatus=false;model.preferRuntimeCache=useCache;
            // Quality-locked A/B. New fields are optional so this harness also runs original code.
            SetOptional(model,"enablePerformanceScheduling",false);SetOptional(model,"maxResidentSplats",2000000);
            BeginTrial();return reportPath;
        }

        static void SetOptional(object target,string name,object value){var field=target.GetType().GetField(name);if(field!=null)field.SetValue(target,value);}
        static double OptionalMetric(string name,double missing=-1)
        {
            var p=model.GetType().GetProperty("Metrics");var metrics=p==null?null:p.GetValue(model);
            if(metrics==null)return missing;var type=metrics.GetType();var f=type.GetField(name);var property=type.GetProperty(name);
            object value=f!=null?f.GetValue(metrics):property!=null?property.GetValue(metrics):null;
            return value==null?missing:Convert.ToDouble(value,CultureInfo.InvariantCulture);
        }
        static void BeginTrial(){nextMemorySample=0;samples.Clear();firstCoverage=-1;began=lastFrame=lastProgress=EditorApplication.timeSinceStartup;model.Load();Status=label+" trial "+(trial+1)+"/"+totalTrials;}

        static void Tick()
        {
            if(!Running||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            try{
                // DX12 can retain upload scratch allocations when offscreen rendering is driven from
                // EditorApplication.update while the Editor is unfocused (Unity UUM-86354).
                // Stop instead of pausing: a wall-clock replay must never silently skip its route.
                if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Direct3D12&&!UnityEditorInternal.InternalEditorUtility.isApplicationActive)
                    throw new InvalidOperationException("DX12 Editor replay stopped because the Editor lost focus. Background offscreen rendering can exhaust native upload scratch memory (UUM-86354). Run this isolated validation with a -force-d3d11 Editor session, or keep the DX12 Editor focused. Player graphics settings are unchanged.");
                double now=EditorApplication.timeSinceStartup,elapsed=now-began;
                if(now-lastFrame<1.0/90)return;
                double frameMs=(now-lastFrame)*1000;lastFrame=now;
                if(model.Metadata!=null)ApplyPose(elapsed%30,model.ModelMatrix,model.Metadata.forwardBox.Bounds,camera.transform);
                var sw=Stopwatch.StartNew();model.Tick(camera);double tickMs=sw.Elapsed.TotalMilliseconds;sw.Restart();
                RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=texture});double renderMs=sw.Elapsed.TotalMilliseconds;
                if(model.CoverageReady&&firstCoverage<0)firstCoverage=EditorApplication.timeSinceStartup-began;
                FrameTimingManager.CaptureFrameTimings();uint count=FrameTimingManager.GetLatestTimings(1,timings);CaptureMemory(now);
                samples.Add(new Sample{editorFocused=UnityEditorInternal.InternalEditorUtility.isApplicationActive,seconds=elapsed,frameMs=frameMs,tickMs=tickMs,renderSubmitMs=renderMs,cpuFrameMs=count>0?timings[0].cpuFrameTime:-1,gpuFrameMs=count>0&&timings[0].gpuFrameTime>0?timings[0].gpuFrameTime:-1,uploadMs=OptionalMetric("UploadMilliseconds"),visible=model.VisibleSplats,covered=model.CoveredNodes,pending=model.PendingLoads,resident=model.ResidentRanges,transitions=model.TransitioningNodes,cpuBytes=model.CPUBytes,gpuBytes=model.GPUBytes,processBytes=Profiler.GetTotalAllocatedMemoryLong(),privateBytes=processPrivateBytes,graphicsDriverBytes=graphicsDriverBytes,reads=model.DiskReads});
                if(now-lastProgress>=60){lastProgress=now;File.WriteAllText(Path.ChangeExtension(reportPath,"progress.json"),JsonConvert.SerializeObject(new{status="running",trial=trial+1,seconds=elapsed,sampleCount=samples.Count,firstCoverageSeconds=firstCoverage,covered=model.CoveredNodes,cpuBytes=model.CPUBytes,gpuBytes=model.GPUBytes,unityAllocatedBytes=Profiler.GetTotalAllocatedMemoryLong(),processPrivateBytes,graphicsDriverBytes,reads=model.DiskReads},Formatting.Indented));}
                if(!string.IsNullOrEmpty(model.LastError))throw new InvalidOperationException(model.LastError);
                if(elapsed<duration)return;
                CompleteTrial();
            }catch(Exception ex){Status="Failed: "+ex.Message;Finish("failed",ex.ToString());}
        }

        /// <summary>Deterministic 30s route; timestamp-based, not frame-count-based.</summary>
        public static void ApplyPose(double seconds,Matrix4x4 matrix,Bounds bounds,Transform target)
        {
            Vector3 center=matrix.MultiplyPoint3x4(bounds.center);float radius=Mathf.Max(20,bounds.extents.magnitude);
            Vector3 wide=center+new Vector3(.15f,.65f,-1)*radius*1.5f;
            Vector3 near=center+new Vector3(-.42f,0,-.12f)*radius;near.y=Mathf.Max(60,center.y+80);
            Vector3 other=center+new Vector3(.45f,0,.16f)*radius;other.y=near.y;
            Vector3 position,look=center;
            if(seconds<5)position=wide;
            else if(seconds<12)position=Vector3.Lerp(wide,near,(float)((seconds-5)/7));
            else if(seconds<18){position=near;look=center+Quaternion.Euler(0,Mathf.Sin((float)(seconds-12)*2.5f)*70,0)*new Vector3(0,0,radius*.3f);}
            else if(seconds<24)position=Vector3.Lerp(near,wide,(float)((seconds-18)/6));
            else if(seconds<27)position=other; // Explicit teleport to test invalidation and reprioritisation.
            else position=Vector3.Lerp(other,wide,(float)((seconds-27)/3));
            target.SetPositionAndRotation(position,Quaternion.LookRotation(look-position,Vector3.up));
        }

        static void CompleteTrial()
        {
            long baseline=model.Metadata.tree.Sum(n=>(long)n.lods[model.Metadata.levels-1].count);
            if(samples.Count==0||samples.Max(s=>s.visible)<=baseline)throw new InvalidOperationException("Replay never refined beyond baseline; reject apparent speedup from reduced quality.");
            var measured=samples.Where(s=>s.seconds>=5).ToArray();var frames=measured.Select(s=>s.frameMs).OrderBy(x=>x).ToArray();
            int minCoverage=samples.Where(s=>s.seconds>=Math.Max(0,firstCoverage)).Select(s=>s.covered).DefaultIfEmpty(0).Min();
            int rendered=samples.Count(s=>s.visible>0);
            summaries.Add(new{trial=trial+1,firstCoverageSeconds=firstCoverage,renderedFrames=rendered,minCoveredNodes=minCoverage,frameCount=samples.Count,meanEditorIterationMs=frames.Length==0?0:frames.Average(),p95EditorIterationMs=frames.Length==0?0:frames[(int)Math.Min(frames.Length-1,Math.Ceiling(frames.Length*.95)-1)],maxCPUBytes=samples.Max(s=>s.cpuBytes),maxGPUBytes=samples.Max(s=>s.gpuBytes),maxProcessPrivateBytes=samples.Max(s=>s.privateBytes),maxGraphicsDriverBytes=samples.Max(s=>s.graphicsDriverBytes),diskReads=model.DiskReads,cacheReads=model.RuntimeCacheReads,plyReads=model.PLYReads});
            var output=samples.ToArray();int number=trial+1;string basePath=Path.ChangeExtension(reportPath,null)+"-trial"+number;
            // Serialize off the measurement path. This is cache/report output, never an asset edit.
            Task.Run(()=>{File.WriteAllText(basePath+".samples.json",JsonConvert.SerializeObject(output));WriteCSV(basePath+".csv",output);});
            if(firstCoverage<0||rendered==0||minCoverage!=model.Metadata.tree.Length)throw new InvalidOperationException("Replay did not retain complete coarse coverage.");
            trial++;if(trial>=totalTrials){Finish("passed");return;}BeginTrial();
        }
        static void WriteCSV(string path,Sample[] data)
        {
            using var writer=new StreamWriter(path,false,Encoding.UTF8);writer.WriteLine("seconds,editorIterationMs,tickMs,renderSubmitMs,cpuFrameMs,gpuFrameMs,uploadSubmitMs,visible,covered,pending,resident,transitions,cpuBytes,gpuBytes,unityAllocatedBytes,processPrivateBytes,graphicsDriverBytes,reads");
            foreach(var s in data)writer.WriteLine(string.Join(",",new object[]{s.seconds,s.frameMs,s.tickMs,s.renderSubmitMs,s.cpuFrameMs,s.gpuFrameMs,s.uploadMs,s.visible,s.covered,s.pending,s.resident,s.transitions,s.cpuBytes,s.gpuBytes,s.processBytes,s.privateBytes,s.graphicsDriverBytes,s.reads}.Select(v=>Convert.ToString(v,CultureInfo.InvariantCulture))));
        }
        static void Finish(string status,string error=null)
        {
            if(string.IsNullOrEmpty(reportPath))return;
            var report=new{status,label,dataset=Dataset,metadataSHA256=fingerprint,unity=Application.unityVersion,device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),pipeline="URP14",width=Width,height=Height,visibleLimit=1000000,gpuBudgetMiB=640,cpuBudgetMiB=384,uploadSplatsPerFrame=16384,cache=useCache,trialSeconds=duration,cancelled=aborted,notes="Windows Editor isolated preview replay, not device FPS. Editor iteration includes scheduling/render submission and Editor overhead. OS filesystem cache is not flushed. GPU frame time unavailable is -1. No PICO measurement. Source point count excludes other LODs. GPUBytes is the plugin buffer estimate, not total driver allocation. Process private bytes (Windows native fallback; -1 if unavailable) and Unity graphics-driver counters are sampled every 0.5 seconds and can miss transient peaks; neither is a complete GPU memory census.",trials=summaries,error};
            File.WriteAllText(reportPath,JsonConvert.SerializeObject(report,Formatting.Indented));Status=status;Cleanup();
        }
        static void CaptureMemory(double now)
        {
            if(now<nextMemorySample)return;
            nextMemorySample=now+.5;
            processPrivateBytes=ReadProcessPrivateBytes();
            graphicsDriverBytes=Profiler.GetAllocatedMemoryForGraphicsDriver();
        }
        // Mono's Process.PrivateMemorySize64 can report zero in the Windows Unity Editor.
        // The Windows process counter includes native allocations missed by Unity's managed counters.
        static long ReadProcessPrivateBytes()
        {
            try{benchmarkProcess.Refresh();long value=benchmarkProcess.PrivateMemorySize64;if(value>0)return value;}
            catch(Exception){ }
#if UNITY_EDITOR_WIN
            try{
                var counters=new ProcessMemoryCountersEx();counters.cb=(uint)Marshal.SizeOf<ProcessMemoryCountersEx>();
                if(GetProcessMemoryInfo(GetCurrentProcess(),ref counters,counters.cb)){
                    ulong value=counters.PrivateUsage.ToUInt64();if(value>0&&value<=long.MaxValue)return (long)value;
                }
            }catch(Exception){ }
#endif
            return -1; // Unsupported/unavailable is distinct from an empty process.
        }
#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        struct ProcessMemoryCountersEx
        {
            public uint cb,PageFaultCount;
            public UIntPtr PeakWorkingSetSize,WorkingSetSize,QuotaPeakPagedPoolUsage,QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage,QuotaNonPagedPoolUsage,PagefileUsage,PeakPagefileUsage,PrivateUsage;
        }
        [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll",EntryPoint="K32GetProcessMemoryInfo",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool GetProcessMemoryInfo(IntPtr process,ref ProcessMemoryCountersEx counters,uint size);
#endif
        static void Shutdown(){Cleanup();benchmarkProcess.Dispose();}
        static void Cleanup()
        {
            if(model!=null)model.Unload();model=null;camera=null;
            if(scene.IsValid())EditorSceneManager.ClosePreviewScene(scene);
            if(texture!=null){texture.Release();UnityEngine.Object.DestroyImmediate(texture);texture=null;}
            if(savedPipeline){GraphicsSettings.defaultRenderPipeline=previousPipeline;QualitySettings.renderPipeline=previousQuality;savedPipeline=false;}
            foreach(var other in suspended)if(other!=null)other.enabled=true;suspended.Clear();
        }
    }
}
