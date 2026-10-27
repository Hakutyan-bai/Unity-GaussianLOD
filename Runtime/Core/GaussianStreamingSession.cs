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
        internal sealed class Entry
        {
            public GaussianRangeKey Key;
            public bool Permanent, Baseline, Wanted, Active;
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
        readonly Plane[] planes=new Plane[6];
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
        int configuredBudget,configuredVisible;
        double lastSelection=-1;
        Vector3 previousCamera;
        bool cameraKnown;
        bool disposed;
        public GaussianStreamingSession(GaussianLODRenderer renderer,GaussianLODLoader loader)
        {
            owner=renderer;Loader=loader;
            var meta=loader.Metadata;
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
            if(GPU!=null&&configuredBudget==owner.maxGPUMemory&&configuredVisible==owner.maxVisibleSplats)return;
            if(baselinePoints>Mathf.Clamp(owner.maxVisibleSplats,1,8000000))
                throw new InvalidOperationException($"完整粗级模型需要至少 {baselinePoints:N0} 个可见点；请增加 Max Visible Splats，避免分区缺失。");
            if(permanentBytes>owner.maxCPUCache*1024L*1024 || permanentBytes+GaussianGPUResources.SortBytes(owner.maxVisibleSplats)>owner.maxGPUMemory*1024L*1024)
                throw new InvalidOperationException($"常驻区间需要 {permanentBytes/1048576f:F1} MiB 解码缓存，另需排序空间；请增加 Max CPU Cache / Max GPU Memory，校园建议各 512 MiB。");
            // Free the old arena first; changing the budget must not transiently double GPU allocation.
            GPU?.Dispose();GPU=null;CoverageReady=false;
            for(int n=0;n<current.Length;n++)current[n]=previous[n]=-1;
            foreach(var e in entries){e.Slot=-1;e.Uploaded=0;}
            var next=new GaussianGPUResources(owner.maxGPUMemory,owner.maxVisibleSplats,owner.ResolveShader(),owner.ResolveSort(),permanentPoints,current.Length);
            if(permanentPoints>next.Capacity){next.Dispose();throw new InvalidOperationException("常驻区间超出 GPU arena 的寻址上限。");}
            GPU=next;configuredBudget=owner.maxGPUMemory;configuredVisible=owner.maxVisibleSplats;
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
            ConfigureGPU();
            if(permanentBytes>owner.maxCPUCache*1024L*1024)throw new InvalidOperationException("Max CPU Cache 小于常驻数据，请增加预算。");
            Poll(now);
            if(camera!=null && now-lastSelection>=.1){Select(camera,now);lastSelection=now;}
            foreach(var e in entries){e.Wanted=e.Permanent;e.Active=false;}
            int coarse=Loader.Metadata.levels-1;
            for(int n=0;n<current.Length;n++)
            {
                if(previous[n]>=0 && now-transitionStart[n]>=owner.lodTransitionSeconds)previous[n]=-1;
                Protect(n,current[n],true);Protect(n,previous[n],true);
                if(desired[n]>=0)Protect(n,NextLevel(n),false);
            }
            int starts=0,remaining=owner.uploadSplatsPerFrame;
            // Global coverage first. Do not spend IO/upload capacity on L3 while any L4 is missing.
            foreach(var e in baselineEntries){Request(e,now,ref starts);Upload(e,now,ref remaining);}
            bool baselineReady=true;foreach(var e in baselineEntries)if(!e.Ready)baselineReady=false;
            if(!baselineReady){EvictCPU(0);return;}
            if(!CoverageReady)
            {
                for(int n=0;n<current.Length;n++){current[n]=coarse;Protect(n,coarse,true);}
                CoverageReady=true;
                // Publish the complete baseline for at least one frame before any refinement.
                return;
            }
            foreach(var e in entries)if(e.Permanent&&!e.Baseline){Request(e,now,ref starts);Upload(e,now,ref remaining);}
            int frontier=RefinementFront();
            foreach(int n in ordered)
            {
                if(previous[n]>=0 || desired[n]<0)continue;
                int next=NextLevel(n);
                if(owner.coherentLOD && next<current[n] && current[n]<frontier)continue;
                var e=Find(Loader.Metadata.tree[n].lods[next]);
                if(e!=null){Request(e,now,ref starts);Upload(e,now,ref remaining);}
            }
            CommitReady(now);
            EvictCPU(0);
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
        int RefinementFront()
        {
            int front=0;for(int n=0;n<current.Length;n++)if(visible[n]&&desired[n]>=0&&current[n]>desired[n])front=Math.Max(front,current[n]);return front;
        }
        void CommitReady(double now)
        {
            long steady=0,draw=0;
            int frontier=RefinementFront();
            for(int n=0;n<current.Length;n++)if(visible[n]){steady+=Count(n,current[n]);draw+=Count(n,current[n])+Count(n,previous[n]);}
            // Reductions precede upgrades. Whole-region cuts must fit even while old and new coexist.
            for(int pass=0;pass<2;pass++)for(int k=0;k<current.Length;k++)
            {
                int n=(transitionCursor+k)%current.Length;
                if(previous[n]>=0||desired[n]<0)continue;
                int old=current[n],next=NextLevel(n);if(old==next||!Ready(n,next))continue;
                if(owner.coherentLOD && next<old && old<frontier)continue;
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
            GeometryUtility.CalculateFrustumPlanes(camera,planes);ordered.Clear();
            float focal=camera.orthographic?camera.pixelHeight/Mathf.Max(.001f,2*camera.orthographicSize):camera.pixelHeight/(2*Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f));
            float elapsed=lastSelection<0?.1f:(float)Math.Max(.01,now-lastSelection);
            float speed=cameraKnown?Vector3.Distance(camera.transform.position,previousCamera)/elapsed:0;
            bool moving=speed>.1f;previousCamera=camera.transform.position;cameraKnown=true;
            // Reserve a portion of visible/sort capacity for complete old+new transitions.
            int budget=Math.Max((int)baselinePoints,Math.Min(EffectivePointLimit,(int)(GPU.VisibleCapacity*.75f)));
            long used=0;
            for(int n=0;n<current.Length;n++)
            {
                Bounds b=TransformBounds(meta.tree[n].bound.Bounds,model);
                // Dense Aholo bounds omit outliers. Add a guard band and movement lookahead so a
                // region stays resident while its ellipse footprint enters/leaves the frustum.
                b.Expand(Math.Max(1,b.size.magnitude*.05f)*Math.Max(1,owner.renderScale)+Math.Min(speed*.6f,owner.preloadDistance));
                visible[n]=!owner.enableFrustumCulling||GeometryUtility.TestPlanesAABB(planes,b);
                float distance=Mathf.Max(.01f,Mathf.Sqrt(b.SqrDistance(camera.transform.position)));
                float diameter=camera.orthographic?b.size.magnitude*focal:b.size.magnitude*focal/distance;
                weights[n]=Mathf.Min(camera.pixelWidth*(float)camera.pixelHeight,diameter*diameter);
                target[n]=meta.levels-1;
                // Off-screen preload is distance limited and never contributes to drawing.
                if(visible[n] || distance<owner.preloadDistance)ordered.Add(n);
                used+=meta.tree[n].lods[meta.levels-1].count;
            }
            ordered.Sort((a,b)=>weights[b].CompareTo(weights[a]));
            owner.BudgetLimited=EffectivePointLimit<owner.maxVisibleSplats;
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
                        if(visible[n]&&meta.tree[n].lods[0].count>0&&weights[n]>Math.Max(1,meta.tree[n].lods[target[n]].count))coarseFront=Math.Max(coarseFront,target[n]);
                    foreach(int n in ordered) {
                        int l=target[n];if(!visible[n] || l<=0 || (moving&&current[n]<0&&l<=Math.Min(2,meta.levels-1)))continue;
                        // Keep a common detail frontier. A tiny high-priority tile must not reach
                        // L0 while adjacent visible campus regions are still at L3/L4.
                        if(owner.coherentLOD && l<coarseFront)continue;
                        int delta=meta.tree[n].lods[l-1].count-meta.tree[n].lods[l].count;
                        float s=weights[n]/Math.Max(1,meta.tree[n].lods[l].count);
                        if(s>score && used+delta<=budget) {best=n;score=s;}
                    }
                    if(best<0)break;
                    int level=target[best];used+=meta.tree[best].lods[level-1].count-meta.tree[best].lods[level].count;target[best]--;
                }
            }
            foreach(int n in ordered)if(!visible[n])target[n]=meta.levels-1;
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
            if(e.Ready || e.CPU!=null){e.Used=now;return;}
            if(e.Job!=null||now<e.RetryAt||starts>=Math.Max(1,owner.loadChunksPerFrame)||InFlight>=Math.Max(1,owner.maxConcurrentLoads))return;
            if(e.Key.Count>GPU.Capacity){Fail(e,now,new IOException("单个共享区间大于 GPU 容量，请增加 Max GPU Memory 或重新切块。"));return;}
            if(!EvictCPU(e.Bytes)){owner.BudgetLimited=true;return;}
            CPUBytes+=e.Bytes;starts++;DiskReads++;InFlight++;e.Used=now;
            if(owner.enableAsyncLoading)e.Job=Loader.ReadAsync(e.Key,cancel.Token);
            else {try{e.Job=Task.FromResult(Loader.Read(e.Key,cancel.Token));}catch(Exception ex){e.Job=Task.FromException<GaussianSplat[]>(ex);}}
        }
        void Poll(double now)
        {
            foreach(var e in entries)if(e.Job!=null&&e.Job.IsCompleted)
            {
                var job=e.Job;e.Job=null;InFlight--;
                if(job.IsCanceled){CPUBytes-=e.Bytes;continue;}
                if(job.IsFaulted){CPUBytes-=e.Bytes;Fail(e,now,job.Exception?.GetBaseException());continue;}
                e.CPU=job.Result;e.Failures=0;
            }
        }
        void Fail(Entry e,double now,Exception ex)
        {
            e.RetryAt=now+Math.Min(60,Math.Pow(2,Math.Min(6,++e.Failures)));
            owner.ReportError($"读取 {e.Key} 失败；保留可用 LOD，{e.RetryAt-now:F0}s 后重试。{ex?.Message}");
        }
        bool EvictCPU(long needed)
        {
            long limit=owner.maxCPUCache*1024L*1024;
            while(CPUBytes+needed>limit)
            {
                Entry oldest=null;
                foreach(var e in entries)if(!e.Permanent&&e.CPU!=null&&!(e.Slot>=0&&!e.Ready)&&(!e.Wanted||e.Ready))if(oldest==null||e.Used<oldest.Used)oldest=e;
                if(oldest==null)return false;oldest.CPU=null;CPUBytes-=oldest.Bytes;
            }
            return true;
        }
        void Upload(Entry e,double now,ref int remaining)
        {
            if(e.Ready||e.CPU==null||remaining<=0)return;
            if(e.Slot<0)
            {
                int slot=GPU.Slots.Allocate(e.Key.Count);
                while(slot<0)
                {
                    Entry oldest=null;
                    foreach(var c in entries)if(c.Slot>=0&&!c.Permanent&&!c.Active&&!c.Wanted)if(oldest==null||c.Used<oldest.Used)oldest=c;
                    if(oldest==null){owner.BudgetLimited=true;return;}
                    GPU.Slots.Release(oldest.Slot,oldest.Key.Count);oldest.Slot=-1;oldest.Uploaded=0;
                    slot=GPU.Slots.Allocate(e.Key.Count);
                }
                e.Slot=slot;
            }
            int n=Math.Min(remaining,e.Key.Count-e.Uploaded);
            GPU.Splats.SetData(e.CPU,e.Uploaded,e.Slot+e.Uploaded,n);e.Uploaded+=n;remaining-=n;e.Used=now;
        }
        public List<Segment> VisibleSegments(Camera camera)
        {
            segments.Clear();if(!CoverageReady)return segments;
            GeometryUtility.CalculateFrustumPlanes(camera,planes);var model=owner.ModelMatrix;
            double now=Time.realtimeSinceStartupAsDouble;
            long count=0;
            for(int n=0;n<current.Length;n++)
            {
                Bounds b=TransformBounds(Loader.Metadata.tree[n].bound.Bounds,model);
                b.Expand(Math.Max(1,b.size.magnitude*.05f)*Math.Max(1,owner.renderScale));
                if(owner.enableFrustumCulling&&!GeometryUtility.TestPlanesAABB(planes,b))continue;
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
                    var b=TransformBounds(Loader.Metadata.tree[n].bound.Bounds,model);b.Expand(Math.Max(1,b.size.magnitude*.05f)*Math.Max(1,owner.renderScale));
                    if(!owner.enableFrustumCulling||GeometryUtility.TestPlanesAABB(planes,b))AddSegment(n,Loader.Metadata.levels-1,1,now);
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
            if(e!=null&&e.Ready){e.Used=now;segments.Add(new Segment{Slot=e.Slot+checked((int)(r.offset-e.Key.Offset)),Count=r.count,Level=level,FadeIndex=index});}
        }
        public void NoteCacheDecision(int n,int level)
        {
            var e=Find(Loader.Metadata.tree[n].lods[level]);if(e!=null&&(e.Ready||e.CPU!=null))CacheHits++;
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;cancel.Cancel();
            foreach(var e in entries)if(e.Job!=null) {
                // Observe faults and drop late arrays; background I/O owns no Unity GPU resources.
                _=e.Job.ContinueWith(t=>{if(t.IsFaulted){var ignored=t.Exception;}},TaskScheduler.Default);
                e.Job=null;
            }
            GPU?.Dispose();GPU=null;entries.Clear();mapping.Clear();CPUBytes=0;cancel.Dispose();
        }
        public void InitializeCandidates() { candidate=new int[current.Length];for(int i=0;i<candidate.Length;i++)candidate[i]=-2; }
    }
}
