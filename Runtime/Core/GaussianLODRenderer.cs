using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatLOD
{
    public enum GaussianDataLocation { ExternalDirectory, StreamingAssets, PersistentDataDirectory }
    [ExecuteAlways,DisallowMultipleComponent,AddComponentMenu("Rendering/Gaussian LOD Renderer")]
    public sealed class GaussianLODRenderer : MonoBehaviour
    {
        [Header("Model Configuration")]
        public TextAsset jsonConfiguration;
        public GaussianDataLocation dataLocation=GaussianDataLocation.StreamingAssets;
        public string jsonPath="GaussianSplatLOD/Demo/lod-meta.json";
        public bool loadOnEnable=true;
        public Camera targetCamera;
        [Header("Rendering Settings")]
        public bool enableRendering=true,enableDynamicLOD=true,enableFrustumCulling=true;
        [Tooltip("先提升所有可见区域的共同精度，再细化局部，减少清晰/模糊片区拼接感。")]
        public bool coherentLOD=true;
        [Min(1)] public int maxVisibleSplats=1000000;
        [Range(.05f,4)] public float renderScale=1;
        [Range(0,10)] public float opacityMultiplier=1;
        public float coordinateScale=1;
        public Vector3 positionOffset,rotationOffset;
        [Header("Streaming Settings")]
        [Min(16)] public int maxGPUMemory=512;
        [Min(1)] public int maxCPUCache=512;
        [Min(0)] public float preloadDistance=20;
        [Range(1,16)] public int loadChunksPerFrame=2;
        public bool enableAsyncLoading=true;
        [Tooltip("优先使用 Editor 生成的无损 .gsbin；失效或缺失时回退 PLY。修改后 Reload 生效。")]
        public bool preferRuntimeCache=true;
        [Range(1,8)] public int maxConcurrentLoads=2;
        [Range(1024,262144)] public int uploadSplatsPerFrame=16384;
        [Range(.1f,2)] public float lodHysteresis=.25f;
        [Range(.1f,2)] public float lodTransitionSeconds=.4f;
        [Header("Predictive streaming")]
        public Transform streamingOrigin;
        public bool enablePrediction=true;
        [Min(0)] public float predictionSeconds=1, maxPredictionDistance=60;
        [Min(1)] public float teleportDistance=20;
        [Range(.01f,.5f)] public float selectionInterval=.05f;
        [Range(1,90)] public float headTurnThreshold=10;
        [Min(0)] public float nearQualityDistance=40;
        [Min(.01f)] public float lodPixelThreshold=1;
        public GaussianPriorityWeights priorityWeights=new GaussianPriorityWeights();
        [Header("Pipeline and cache budgets")]
        [Min(0)] public int maxResidentSplats=0;
        [Range(1,8)] public int maxConcurrentIO=2, maxConcurrentDecodes=1;
        [Range(1,8)] public int maxConcurrentUploads=1;
        [Range(.1f,10)] public float gpuUploadBudgetMilliseconds=.75f;
        [Min(262144)] public int uploadBytesPerFrame=4*1048576;
        [Range(1,32)] public int maxUploadSubmissions=4;
        [Min(0)] public float cacheRetentionSeconds=10;
        [Range(.1f,1)] public float cacheHighWatermark=.9f, cacheLowWatermark=.8f;
        [Range(0,.5f)] public float prefetchMemoryFraction=.2f;
        public bool enablePerformanceScheduling;
        [Range(30,144)] public float targetRefreshRate=90;
        [Header("Debug Settings")]
        public bool showChunkBounds,showLODLevel,showStatistics=true,showLoadingStatus=true;
        [SerializeField,HideInInspector] Shader gaussianShader;
        [SerializeField,HideInInspector] ComputeShader sortingShader;

        public GaussianStreamingMetrics Metrics {get;private set;}=new GaussianStreamingMetrics();
        public double ActualUploadMilliseconds=>Metrics.UploadMilliseconds;
        public double SchedulingMilliseconds=>Metrics.SchedulingMilliseconds;
        public int QueuedChunks=>Metrics.QueueCount;
        public string BudgetReason {get;internal set;}
        public string InstallationStatus=>datasetSource?.Status;
        public float InstallationProgress=>datasetSource?.Progress??0;
        public string Status {get;private set;}="Unloaded";
        public string LastError {get;private set;}
        public int VisibleSplats {get;private set;}
        public int VisibleChunks {get;private set;}
        public float FPS {get;private set;}
        public float LastUploadMilliseconds {get;private set;}
        public bool BudgetLimited {get;internal set;}
        public bool CoverageReady => session?.CoverageReady??false;
        public int CoveredNodes => session?.CoveredNodes??0;
        public int TransitioningNodes => session?.TransitioningNodes??0;
        public int EffectivePointLimit => session?.EffectivePointLimit??0;
        public long GPUBytes => session?.GPU?.Bytes??0;
        public long CPUBytes => session?.CPUBytes??0;
        public int PendingLoads => session?.InFlight??0;
        public long DiskReads => session?.DiskReads??0;
        public long RuntimeCacheReads => session?.Loader.RuntimeCacheReads??0;
        public long PLYReads => session?.Loader.PLYReads??0;
        public int ResidentRanges => session?.ResidentRanges??0;
        public float CacheHitRate => Metrics.CacheHitRate;
        public GaussianLODMetadata Metadata => session?.Loader.Metadata;
        public string DataDirectory {get {try{return Path.GetDirectoryName(ResolveJsonPath());}catch{return "";}}}
        public Matrix4x4 ModelMatrix => transform.localToWorldMatrix*Matrix4x4.TRS(positionOffset,Quaternion.Euler(rotationOffset),Vector3.one*coordinateScale);
        public static Func<Camera> EditorCameraProvider;
        internal static readonly HashSet<GaussianLODRenderer> Instances=new HashSet<GaussianLODRenderer>();
        GaussianStreamingSession session;
        GaussianGPUResources drawGPU;
        Task<GaussianLODLoader> metadataJob;
        GaussianDatasetSource datasetSource;
        System.Threading.CancellationTokenSource metadataCancellation;
        readonly Dictionary<Camera,CommandBuffer> builtinCommands=new Dictionary<Camera,CommandBuffer>();
        MaterialPropertyBlock properties;
        GaussianVisibleSegment[] descriptors;
        Matrix4x4 lastSortModel,lastSortView,lastSortOtherView;
        Camera lastSortCamera;
        bool sortKnown,lastSortSeparate;


        ulong lastSignature;
        int lastCount;
        double lastTick=-1;
        bool unloaded=true;
        bool tickFailed;
        public string ResolveJsonPath()
        {
            if(string.IsNullOrWhiteSpace(jsonPath))throw new IOException("请先选择 lod-meta.json。");
            if(dataLocation==GaussianDataLocation.ExternalDirectory)return Path.GetFullPath(jsonPath);
            if(Path.IsPathRooted(jsonPath)||jsonPath.Contains(":")||Array.IndexOf(jsonPath.Replace('\\','/').Split('/'),"..")>=0)throw new IOException("StreamingAssets 模式需要相对 JSON 路径。");
            string root=dataLocation==GaussianDataLocation.PersistentDataDirectory?Application.persistentDataPath:Application.streamingAssetsPath;
            return root.Contains("://")?root.TrimEnd('/')+"/"+jsonPath.Replace('\\','/'):Path.GetFullPath(Path.Combine(root,jsonPath));
        }
        internal Shader ResolveShader() => gaussianShader!=null?gaussianShader:(gaussianShader=Resources.Load<Shader>("GaussianLOD"));
        internal ComputeShader ResolveSort() => sortingShader!=null?sortingShader:(sortingShader=Resources.Load<ComputeShader>("GaussianLODSort"));
        public void SetShaderAssets(Shader draw,ComputeShader sort){gaussianShader=draw;sortingShader=sort;}
        void OnEnable() {properties=new MaterialPropertyBlock();Instances.Add(this);Camera.onPreCull+=OnBuiltinCamera;if(loadOnEnable)Load();}
        void OnDisable(){Camera.onPreCull-=OnBuiltinCamera;Instances.Remove(this);Unload();}
        void OnDestroy()=>Unload();
        void OnApplicationQuit()=>Unload();
        void OnValidate()
        {
            maxVisibleSplats=Mathf.Clamp(maxVisibleSplats,1,8000000);maxGPUMemory=Mathf.Max(16,maxGPUMemory);maxCPUCache=Mathf.Max(1,maxCPUCache);
            loadChunksPerFrame=Mathf.Clamp(loadChunksPerFrame,1,16);maxConcurrentLoads=Mathf.Clamp(maxConcurrentLoads,1,8);uploadSplatsPerFrame=Mathf.Clamp(uploadSplatsPerFrame,1024,262144);
            maxResidentSplats=Mathf.Max(0,maxResidentSplats);maxConcurrentIO=Mathf.Clamp(maxConcurrentIO,1,8);maxConcurrentDecodes=Mathf.Clamp(maxConcurrentDecodes,1,8);maxConcurrentUploads=Mathf.Clamp(maxConcurrentUploads,1,8);
            selectionInterval=Mathf.Clamp(selectionInterval,.01f,.5f);teleportDistance=Mathf.Max(1,teleportDistance);predictionSeconds=Mathf.Max(0,predictionSeconds);maxPredictionDistance=Mathf.Max(0,maxPredictionDistance);
            gpuUploadBudgetMilliseconds=Mathf.Max(.1f,gpuUploadBudgetMilliseconds);uploadBytesPerFrame=Mathf.Max(262144,uploadBytesPerFrame);maxUploadSubmissions=Mathf.Max(1,maxUploadSubmissions);
            cacheRetentionSeconds=Mathf.Max(0,cacheRetentionSeconds);cacheHighWatermark=Mathf.Clamp(cacheHighWatermark,.1f,1);cacheLowWatermark=Mathf.Clamp(cacheLowWatermark,.1f,cacheHighWatermark);
            if(priorityWeights==null)priorityWeights=new GaussianPriorityWeights();
        }
        public void Load()
        {
            Unload();Metrics=new GaussianStreamingMetrics();unloaded=false;LastError=null;Status="Loading metadata";
            try {
                string path=ResolveJsonPath();bool cached=preferRuntimeCache;
                metadataCancellation=new System.Threading.CancellationTokenSource();
                if(path.Contains("://")){
                    datasetSource=new GaussianDatasetSource(path,Path.Combine(Application.persistentDataPath,"GaussianSplatLOD"),cached);
                    metadataJob=datasetSource.OpenAsync(metadataCancellation.Token);
                }else metadataJob=Task.Run(()=>new GaussianLODLoader(path,cached),metadataCancellation.Token);
            }
            catch(Exception e){ReportError(e.Message);Status="Failed";}
        }
        public void Reload()=>Load();
        // Starting points for profiling on devices, not verified device performance promises.
        public void ApplyMobilePreset(bool pico)
        {
            maxVisibleSplats=pico?1000000:200000;
            maxGPUMemory=pico?640:256;maxCPUCache=pico?384:192;maxResidentSplats=pico?2000000:700000;
            loadChunksPerFrame=2;maxConcurrentLoads=2;maxConcurrentIO=2;maxConcurrentDecodes=1;maxConcurrentUploads=1;
            uploadSplatsPerFrame=16384;uploadBytesPerFrame=4*1048576;gpuUploadBudgetMilliseconds=.75f;
            enableAsyncLoading=true;preferRuntimeCache=true;coherentLOD=true;enablePrediction=true;
            preloadDistance=20;predictionSeconds=1;maxPredictionDistance=60;teleportDistance=20;
            lodHysteresis=.25f;lodTransitionSeconds=.4f;nearQualityDistance=40;
            cacheRetentionSeconds=10;cacheHighWatermark=.9f;cacheLowWatermark=.8f;
            enablePerformanceScheduling=pico;targetRefreshRate=90;
        }
        public void Unload()
        {
            unloaded=true;metadataCancellation?.Cancel();datasetSource?.Dispose();datasetSource=null;metadataCancellation?.Dispose();metadataCancellation=null;session?.Dispose();session=null;
            if(metadataJob!=null){_=metadataJob.ContinueWith(t=>{if(t.IsFaulted){var ignored=t.Exception;}},TaskScheduler.Default);metadataJob=null;}
            foreach(var item in builtinCommands){if(item.Key!=null)item.Key.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,item.Value);item.Value.Dispose();}
            builtinCommands.Clear();descriptors=null;sortKnown=false;lastSortCamera=null;drawGPU=null;lastSignature=0;lastCount=0;VisibleSplats=VisibleChunks=0;Status="Unloaded";FPS=0;lastTick=-1;
        }
        public void ReportError(string text) {if(LastError==text)return;LastError=text;Debug.LogError("Gaussian LOD: "+text,this);}
        void Update()
        {
            if(datasetSource!=null)datasetSource.MaxConcurrentTransfers=maxConcurrentIO;datasetSource?.Tick();Metrics.FileTransfers=datasetSource?.FileTransfers??0;GaussianGPUResources.CollectRetired();
            Metrics.SampleFrame(enablePerformanceScheduling,targetRefreshRate,gpuUploadBudgetMilliseconds,CPUBytes,maxCPUCache*1048576L,GPUBytes,maxGPUMemory*1048576L);
            FPS=Metrics.FPS;
            Tick(targetCamera!=null?targetCamera:(Application.isPlaying?Camera.main:EditorCameraProvider?.Invoke()));
        }
        public void Tick(Camera camera)
        {
            if(unloaded)return;
            datasetSource?.Tick();
            double now=Time.realtimeSinceStartupAsDouble;if(now-lastTick<.004)return;lastTick=now;
            try
            {
                if(metadataJob!=null&&metadataJob.IsCompleted)
                {
                    var job=metadataJob;metadataJob=null;
                    if(job.IsFaulted)throw job.Exception.GetBaseException();
                    session=new GaussianStreamingSession(this,job.Result);session.InitializeCandidates();
                }
                if(session==null)return;
                long tickStarted=System.Diagnostics.Stopwatch.GetTimestamp();session.Tick(camera,now);LastUploadMilliseconds=(float)((System.Diagnostics.Stopwatch.GetTimestamp()-tickStarted)*1000.0/System.Diagnostics.Stopwatch.Frequency);Metrics.SchedulingMilliseconds=Math.Max(0,LastUploadMilliseconds-Metrics.UploadMilliseconds);
                Status=!CoverageReady?"Preparing complete coarse model":PendingLoads>0?"Streaming":BudgetLimited?"Budget limited":"Ready";if(tickFailed){LastError=null;tickFailed=false;}
            }
            catch(Exception e){tickFailed=true;Status="Error";ReportError(e.Message);}
        }
        void OnBuiltinCamera(Camera camera)
        {
            if(GraphicsSettings.currentRenderPipeline!=null)return;
            if(camera.cameraType==CameraType.Preview||camera.cameraType==CameraType.Reflection)return;
            if(!builtinCommands.TryGetValue(camera,out var cmd)) {
                cmd=new CommandBuffer{name="Gaussian LOD: sort + blend"};builtinCommands.Add(camera,cmd);camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha,cmd);
            }
            cmd.Clear();Record(camera,cmd);
        }
        public void Record(Camera camera,CommandBuffer cmd)=>Record(camera,cmd,GaussianViewContext.FromCamera(camera));
        internal void Record(Camera camera,CommandBuffer cmd,GaussianViewContext views)
        {
            if(!isActiveAndEnabled||!enableRendering||unloaded||camera.cameraType==CameraType.Preview||camera.cameraType==CameraType.Reflection)return;
            if((camera.cullingMask&(1<<gameObject.layer))==0)return;
            if(targetCamera==null||targetCamera==camera)Tick(camera);
            if(session?.GPU==null||session.WaitingForGPU)return;
            var gpu=session.GPU;if(drawGPU!=gpu){drawGPU=gpu;lastSignature=0;lastCount=0;}
            var segments=session.VisibleSegments(camera,views);
            int count=0,end=-1,chunks=0;ulong signature=1469598103934665603;
            foreach(var s in segments) {
                int start=Math.Max(end,s.Slot),high=s.Slot+s.Count,n=Math.Max(0,high-start);
                if(n>0){signature=unchecked((signature^(ulong)(start*397L+n*17L+s.PackedLevel))*1099511628211);count+=n;chunks++;}end=Math.Max(end,high);
            }
            // Session supplies a whole-region cut, including transition reserve. Never truncate a
            // cut by GPU slot order: doing so removes arbitrary spatial regions during LOD changes.
            if(count>gpu.VisibleCapacity){ReportError("完整 LOD 绘制集合超过排序容量，请增加可见点预算。");return;}
            VisibleSplats=count;VisibleChunks=chunks;if(count==0)return;
            bool changed=signature!=lastSignature||count!=lastCount;
            if(changed)
            {
                if(descriptors==null||descriptors.Length<segments.Count)descriptors=new GaussianVisibleSegment[Math.Max(16,segments.Count*2)];
                int at=0,written=0;end=-1;
                foreach(var segment in segments){int start=Math.Max(end,segment.Slot),high=segment.Slot+segment.Count,n=Math.Max(0,high-start);if(n>0){descriptors[at++]=new GaussianVisibleSegment{Slot=(uint)start,Count=(uint)n,PackedLevel=segment.PackedLevel,OutputStart=(uint)written};written+=n;}end=Math.Max(end,high);}
                gpu.BuildVisible(cmd,descriptors,at,count);lastSignature=signature;lastCount=count;
            }
            var model=ModelMatrix;var view=views.View[0];
            cmd.SetBufferData(gpu.Fades,session.Fades);
            // Relative depth order is unchanged by pure camera translation with a fixed view axis.
            bool orientationChanged=!sortKnown||!SameAxes(lastSortView,view)||lastSortModel!=model||lastSortCamera!=camera;
            bool otherChanged=views.RequiresPerEyeSort&&(!sortKnown||!SameAxes(lastSortOtherView,views.View[1]));
            if(changed||orientationChanged||otherChanged||lastSortSeparate!=views.RequiresPerEyeSort){
                cmd.BeginSample("Gaussian LOD depth sort");gpu.Sort(cmd,view*model,count);
                if(views.RequiresPerEyeSort)gpu.Sort(cmd,views.View[1]*model,count,1);
                cmd.EndSample("Gaussian LOD depth sort");lastSortModel=model;lastSortView=view;lastSortOtherView=views.View[Math.Min(1,views.ViewCount-1)];lastSortCamera=camera;sortKnown=true;lastSortSeparate=views.RequiresPerEyeSort;
            }
            properties.Clear();properties.SetBuffer("_Splats",gpu.Splats);properties.SetBuffer("_Visible",gpu.Visible);properties.SetBuffer("_Order",gpu.GetOrder(views.RequiresPerEyeSort&&!views.SinglePass?views.DrawEye:0));properties.SetBuffer("_OrderRight",gpu.GetOrder(views.RequiresPerEyeSort?1:0));properties.SetBuffer("_Fades",gpu.Fades);
            properties.SetMatrix("_Model",model);views.ApplyTo(properties,model);
            properties.SetFloat("_RenderScale",Mathf.Max(.001f,renderScale));properties.SetFloat("_OpacityMultiplier",Mathf.Max(0,opacityMultiplier));properties.SetInt("_SHDegree",Metadata.shDegree);properties.SetInt("_DebugLOD",showLODLevel?1:0);properties.SetInt("_Orthographic",camera.orthographic?1:0);
            cmd.BeginSample("Gaussian LOD raster");cmd.DrawProcedural(Matrix4x4.identity,gpu.Material,0,MeshTopology.Triangles,6,count,properties);cmd.EndSample("Gaussian LOD raster");gpu.MarkSubmitted(cmd);

        }
        static bool SameAxes(Matrix4x4 a,Matrix4x4 b)
        {
            for(int row=0;row<3;row++)for(int column=0;column<3;column++)if(Mathf.Abs(a[row,column]-b[row,column])>1e-6f)return false;
            return true;
        }
        public int[] GetCurrentLODLevels() => session?.CurrentLevels;
        void OnDrawGizmos()
        {
            if(!showChunkBounds||Metadata==null)return;
            Matrix4x4 old=Gizmos.matrix;Gizmos.matrix=ModelMatrix;
            for(int n=0;n<Metadata.tree.Length;n++){
                int l=session.CurrentLevels[n];Gizmos.color=l<0?Color.gray:Color.HSVToRGB((l*.17f)%1, .8f,1);
                var b=Metadata.tree[n].bound.Bounds;Gizmos.DrawWireCube(b.center,b.size);
            }
            Gizmos.matrix=old;
        }
        void OnDrawGizmosSelected(){if(showChunkBounds||Metadata==null)return;Matrix4x4 old=Gizmos.matrix;Gizmos.matrix=ModelMatrix;Gizmos.color=Color.cyan;var b=Metadata.forwardBox.Bounds;Gizmos.DrawWireCube(b.center,b.size);Gizmos.matrix=old;}
        void OnGUI()
        {
            if(!Application.isPlaying||(!showStatistics&&!showLoadingStatus))return;
            int row=Math.Abs(GetInstanceID()%5);GUILayout.BeginArea(new Rect(10,10+row*110,520,110),GUI.skin.box);
            GUILayout.Label(name+" — "+Status);
            if(showStatistics)GUILayout.Label($"{FPS:F1} app FPS | {VisibleSplats:N0} splats / {VisibleChunks} chunks | GPU {GPUBytes/1048576f:F1} MiB | CPU {CPUBytes/1048576f:F1} MiB\nCache {CacheHitRate:P1} | reads {DiskReads} | tick {LastUploadMilliseconds:F2} ms");
            if(showLoadingStatus)GUILayout.Label($"Pending {PendingLoads} | resident ranges {ResidentRanges}"+(BudgetLimited?" | Budget limited":""));
            GUILayout.EndArea();
        }
    }
}
