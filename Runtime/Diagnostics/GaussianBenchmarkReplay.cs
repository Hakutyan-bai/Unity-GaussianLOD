using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.XR;

namespace GaussianSplatLOD.Diagnostics
{
    /// <summary>Opt-in diagnostics and timestamp-based locomotion replay; never replaces head tracking.</summary>
    public sealed class GaussianBenchmarkReplay : MonoBehaviour
    {
        public GaussianLODRenderer model;
        public Camera viewingCamera;
        public Transform locomotionRoot;
        public bool runOnStart;
        public bool driveRoute=true;
        [Min(30)] public float durationSeconds=1200;
        [Range(1,60)] public float sampleFrequency=10;
        public string reportName="SUIT-device";
        public bool Running{get;private set;}
        public string LogPath{get;private set;}
        public string LogError=>writer?.Error;
        public int DroppedSamples=>writer==null?0:writer.Dropped;
        double started,nextSample;
        long frame;
        BufferedWriter writer;
        readonly GaussianMetricReader reader=new GaussianMetricReader();

        struct Sample
        {
            public double seconds,cpuMs,gpuMs,uploadMs,fps,loadMean,loadP95,cacheHit,preloadHit,ioMs,decodeMs,queueWaitMs;
            public int visible,covered,pending;public long cpu,gpu,unityAllocated,reads;public Vector3 position,headEuler;
            public string CSV()=>string.Join(",",new[]{seconds.ToString("F4",CultureInfo.InvariantCulture),fps.ToString("F3",CultureInfo.InvariantCulture),cpuMs.ToString("F4",CultureInfo.InvariantCulture),gpuMs.ToString("F4",CultureInfo.InvariantCulture),uploadMs.ToString("F4",CultureInfo.InvariantCulture),visible.ToString(),covered.ToString(),pending.ToString(),cpu.ToString(),gpu.ToString(),unityAllocated.ToString(),reads.ToString(),loadMean.ToString("F4",CultureInfo.InvariantCulture),loadP95.ToString("F4",CultureInfo.InvariantCulture),cacheHit.ToString("F4",CultureInfo.InvariantCulture),preloadHit.ToString("F4",CultureInfo.InvariantCulture),position.x.ToString("F3",CultureInfo.InvariantCulture),position.y.ToString("F3",CultureInfo.InvariantCulture),position.z.ToString("F3",CultureInfo.InvariantCulture),headEuler.x.ToString("F3",CultureInfo.InvariantCulture),headEuler.y.ToString("F3",CultureInfo.InvariantCulture),headEuler.z.ToString("F3",CultureInfo.InvariantCulture),ioMs.ToString("F4",CultureInfo.InvariantCulture),decodeMs.ToString("F4",CultureInfo.InvariantCulture),queueWaitMs.ToString("F4",CultureInfo.InvariantCulture)});
        }
        sealed class BufferedWriter
        {
            readonly ConcurrentQueue<Sample> queue=new ConcurrentQueue<Sample>();
            readonly AutoResetEvent signal=new AutoResetEvent(false);
            volatile bool stopped;int queued;public int Dropped;public string Error;readonly Task completion;
            public BufferedWriter(string path,string metadata)
            {
                completion=Task.Run(()=>{try{Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path+".metadata.json",metadata);using var file=new StreamWriter(path);file.WriteLine("seconds,fps,cpuFrameMs,gpuFrameMs,uploadSubmitMs,visible,covered,pending,cpuCacheBytes,gpuEstimatedBytes,unityAllocatedBytes,diskReads,meanLoadMs,p95LoadMs,cacheHit,preloadHit,headX,headY,headZ,pitch,yaw,roll,ioCumulativeMs,decodeCumulativeMs,lastQueueWaitMs");while(!stopped||!queue.IsEmpty){while(queue.TryDequeue(out var sample)){Interlocked.Decrement(ref queued);file.WriteLine(sample.CSV());}file.Flush();if(!stopped)signal.WaitOne(250);}}catch(Exception ex){Error=ex.ToString();}finally{signal.Dispose();}});
            }
            public void Add(Sample sample){if(stopped)return;if(Interlocked.Increment(ref queued)>2048){Interlocked.Decrement(ref queued);Interlocked.Increment(ref Dropped);return;}queue.Enqueue(sample);}
            public void Stop(){stopped=true;try{signal.Set();}catch(ObjectDisposedException){}}
            public void FlushBeforeQuit(){Stop();completion.Wait(1000);}
        }
        void Start(){if(runOnStart)Begin();}
        [ContextMenu("Begin Replay / Recording")]
        public void Begin()
        {
            if(!Application.isPlaying||Running||model==null)return;
            if(viewingCamera==null)viewingCamera=Camera.main;if(viewingCamera==null)return;
            string filename=string.Concat(reportName.Split(Path.GetInvalidFileNameChars()));
            LogPath=Path.Combine(Application.persistentDataPath,"GaussianSplatLODReports",filename+"-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".csv");
            string metadata=Newtonsoft.Json.JsonConvert.SerializeObject(new{pathVersion=GaussianReplayPath.Version,dataset=model.jsonPath,unity=Application.unityVersion,device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),xr=XRSettings.enabled,duration=durationSeconds,targetHz=90,sourcePoints=model.Metadata?.counts??0,modelMatrix=model.ModelMatrix.ToString(),cache=model.preferRuntimeCache,visibleLimit=model.maxVisibleSplats,cpuMiB=model.maxCPUCache,gpuMiB=model.maxGPUMemory,note="GPU unavailable=-1. GPU bytes estimated; Unity allocated is not Android process PSS. Actual head pose is recorded; XR head tracking is never overwritten. OS cache is not flushed."},Newtonsoft.Json.Formatting.Indented);
            writer=new BufferedWriter(LogPath,metadata);started=nextSample=Time.realtimeSinceStartupAsDouble;frame=0;Running=true;
        }
        void Update()
        {
            if(!Running||model==null||viewingCamera==null)return;
            double now=Time.realtimeSinceStartupAsDouble,elapsed=now-started;
            if(elapsed>=durationSeconds){Stop();return;}
            if(driveRoute&&model.Metadata!=null)
            {
                var pose=GaussianReplayPath.Evaluate(elapsed,model.ModelMatrix,model.Metadata.forwardBox.Bounds);
                if(locomotionRoot!=null&&locomotionRoot!=viewingCamera.transform){locomotionRoot.position=pose.position;locomotionRoot.rotation=Quaternion.Euler(0,pose.rotation.eulerAngles.y,0);}
                else if(!XRSettings.enabled)viewingCamera.transform.SetPositionAndRotation(pose.position,pose.rotation);
            }
            frame++;if(now<nextSample)return;nextSample=now+1.0/Mathf.Max(1,sampleFrequency);
            reader.Refresh(model);writer.Add(new Sample{seconds=elapsed,fps=reader.Number("FPS"),cpuMs=reader.Number("CPUFrameMilliseconds"),gpuMs=reader.Number("GPUMilliseconds"),uploadMs=reader.Number("UploadMilliseconds"),visible=model.VisibleSplats,covered=model.CoveredNodes,pending=model.PendingLoads,cpu=model.CPUBytes,gpu=model.GPUBytes,unityAllocated=Profiler.GetTotalAllocatedMemoryLong(),reads=model.DiskReads,loadMean=reader.Number("MeanLoadMilliseconds"),loadP95=reader.Number("P95LoadMilliseconds"),cacheHit=reader.Number("CacheHitRate"),preloadHit=reader.Number("PreloadHitRate"),position=viewingCamera.transform.position,headEuler=viewingCamera.transform.eulerAngles,ioMs=reader.Number("IOReadMilliseconds"),decodeMs=reader.Number("DecodeMilliseconds"),queueWaitMs=reader.Number("QueueWaitMilliseconds")});
        }
        [ContextMenu("Stop Replay / Recording")]
        public void Stop(){Running=false;writer?.Stop();}
        void OnDisable()=>Stop();
        void OnApplicationQuit(){Running=false;writer?.FlushBeforeQuit();}
    }
}
