using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GaussianSplatLOD
{
    internal sealed class GaussianStreamingSession : IDisposable
    {
        internal enum GaussianRequestStage { Idle,Queued,Loading,Uploading,Resident,Retired }
        internal sealed class Entry
        {
            public GaussianRangeKey Key;
            public bool Permanent, Baseline, Wanted, Active,Queued,Cancelled,Prefetched,PrefetchConsumed;
            public CancellationTokenSource Cancellation;
            public double QueuedSince,RequestedAt,Priority,LastLoadMilliseconds;
            public long Reservation;
            public int Generation,Tier;
            public double DeferPrefetchUntil;
            public GaussianRequestStage Stage;
            public GaussianSplat[] CPU;
            public Task<GaussianSplat[]> Job;
            public int Slot=-1, Uploaded;
            public double Used, RetryAt;
            public int Failures;
            public bool Ready => Slot>=0 && Uploaded==Key.Count;
            public long Bytes => Key.Count*(long)GaussianSplat.Stride;
        }
        internal struct Segment
        {
            public int Slot,Count,Level,FadeIndex;
            public uint PackedLevel => (uint)Level | ((uint)FadeIndex<<8);
        }
        readonly GaussianLODRenderer owner;
        public readonly GaussianLODLoader Loader;
        readonly List<Entry> entries=new List<Entry>();
        readonly Dictionary<GaussianRangeKey,Entry> mapping=new Dictionary<GaussianRangeKey,Entry>();
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly List<Entry> queue=new List<Entry>();
        readonly List<RetiredSlot> retired=new List<RetiredSlot>();
        struct RetiredSlot {public int Slot,Count;public GaussianGPUResources.GaussianFence Fence;}
        GaussianViewContext selectionViews;
        readonly Bounds[] worldBounds;
        readonly bool[] predicted;
        readonly float[] distances,nodePriorities;
        Matrix4x4 boundsMatrix;float boundsScale=-1;bool boundsKnown;
        Vector3 velocity,lastRigPosition,predictedPosition;
        Quaternion lastHeadRotation;
        bool motionKnown,teleported;
        double motionTime;
        readonly System.Diagnostics.Stopwatch uploadWatch=new System.Diagnostics.Stopwatch();
        int uploadSubmissions,uploadEntries,lastUploadFrame=-1;
        readonly HashSet<Entry> uploadedThisFrame=new HashSet<Entry>();
        GaussianChunkBVH index;
        readonly List<int> visibleCandidates=new List<int>();
        readonly List<int> ordered=new List<int>();
        readonly List<Segment> segments=new List<Segment>();
        readonly int[] desired, target, current, previous;
        readonly double[] targetSince, transitionStart;
        readonly float[] weights;
        public readonly float[] Fades;
        readonly bool[] visible;
        public GaussianGPUResources GPU {get;private set;}
        public long CPUBytes {get;private set;}
        public int InFlight {get;private set;}
        public long DiskReads {get;private set;}
        public long CacheHits {get;private set;}
        public int ResidentRanges {get {int n=0;foreach(var e in entries)if(e.Ready)n++;return n;}}
        public int[] CurrentLevels => current;
        public bool CoverageReady {get;private set;}
        public int CoveredNodes {get {int n=0;foreach(int l in current)if(l>=0)n++;return n;}}
        public int TransitioningNodes {get {int n=0;foreach(int l in previous)if(l>=0)n++;return n;}}
        public int EffectivePointLimit => GPU==null?0:Math.Min(GPU.VisibleCapacity,Math.Max((int)baselinePoints,(int)Math.Min(int.MaxValue,(GPU.Capacity-permanentPoints)*.65+baselinePoints)));
        long permanentPoints,permanentBytes,baselinePoints;
        readonly List<Entry> baselineEntries=new List<Entry>();
        int transitionCursor;
        int configuredBudget,configuredVisible,configuredResident,generation;
        double lastSelection=-1;
        int configuredIO=-1,configuredDecodes=-1;
        public bool WaitingForGPU {get;private set;}
        GaussianGPUResources.GaussianFence rebuildFence;
        bool disposed;
        public GaussianStreamingSession(GaussianLODRenderer renderer,GaussianLODLoader loader)
        {
            owner=renderer;Loader=loader;
            var meta=loader.Metadata;
            loader.ConfigureConcurrency(renderer.maxConcurrentIO,renderer.maxConcurrentDecodes);
            worldBounds=new Bounds[meta.tree.Length];predicted=new bool[meta.tree.Length];distances=new float[meta.tree.Length];nodePriorities=new float[meta.tree.Length];
            desired=new int[meta.tree.Length];target=new int[meta.tree.Length];current=new int[meta.tree.Length];previous=new int[meta.tree.Length];targetSince=new double[meta.tree.Length];transitionStart=new double[meta.tree.Length];weights=new float[meta.tree.Length];visible=new bool[meta.tree.Length];Fades=new float[meta.tree.Length*2];
            for(int n=0;n<current.Length;n++){desired[n]=target[n]=current[n]=previous[n]=-1;}
            var pinned=new HashSet<int>(meta.permanentFiles);
            var baselineKeys=new HashSet<GaussianRangeKey>();
            foreach(var node in meta.tree)if(node.lods[meta.levels-1].count>0)baselineKeys.Add(node.lods[meta.levels-1].Key);
            var unique=new HashSet<GaussianRangeKey>();
            foreach(var node in meta.tree) foreach(var r in node.lods)if(r.count>0)unique.Add(r.Key);
            var keys=new List<GaussianRangeKey>(unique);
            keys.Sort((a,b)=>a.File!=b.File?a.File.CompareTo(b.File):a.Offset.CompareTo(b.Offset));
            // Merge shared intervals and contiguous permanent/baseline file ranges: two merged coarse files
            // become two disk jobs, rather than >100 small reads competing with detail requests.
            int i=0;
            while(i<keys.Count)
            {
                int end=i+1;var first=keys[i];long high=first.Offset+first.Count;
                bool pinnedFile=pinned.Contains(first.File);
                while(end<keys.Count&&keys[end].File==first.File&&
                    (keys[end].Offset<high || (keys[end].Offset==high && (pinnedFile || (baselineKeys.Contains(first)&&baselineKeys.Contains(keys[end]))))))
                {high=Math.Max(high,keys[end].Offset+keys[end].Count);end++;}
                if(high-first.Offset>int.MaxValue)throw new InvalidDataException("共享 PLY 区间超过单缓冲可寻址数量。");
                var e=new Entry{Key=new GaussianRangeKey(first.File,first.Offset,(int)(high-first.Offset)),Permanent=pinned.Contains(first.File)};
                entries.Add(e);for(int j=i;j<end;j++){mapping.Add(keys[j],e);if(baselineKeys.Contains(keys[j]))e.Baseline=true;}
                // A complete coarse model survives every culling/eviction/failed refinement.
                if(e.Baseline){e.Permanent=true;baselineEntries.Add(e);}
                if(e.Permanent){permanentPoints+=e.Key.Count;permanentBytes+=e.Bytes;}
                i=end;
            }
            foreach(var key in baselineKeys)baselinePoints+=key.Count;
        }
        internal Entry Find(GaussianLODRange r) => r.count==0?null:mapping[r.Key];
        public void ConfigureGPU()
        {
            if(GPU!=null&&configuredBudget==owner.maxGPUMemory&&configuredVisible==owner.maxVisibleSplats&&configuredResident==owner.maxResidentSplats){WaitingForGPU=false;return;}
            if(GPU!=null){
                if(!WaitingForGPU){rebuildFence=GPU.CaptureFence();WaitingForGPU=true;}
                if(!GaussianGPUResources.CanRecycle(rebuildFence))return;
            }
            WaitingForGPU=false;
            if(baselinePoints>Mathf.Clamp(owner.maxVisibleSplats,1,8000000))
                throw new InvalidOperationException($"完整粗级模型需要至少 {baselinePoints:N0} 个可见点；请增加 Max Visible Splats，避免分区缺失。");
            if(permanentBytes>owner.maxCPUCache*1024L*1024 || permanentBytes+GaussianGPUResources.SortBytes(owner.maxVisibleSplats)>owner.maxGPUMemory*1024L*1024)
                throw new InvalidOperationException($"常驻区间需要 {permanentBytes/1048576f:F1} MiB 解码缓存，另需排序空间；请增加 Max CPU Cache / Max GPU Memory，校园建议各 512 MiB。");
            // Free the old arena first; changing the budget must not transiently double GPU allocation.
            GPU?.Dispose();GPU=null;retired.Clear();CoverageReady=false;
            for(int n=0;n<current.Length;n++)current[n]=previous[n]=-1;
            foreach(var e in entries){e.Slot=-1;e.Uploaded=0;}
            var next=new GaussianGPUResources(owner.maxGPUMemory,owner.maxVisibleSplats,owner.ResolveShader(),owner.ResolveSort(),permanentPoints,current.Length,owner.maxResidentSplats);
            if(permanentPoints>next.Capacity){next.Dispose();throw new InvalidOperationException("常驻区间超出 GPU arena 的寻址上限。");}
            GPU=next;configuredBudget=owner.maxGPUMemory;configuredVisible=owner.maxVisibleSplats;configuredResident=owner.maxResidentSplats;
        }
        public static Bounds TransformBounds(Bounds b,Matrix4x4 m)
        {
            Vector3 x=m.MultiplyVector(new Vector3(b.extents.x,0,0)),y=m.MultiplyVector(new Vector3(0,b.extents.y,0)),z=m.MultiplyVector(new Vector3(0,0,b.extents.z));
            var extent=new Vector3(Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x),Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y),Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z));
            return new Bounds(m.MultiplyPoint3x4(b.center),extent*2);
        }
        public void Tick(Camera camera,double now)
        {
            if(disposed)return;
            ConfigureGPU();if(WaitingForGPU)return;CollectSlots();
            if(permanentBytes>owner.maxCPUCache*1048576L)throw new InvalidOperationException("CPU cache budget cannot contain permanent ranges");
            if(configuredIO!=owner.maxConcurrentIO||configuredDecodes!=owner.maxConcurrentDecodes){Loader.ConfigureConcurrency(owner.maxConcurrentIO,owner.maxConcurrentDecodes);configuredIO=owner.maxConcurrentIO;configuredDecodes=owner.maxConcurrentDecodes;}
            if(!Application.isPlaying||lastUploadFrame!=Time.frameCount){lastUploadFrame=Time.frameCount;owner.Metrics.UploadMilliseconds=0;uploadSubmissions=uploadEntries=0;uploadedThisFrame.Clear();}
            if(camera!=null){bool changed=Quaternion.Angle(lastHeadRotation,camera.transform.rotation)>=owner.headTurnThreshold;
                if(now-lastSelection>=owner.selectionInterval||changed){UpdateMotion(camera,now);Select(camera,now);lastSelection=now;lastHeadRotation=camera.transform.rotation;}}
            Poll(now);
            foreach(var e in entries){e.Wanted=e.Permanent;e.Active=false;e.Queued=false;e.Tier=e.Baseline?3:e.Permanent?2:-1;e.Priority=e.Baseline?100000:e.Permanent?10000:-1;}
            queue.Clear();
            int coarse=Loader.Metadata.levels-1;
            for(int n=0;n<current.Length;n++){
                if(previous[n]>=0&&now-transitionStart[n]>=owner.lodTransitionSeconds)previous[n]=-1;
                Protect(n,current[n],true);Protect(n,previous[n],true);
                if(desired[n]>=0)Protect(n,NextLevel(n),false);
            }
            // Cancel only after every consumer of a shared interval has contributed its protection.
            foreach(var e in entries)if(e.Job!=null&&!e.Permanent&&!e.Active&&!e.Wanted&&!e.Cancelled){e.Cancelled=true;e.Cancellation?.Cancel();owner.Metrics.CancelledRequests++;}
            int starts=0,remaining=Math.Min(owner.uploadSplatsPerFrame,Math.Max(1,owner.uploadBytesPerFrame/GaussianSplat.Stride));
            foreach(var e in baselineEntries){Request(e,now,ref starts);Upload(e,now,ref remaining);}
            bool baselineReady=true;foreach(var e in baselineEntries)if(!e.Ready)baselineReady=false;
            if(!baselineReady){ProcessQueue(now,ref starts);UpdateMetrics();EvictCPU(0,now);return;}
            if(!CoverageReady){for(int n=0;n<current.Length;n++){current[n]=coarse;Protect(n,coarse,true);}CoverageReady=true;UpdateMetrics();return;}
            foreach(var e in entries)if(e.Permanent&&!e.Baseline){Request(e,now,ref starts);Upload(e,now,ref remaining);}
            int frontier=RefinementFront(now);
            foreach(int n in ordered){
                if(previous[n]>=0||desired[n]<0)continue;
                int next=NextLevel(n);
                if(owner.coherentLOD&&visible[n]&&distances[n]>owner.nearQualityDistance&&next<current[n]&&current[n]<frontier)continue;
                var e=Find(Loader.Metadata.tree[n].lods[next]);
                if(e==null)continue;
                e.Tier=Math.Max(e.Tier,visible[n]?1:0);e.Priority=Math.Max(e.Priority,nodePriorities[n]);
                if(!visible[n]&&predicted[n]&&!e.Prefetched){e.Prefetched=true;owner.Metrics.PreloadRequests++;}
                Request(e,now,ref starts);
            }
            ProcessQueue(now,ref starts);
            // Upload in the same dynamically re-scored order, not the order reads happened to finish.
            foreach(var e in entries)if(e.CPU!=null&&!e.Ready&&e.Wanted&&!e.Queued){e.Queued=true;queue.Add(e);}
            queue.Sort(ComparePriority);
            foreach(var e in queue)if(e.Wanted)Upload(e,now,ref remaining);
            CommitReady(now);EvictCPU(0,now);UpdateMetrics();
        }
        void UpdateMetrics()
        {
            var m=owner.Metrics;m.QueueCount=0;m.UploadingCount=0;
            foreach(var e in entries){if(e.Queued&&e.Job==null&&e.CPU==null&&!e.Ready)m.QueueCount++;if(e.CPU!=null&&!e.Ready&&e.Wanted)m.UploadingCount++;}
            m.ReadingCount=Loader.ActiveReads;m.DecodingCount=Loader.ActiveParses;m.IOReadMilliseconds=Loader.IOReadMilliseconds;m.DecodeMilliseconds=Loader.DecodeMilliseconds;m.IOBytes=Loader.IOBytes;m.StagingBytes=Loader.StagingBytes;
        }
        static int ComparePriority(Entry a,Entry b){int tier=b.Tier.CompareTo(a.Tier);if(tier!=0)return tier;int result=b.Priority.CompareTo(a.Priority);return result!=0?result:a.Key.File.CompareTo(b.Key.File);}
        void ProcessQueue(double now,ref int starts)
        {
            queue.Sort(ComparePriority);
            foreach(var e in queue)if(e.Wanted&&!e.Cancelled)StartRequest(e,now,ref starts);
        }
        void UpdateMotion(Camera camera,double now)
        {
            Vector3 rig=owner.streamingOrigin!=null?owner.streamingOrigin.position:camera.transform.position;
            float elapsed=(float)Math.Max(.001,now-motionTime);Vector3 delta=rig-lastRigPosition;
            teleported=motionKnown&&delta.sqrMagnitude>owner.teleportDistance*owner.teleportDistance;
            if(!motionKnown||teleported)velocity=Vector3.zero;
            else velocity=Vector3.Lerp(velocity,delta/elapsed,1-Mathf.Exp(-elapsed*8));
            if(teleported){generation++;foreach(var e in entries)if(e.Job!=null&&!e.Permanent&&!e.Active&&!e.Cancelled){e.Cancelled=true;e.Cancellation?.Cancel();owner.Metrics.CancelledRequests++;}}
            lastRigPosition=rig;motionKnown=true;motionTime=now;
            predictedPosition=rig+(owner.enablePrediction?Vector3.ClampMagnitude(velocity*owner.predictionSeconds,owner.maxPredictionDistance*owner.Metrics.PrefetchScale):Vector3.zero);
        }
        void RefreshBounds()
        {
            Matrix4x4 model=owner.ModelMatrix;if(boundsKnown&&boundsMatrix==model&&boundsScale==owner.renderScale)return;
            boundsMatrix=model;boundsScale=owner.renderScale;boundsKnown=true;
            for(int n=0;n<worldBounds.Length;n++){var b=TransformBounds(Loader.Metadata.tree[n].bound.Bounds,model);b.Expand(Math.Max(1,b.size.magnitude*.05f)*Math.Max(1,owner.renderScale));worldBounds[n]=b;}
            index=worldBounds.Length>256?new GaussianChunkBVH(worldBounds):null;
        }
        void CollectSlots()
        {
            for(int i=retired.Count-1;i>=0;i--)if(GaussianGPUResources.CanRecycle(retired[i].Fence)){GPU.Slots.Release(retired[i].Slot,retired[i].Count);retired.RemoveAt(i);}
        }
        void RetireSlot(Entry e)
        {
            if(e.Slot<0)return;retired.Add(new RetiredSlot{Slot=e.Slot,Count=e.Key.Count,Fence=GPU.CaptureFence()});e.Slot=-1;e.Uploaded=0;e.Stage=GaussianRequestStage.Retired;
        }
        void Protect(int n,int level,bool active)
        {
            if(level<0)return;var e=Find(Loader.Metadata.tree[n].lods[level]);
            if(e==null)return;e.Wanted=true;if(active)e.Active=true;
        }
        int NextLevel(int n)
        {
            int c=current[n],d=desired[n];
            // Refinement is progressive; do not jump from 1% straight to 100% on one chunk.
            return c<0?Loader.Metadata.levels-1:d<c?c-1:d;
        }
        bool Ready(int n,int l){var r=Loader.Metadata.tree[n].lods[l];return r.count==0 || Find(r).Ready;}
        bool SharesRecords(int n,int a,int b)
        {
            var x=Loader.Metadata.tree[n].lods[a];var y=Loader.Metadata.tree[n].lods[b];
            return x.Key.Equals(y.Key);
        }
        int Count(int n,int l)=>l<0?0:Loader.Metadata.tree[n].lods[l].count;
        int RefinementFront(double now)
        {
            int front=0;for(int n=0;n<current.Length;n++)if(visible[n]&&desired[n]>=0&&current[n]>desired[n]&&distances[n]>owner.nearQualityDistance){var pending=Find(Loader.Metadata.tree[n].lods[NextLevel(n)]);if(pending==null||pending.Failures==0||pending.RetryAt<=now)front=Math.Max(front,current[n]);}return front;
        }
        void CommitReady(double now)
        {
            long steady=0,draw=0;
            int frontier=RefinementFront(now);
            for(int n=0;n<current.Length;n++)if(visible[n]){steady+=Count(n,current[n]);draw+=Count(n,current[n])+Count(n,previous[n]);}
            // Reductions precede upgrades. Whole-region cuts must fit even while old and new coexist.
            for(int pass=0;pass<2;pass++)for(int k=0;k<current.Length;k++)
            {
                int n=(transitionCursor+k)%current.Length;
                if(previous[n]>=0||desired[n]<0)continue;
                int old=current[n],next=NextLevel(n);if(old==next||!Ready(n,next))continue;
                if(owner.coherentLOD && visible[n]&&distances[n]>owner.nearQualityDistance && next<old && old<frontier)continue;
                int oldCount=Count(n,old),newCount=Count(n,next);
                if((newCount>oldCount)!=(pass==1))continue;
                if(SharesRecords(n,old,next)){current[n]=next;continue;}
                if(!visible[n]){current[n]=next;Protect(n,next,true);continue;}
                long nextSteady=steady-oldCount+newCount;
                if(nextSteady>EffectivePointLimit)continue;
                if(draw+newCount>GPU.VisibleCapacity)continue;
                // Optical-depth fade holds the old range until the transition actually completes.
                previous[n]=old;current[n]=next;transitionStart[n]=now;
                steady=nextSteady;draw+=newCount;Protect(n,next,true);Protect(n,old,true);
            }
            transitionCursor=(transitionCursor+1)%current.Length;
        }
        void Select(Camera camera,double now)
        {
            var meta=Loader.Metadata;var model=owner.ModelMatrix;
            RefreshBounds();selectionViews=GaussianViewContext.FromCamera(camera);ordered.Clear();
            if(index!=null&&owner.enableFrustumCulling){Array.Clear(visible,0,visible.Length);index.Query(selectionViews,visibleCandidates);foreach(int n in visibleCandidates)visible[n]=true;}
            bool moving=velocity.sqrMagnitude>.01f;
            // Reserve a portion of visible/sort capacity for complete old+new transitions.
            int budget=Math.Max((int)baselinePoints,Math.Min(EffectivePointLimit,(int)(GPU.VisibleCapacity*.75f)));
            long used=0;
            for(int n=0;n<current.Length;n++)
            {
                Bounds b=worldBounds[n];
                visible[n]=!owner.enableFrustumCulling||(index==null?selectionViews.TestBounds(b):visible[n]);
                float distance=distances[n]=Mathf.Max(.01f,Mathf.Sqrt(b.SqrDistance(camera.transform.position)));
                float sweep=GaussianPrediction.DistanceToSegment(b.center,lastRigPosition,predictedPosition);
                predicted[n]=owner.enablePrediction&&(Mathf.Sqrt(b.SqrDistance(lastRigPosition))<owner.preloadDistance||sweep<b.extents.magnitude+owner.preloadDistance*owner.Metrics.PrefetchScale);
                float area=selectionViews.ProjectedArea(b);
                float detail=distance<=owner.nearQualityDistance?1:owner.Metrics.DetailScale;
                weights[n]=area*detail/Mathf.Max(.0001f,owner.lodPixelThreshold*owner.lodPixelThreshold);
                target[n]=meta.levels-1;
                if(visible[n]||predicted[n])ordered.Add(n);
                var cachedEntry=Find(meta.tree[n].lods[Math.Max(0,current[n]-1)]);
                nodePriorities[n]=owner.priorityWeights.Score(visible[n],1/(1+distance/Mathf.Max(1,owner.preloadDistance)),predicted[n]?1:0,current[n]>0,cachedEntry!=null&&(cachedEntry.Ready||cachedEntry.CPU!=null),0,0,0)+Mathf.Min(1,area/Mathf.Max(1,camera.pixelWidth*(float)camera.pixelHeight));
                used+=meta.tree[n].lods[meta.levels-1].count;
            }
            ordered.Sort((a,b)=>nodePriorities[b].CompareTo(nodePriorities[a]));
            owner.BudgetLimited=EffectivePointLimit<owner.maxVisibleSplats;owner.BudgetReason=null;
            // Allocate protected near regions first; never satisfy quality by truncating a range.
            foreach(int n in ordered)if(visible[n]&&distances[n]<=owner.nearQualityDistance){int delta=meta.tree[n].lods[0].count-meta.tree[n].lods[target[n]].count;if(used+delta<=budget){used+=delta;target[n]=0;}else{owner.BudgetLimited=true;owner.BudgetReason="Near LOD0 does not fit the whole-region visible budget";}}
            if(!owner.enableDynamicLOD)
            {
                foreach(int n in ordered)if(visible[n]) {int delta=meta.tree[n].lods[0].count-meta.tree[n].lods[target[n]].count;if(used+delta<=budget){used+=delta;target[n]=0;}}
            }
            else
            {
                // Aholo principle: largest projected area per point receives the next refinement.
                for(int iteration=0;iteration<current.Length*meta.levels;iteration++)
                {
                    int best=-1;float score=1;
                    int coarseFront=0;
                    if(owner.coherentLOD)foreach(int n in ordered)
                        if(visible[n]&&distances[n]>owner.nearQualityDistance&&meta.tree[n].lods[0].count>0&&weights[n]>Math.Max(1,meta.tree[n].lods[target[n]].count))coarseFront=Math.Max(coarseFront,target[n]);
                    foreach(int n in ordered) {
                        int l=target[n];if(!visible[n] || l<=0 || (moving&&current[n]<0&&l<=Math.Min(2,meta.levels-1)))continue;
                        // Keep a common detail frontier. A tiny high-priority tile must not reach
                        // L0 while adjacent visible campus regions are still at L3/L4.
                        if(owner.coherentLOD && distances[n]>owner.nearQualityDistance && l<coarseFront)continue;
                        int delta=meta.tree[n].lods[l-1].count-meta.tree[n].lods[l].count;
                        float s=weights[n]/Math.Max(1,meta.tree[n].lods[l].count);
                        if(s>score && used+delta<=budget) {best=n;score=s;}
                    }
                    if(best<0)break;
                    int level=target[best];used+=meta.tree[best].lods[level-1].count-meta.tree[best].lods[level].count;target[best]--;
                }
            }
            foreach(int n in ordered){
                if(!visible[n])target[n]=predicted[n]?Math.Max(1,meta.levels-3):meta.levels-1;
                else if(distances[n]>owner.nearQualityDistance)target[n]=Math.Min(meta.levels-1,target[n]+owner.Metrics.FarLODBias);
            }
            for(int n=0;n<current.Length;n++)
            {
                if(target[n]!=desired[n]) {
                    // Two consecutive schedules (>=100 ms) plus the configured hold time are required.
                    if(candidate[n]!=target[n]){candidate[n]=target[n];targetSince[n]=now;}
                    if(current[n]<0 || now-targetSince[n]>=owner.lodHysteresis){desired[n]=target[n];if(desired[n]>=0)NoteCacheDecision(n,desired[n]);}
                }
            }
        }
        int[] candidate;
        void Request(Entry e,double now,ref int starts)
        {
            if(e.Ready||e.CPU!=null){e.Used=now;return;}
            if(e.Queued)return;
            e.Queued=true;e.Stage=e.Job==null?GaussianRequestStage.Queued:GaussianRequestStage.Loading;
            if(e.QueuedSince<=0)e.QueuedSince=now;
            e.Priority/=(1+e.LastLoadMilliseconds/100+e.Bytes/(32.0*1048576));
            if(!e.Permanent)e.Priority+=owner.priorityWeights.age*(float)Math.Min(60,now-e.QueuedSince);
            queue.Add(e);
        }
        void StartRequest(Entry e,double now,ref int starts)
        {
            if(e.Ready || e.CPU!=null){e.Used=now;return;}
            if(e.Job!=null||now<e.RetryAt||starts>=Math.Max(1,owner.loadChunksPerFrame)||InFlight>=Math.Max(1,owner.maxConcurrentLoads))return;
            if(e.Key.Count>GPU.Capacity){Fail(e,now,new IOException("单个共享区间大于 GPU 容量，请增加 Max GPU Memory 或重新切块。"));return;}
            if(e.Tier==0){
                if(now<e.DeferPrefetchUntil)return;
                long prefetchBytes=0;foreach(var c in entries)if(c.Tier==0){if(c.CPU!=null)prefetchBytes+=c.Bytes;if(c.Job!=null)prefetchBytes+=c.Reservation;}
                if(prefetchBytes+e.Bytes+1048576>owner.maxCPUCache*1048576L*owner.prefetchMemoryFraction)return;
            }
            if(!EvictCPU(e.Bytes+1048576,now)){
                // Unpublished speculative data must not hold CPU memory hostage while a visible
                // whole-range request waits. In-flight allocations remain reserved until completion.
                if(e.Tier>0)foreach(var c in entries)if(c.Tier==0&&!c.Permanent&&!c.Active&&c.CPU!=null){
                    RetireSlot(c);c.CPU=null;CPUBytes-=c.Bytes;c.Prefetched=c.PrefetchConsumed=false;c.DeferPrefetchUntil=now+2;
                }
                if(!EvictCPU(e.Bytes+1048576,now)){owner.BudgetLimited=true;return;}
            }
            owner.Metrics.QueueWaitMilliseconds=Math.Max(0,now-e.QueuedSince)*1000;
            e.Reservation=e.Bytes+1048576;CPUBytes+=e.Reservation;starts++;DiskReads++;InFlight++;e.Used=now;e.RequestedAt=now;e.Generation=generation;
            e.Cancellation=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);e.Cancelled=false;e.Stage=GaussianRequestStage.Loading;
            owner.Metrics.CacheRequests++;
            if(owner.enableAsyncLoading||Loader.RequiresAsyncFileGate)e.Job=Loader.ReadAsync(e.Key,e.Cancellation.Token);
            else {try{e.Job=Task.FromResult(Loader.Read(e.Key,e.Cancellation.Token));}catch(Exception ex){e.Job=Task.FromException<GaussianSplat[]>(ex);}}
        }
        void Poll(double now)
        {
            foreach(var e in entries)if(e.Job!=null&&e.Job.IsCompleted)
            {
                var job=e.Job;e.Job=null;InFlight--;e.Cancellation?.Dispose();e.Cancellation=null;
                CPUBytes-=e.Reservation;e.Reservation=0;
                if(e.Cancelled||disposed||(!e.Permanent&&e.Generation!=generation)){if(job.IsFaulted){var observed=job.Exception;}e.Cancelled=false;e.Stage=GaussianRequestStage.Idle;owner.Metrics.DroppedResults++;continue;}
                if(job.IsCanceled){e.Stage=GaussianRequestStage.Idle;continue;}
                if(job.IsFaulted){Fail(e,now,job.Exception?.GetBaseException());continue;}
                e.CPU=job.Result;CPUBytes+=e.Bytes;e.Failures=0;e.Stage=GaussianRequestStage.Uploading;e.LastLoadMilliseconds=(now-e.RequestedAt)*1000;
            }
        }
        void Fail(Entry e,double now,Exception ex)
        {
            e.RetryAt=now+Math.Min(60,Math.Pow(2,Math.Min(6,++e.Failures)));
            owner.ReportError($"读取 {e.Key} 失败；保留可用 LOD，{e.RetryAt-now:F0}s 后重试。{ex?.Message}");
        }
        bool EvictCPU(long needed,double now)
        {
            long limit=owner.maxCPUCache*1024L*1024;
            long desiredLimit=needed==0&&CPUBytes>limit*owner.cacheHighWatermark?(long)(limit*owner.cacheLowWatermark):limit;
            while(CPUBytes+needed>desiredLimit)
            {
                Entry oldest=null;
                foreach(var e in entries)if(!e.Permanent&&e.CPU!=null&&!(e.Slot>=0&&!e.Ready)&&(!e.Wanted||e.Ready)&&(needed>0||now-e.Used>=owner.cacheRetentionSeconds))if(oldest==null||e.Used<oldest.Used)oldest=e;
                if(oldest==null)return CPUBytes+needed<=limit;oldest.CPU=null;CPUBytes-=oldest.Bytes;
            }
            return true;
        }
        void Upload(Entry e,double now,ref int remaining)
        {
            if(e.Ready||e.CPU==null||remaining<=0||uploadSubmissions>=owner.maxUploadSubmissions)return;
            if(owner.Metrics.UploadMilliseconds>=owner.gpuUploadBudgetMilliseconds*owner.Metrics.UploadScale)return;
            if(!uploadedThisFrame.Contains(e)&&uploadEntries>=owner.maxConcurrentUploads)return;
            if(e.Slot<0)
            {
                if(e.Stage==GaussianRequestStage.Retired)e.RequestedAt=now;
                int slot=GPU.Slots.Allocate(e.Key.Count);
                while(slot<0)
                {
                    Entry oldest=null;
                    foreach(var c in entries)if(c.Slot>=0&&!c.Permanent&&!c.Active&&!c.Wanted)if(oldest==null||c.Used<oldest.Used)oldest=c;
                    if(oldest==null){owner.BudgetLimited=true;return;}
                    RetireSlot(oldest);CollectSlots();
                    slot=GPU.Slots.Allocate(e.Key.Count);
                }
                e.Slot=slot;
            }
            if(uploadedThisFrame.Add(e))uploadEntries++;
            while(remaining>0&&!e.Ready&&uploadSubmissions<owner.maxUploadSubmissions&&owner.Metrics.UploadMilliseconds<owner.gpuUploadBudgetMilliseconds*owner.Metrics.UploadScale){
                int n=Math.Min(4096,Math.Min(remaining,e.Key.Count-e.Uploaded));
                uploadWatch.Restart();GPU.Splats.SetData(e.CPU,e.Uploaded,e.Slot+e.Uploaded,n);uploadWatch.Stop();
                owner.Metrics.UploadMilliseconds+=uploadWatch.Elapsed.TotalMilliseconds;
                e.Uploaded+=n;remaining-=n;uploadSubmissions++;e.Used=now;
            }
            if(e.Ready){e.Stage=GaussianRequestStage.Resident;e.QueuedSince=0;owner.Metrics.Loaded((now-e.RequestedAt)*1000);}
        }
        public List<Segment> VisibleSegments(Camera camera)=>VisibleSegments(camera,GaussianViewContext.FromCamera(camera));
        public List<Segment> VisibleSegments(Camera camera,GaussianViewContext views)
        {
            segments.Clear();if(!CoverageReady)return segments;
            RefreshBounds();
            double now=Time.realtimeSinceStartupAsDouble;
            long count=0;
            for(int n=0;n<current.Length;n++)
            {
                Bounds b=worldBounds[n];
                if(owner.enableFrustumCulling&&!views.TestBounds(b))continue;
                int c=current[n],p=previous[n];
                float t=p<0?1:Mathf.SmoothStep(0,1,Mathf.Clamp01((float)((now-transitionStart[n])/Math.Max(.1f,owner.lodTransitionSeconds))));
                AddSegment(n,c,t,now,false);if(p>=0&&t<1)AddSegment(n,p,1-t,now,true);
            }
            foreach(var segment in segments)count+=segment.Count;
            if(count>GPU.VisibleCapacity)
            {
                // Another camera or rapid view rotation can reveal more detail than the last
                // scheduling view. Draw a complete resident coarse model, never partial arrays.
                segments.Clear();
                for(int n=0;n<current.Length;n++){
                    var b=worldBounds[n];
                    if(!owner.enableFrustumCulling||views.TestBounds(b))AddSegment(n,Loader.Metadata.levels-1,1,now);
                }
                owner.BudgetLimited=true;
            }
            segments.Sort((a,b)=>a.Slot.CompareTo(b.Slot));return segments;
        }
        void AddSegment(int node,int level,float fade,double now,bool old=false)
        {
            if(level<0||fade<=0)return;
            var r=Loader.Metadata.tree[node].lods[level];var e=Find(r);
            int index=node*2+(old?1:0);Fades[index]=fade;
            if(e!=null&&e.Ready){if(e.Prefetched&&!e.PrefetchConsumed){e.PrefetchConsumed=true;owner.Metrics.PreloadHits++;}e.Used=now;segments.Add(new Segment{Slot=e.Slot+checked((int)(r.offset-e.Key.Offset)),Count=r.count,Level=level,FadeIndex=index});}
        }
        public void NoteCacheDecision(int n,int level)
        {
            var e=Find(Loader.Metadata.tree[n].lods[level]);if(e!=null&&(e.Ready||e.CPU!=null)){CacheHits++;owner.Metrics.CacheRequests++;owner.Metrics.CacheHits++;}
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;cancel.Cancel();
            foreach(var e in entries)if(e.Job!=null) {
                e.Cancellation?.Cancel();var cancellation=e.Cancellation;
                // Observe faults and drop late arrays; background I/O owns no Unity GPU resources.
                _=e.Job.ContinueWith(t=>{if(t.IsFaulted){var ignored=t.Exception;}cancellation?.Dispose();},TaskScheduler.Default);
                e.Job=null;
            }
            GPU?.Dispose();GPU=null;entries.Clear();mapping.Clear();CPUBytes=0;cancel.Dispose();
        }
        public void InitializeCandidates() { candidate=new int[current.Length];for(int i=0;i<candidate.Length;i++)candidate[i]=-2; }
    }
}
