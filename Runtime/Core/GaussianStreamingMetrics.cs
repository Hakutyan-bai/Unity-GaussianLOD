using System;
using UnityEngine;

namespace GaussianSplatLOD
{
    [Serializable]
    public sealed class GaussianPriorityWeights
    {
        [Min(0)] public float visible=6, nearby=3, predicted=2, upgrade=2, cached=1, age=.5f;
        public float Score(bool inView,float proximity,float prediction,bool refinement,bool cache,double waitSeconds,long bytes,double estimatedMilliseconds)
        {
            float value=(inView?visible:0)+nearby*proximity+predicted*prediction+(refinement?upgrade:0)+(cache?cached:0);
            // An aging term outside the size penalty prevents starvation of large visible chunks.
            return value/(1+(float)Math.Max(0,estimatedMilliseconds)/100+bytes/(32f*1048576))+age*(float)Math.Min(60,Math.Max(0,waitSeconds));
        }
    }

    /// <summary>Application-frame measurements, not camera callback rate or an inferred GPU timer.</summary>
    public sealed class GaussianStreamingMetrics
    {
        public float FPS {get;private set;}
        public double ApplicationFrameMilliseconds {get;private set;}
        public double CPUFrameMilliseconds {get;private set;}=-1;
        public double CPUMainMilliseconds {get;private set;}=-1;
        public double CPURenderMilliseconds {get;private set;}=-1;
        public double GPUMilliseconds {get;private set;}=-1;
        public double SchedulingMilliseconds,UploadMilliseconds,IOReadMilliseconds,DecodeMilliseconds,QueueWaitMilliseconds;
        public long IOBytes,StagingBytes;
        public int FileTransfers;
        public int QueueCount,ReadingCount,DecodingCount,UploadingCount;
        public long CancelledRequests,DroppedResults,CacheRequests,CacheHits,PreloadRequests,PreloadHits;
        public float CacheHitRate=>CacheRequests==0?0:(float)CacheHits/CacheRequests;
        public float PreloadHitRate=>PreloadRequests==0?0:(float)PreloadHits/PreloadRequests;
        public string PerformanceReason="Warming up";
        public float DetailScale {get;private set;}=1;
        public float UploadScale {get;private set;}=1;
        public float PrefetchScale {get;private set;}=1;
        public int FarLODBias {get;private set;}
        readonly double[] loads=new double[256],sorted=new double[256];
        readonly FrameTiming[] timing=new FrameTiming[1];
        int samples,write,lastFrame=-1;double loadSum,lastAdjustment;
        public double MeanLoadMilliseconds=>samples==0?0:loadSum/samples;
        public double P95LoadMilliseconds {get;private set;}
        public void Loaded(double milliseconds)
        {
            if(samples==loads.Length)loadSum-=loads[write];else samples++;
            loads[write]=milliseconds;loadSum+=milliseconds;write=(write+1)%loads.Length;
        }
        public void SampleFrame(bool adaptive,float targetHz,double uploadBudget,long cpuBytes,long cpuLimit,long gpuBytes,long gpuLimit)
        {
            if(!Application.isPlaying||Time.frameCount==lastFrame)return;
            lastFrame=Time.frameCount;
            float fps=1/Mathf.Max(.000001f,Time.unscaledDeltaTime);FPS=FPS==0?fps:Mathf.Lerp(FPS,fps,.05f);
            ApplicationFrameMilliseconds=Time.unscaledDeltaTime*1000;
            CPUFrameMilliseconds=CPUMainMilliseconds=CPURenderMilliseconds=GPUMilliseconds=-1;
            FrameTimingManager.CaptureFrameTimings();
            if(FrameTimingManager.GetLatestTimings(1,timing)>0){var f=timing[0];CPUFrameMilliseconds=f.cpuFrameTime>0?f.cpuFrameTime:-1;CPUMainMilliseconds=f.cpuMainThreadFrameTime>0?f.cpuMainThreadFrameTime:-1;CPURenderMilliseconds=f.cpuRenderThreadFrameTime>0?f.cpuRenderThreadFrameTime:-1;GPUMilliseconds=f.gpuFrameTime>0?f.gpuFrameTime:-1;}
            double now=Time.realtimeSinceStartupAsDouble;if(now-lastAdjustment<.5)return;lastAdjustment=now;
            if(samples>0){Array.Copy(loads,sorted,samples);Array.Sort(sorted,0,samples);P95LoadMilliseconds=sorted[Math.Min(samples-1,(int)Math.Ceiling(samples*.95)-1)];}
            if(!adaptive){DetailScale=UploadScale=PrefetchScale=1;FarLODBias=0;PerformanceReason="Quality locked";return;}
            double target=1000/Math.Max(1,targetHz);
            bool memory=cpuBytes>cpuLimit*.9||gpuBytes>gpuLimit*.95;
            bool gpu=GPUMilliseconds>target*.9;
            bool upload=UploadMilliseconds>Math.Max(.1,uploadBudget)*1.15;
            bool cpu=CPUMainMilliseconds>target*.9||CPURenderMilliseconds>target*.9;
            bool backlog=QueueCount>ReadingCount+DecodingCount+8;
            if(gpu){DetailScale=Mathf.Max(.4f,DetailScale-.1f);FarLODBias=DetailScale<.7f?2:1;PerformanceReason="GPU frame over budget: distant detail reduced";}
            else if(!cpu&&!upload){DetailScale=Mathf.Min(1,DetailScale+.025f);if(DetailScale>.85f)FarLODBias=0;PerformanceReason=GPUMilliseconds<0?"GPU timer unavailable; CPU/IO scheduling only":"Measured frame headroom";}
            if(cpu||upload){UploadScale=Mathf.Max(.25f,UploadScale-.15f);PerformanceReason="CPU/upload submission over budget";}else UploadScale=Mathf.Min(1,UploadScale+.05f);
            if(memory||backlog){PrefetchScale=Mathf.Max(.25f,PrefetchScale-.15f);PerformanceReason=memory?"Cache pressure: reduce prefetch":"IO backlog: prioritize visible work";}else PrefetchScale=Mathf.Min(1,PrefetchScale+.05f);
        }
    }
}
