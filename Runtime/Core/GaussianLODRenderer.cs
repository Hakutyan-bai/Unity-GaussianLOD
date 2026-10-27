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
        [Header("Debug Settings")]
        public bool showChunkBounds,showLODLevel,showStatistics=true,showLoadingStatus=true;
        [SerializeField,HideInInspector] Shader gaussianShader;
        [SerializeField,HideInInspector] ComputeShader sortingShader;

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
        public float CacheHitRate => session==null?0:(float)session.CacheHits/Math.Max(1,session.CacheHits+session.DiskReads);
        public GaussianLODMetadata Metadata => session?.Loader.Metadata;
        public string DataDirectory {get {try{return Path.GetDirectoryName(ResolveJsonPath());}catch{return "";}}}
        public Matrix4x4 ModelMatrix => transform.localToWorldMatrix*Matrix4x4.TRS(positionOffset,Quaternion.Euler(rotationOffset),Vector3.one*coordinateScale);
        public static Func<Camera> EditorCameraProvider;
        internal static readonly HashSet<GaussianLODRenderer> Instances=new HashSet<GaussianLODRenderer>();
        GaussianStreamingSession session;
        GaussianGPUResources drawGPU;
        Task<GaussianLODLoader> metadataJob;
        readonly Dictionary<Camera,CommandBuffer> builtinCommands=new Dictionary<Camera,CommandBuffer>();
        MaterialPropertyBlock properties;
        struct VisibleRef {public uint slot,level;}
        VisibleRef[] refs;
        ulong lastSignature;
        int lastCount;
        double lastTick=-1,lastDraw;
        bool unloaded=true;
        bool tickFailed;
        public string ResolveJsonPath()
        {
            if(string.IsNullOrWhiteSpace(jsonPath))throw new IOException("请先选择 lod-meta.json。");
            if(dataLocation==GaussianDataLocation.ExternalDirectory)return Path.GetFullPath(jsonPath);
            if(Path.IsPathRooted(jsonPath)||jsonPath.Contains(":")||Array.IndexOf(jsonPath.Replace('\\','/').Split('/'),"..")>=0)throw new IOException("StreamingAssets 模式需要相对 JSON 路径。");
            if(dataLocation==GaussianDataLocation.StreamingAssets && Application.streamingAssetsPath.Contains("://"))
                throw new IOException("Android APK 的 StreamingAssets 不是可随机读取目录。请先将模型部署到 Application.persistentDataPath，并选择 PersistentDataDirectory 模式；不要将数 GB PLY 作为 TextAsset。");
            string root=dataLocation==GaussianDataLocation.PersistentDataDirectory?Application.persistentDataPath:Application.streamingAssetsPath;
            return Path.GetFullPath(Path.Combine(root,jsonPath));
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
        }
        public void Load()
        {
            Unload();unloaded=false;LastError=null;Status="Loading metadata";
            try {string path=ResolveJsonPath();bool cached=preferRuntimeCache;metadataJob=Task.Run(()=>new GaussianLODLoader(path,cached));}
            catch(Exception e){ReportError(e.Message);Status="Failed";}
        }
        public void Reload()=>Load();
        // Starting points for profiling on devices, not verified device performance promises.
        public void ApplyMobilePreset(bool pico)
        {
            maxVisibleSplats=pico?350000:200000;
            maxGPUMemory=pico?384:256;maxCPUCache=pico?256:192;
            loadChunksPerFrame=2;maxConcurrentLoads=2;uploadSplatsPerFrame=8192;
            enableAsyncLoading=true;preferRuntimeCache=true;coherentLOD=true;
            preloadDistance=10;lodHysteresis=.25f;lodTransitionSeconds=.3f;
        }
        public void Unload()
        {
            unloaded=true;session?.Dispose();session=null;
            if(metadataJob!=null){_=metadataJob.ContinueWith(t=>{if(t.IsFaulted){var ignored=t.Exception;}},TaskScheduler.Default);metadataJob=null;}
            foreach(var item in builtinCommands){if(item.Key!=null)item.Key.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,item.Value);item.Value.Dispose();}
            builtinCommands.Clear();refs=null;drawGPU=null;lastSignature=0;lastCount=0;VisibleSplats=VisibleChunks=0;Status="Unloaded";FPS=0;lastDraw=0;lastTick=-1;
        }
        public void ReportError(string text) {if(LastError==text)return;LastError=text;Debug.LogError("Gaussian LOD: "+text,this);}
        void Update()=>Tick(targetCamera!=null?targetCamera:(Application.isPlaying?Camera.main:EditorCameraProvider?.Invoke()));
        public void Tick(Camera camera)
        {
            if(unloaded)return;
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
                var watch=System.Diagnostics.Stopwatch.StartNew();session.Tick(camera,now);LastUploadMilliseconds=(float)watch.Elapsed.TotalMilliseconds;
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
        public void Record(Camera camera,CommandBuffer cmd)
        {
            if(!isActiveAndEnabled||!enableRendering||unloaded||camera.cameraType==CameraType.Preview||camera.cameraType==CameraType.Reflection)return;
            if((camera.cullingMask&(1<<gameObject.layer))==0)return;
            if(targetCamera==null||targetCamera==camera)Tick(camera);
            if(session?.GPU==null)return;
            var gpu=session.GPU;if(drawGPU!=gpu){drawGPU=gpu;lastSignature=0;lastCount=0;}
            var segments=session.VisibleSegments(camera);
            int count=0,end=-1,chunks=0;ulong signature=1469598103934665603;
            foreach(var s in segments) {
                int start=Math.Max(end,s.Slot),high=s.Slot+s.Count,n=Math.Max(0,high-start);
                if(n>0){signature=unchecked((signature^(ulong)(start*397L+n*17L+s.PackedLevel))*1099511628211);count+=n;chunks++;}end=Math.Max(end,high);
            }
            // Session supplies a whole-region cut, including transition reserve. Never truncate a
            // cut by GPU slot order: doing so removes arbitrary spatial regions during LOD changes.
            if(count>gpu.VisibleCapacity){ReportError("完整 LOD 绘制集合超过排序容量，请增加可见点预算。");return;}
            VisibleSplats=count;VisibleChunks=chunks;if(count==0)return;
            if(refs==null||refs.Length!=gpu.VisibleCapacity){refs=new VisibleRef[gpu.VisibleCapacity];lastSignature=0;}
            if(signature!=lastSignature||count!=lastCount)
            {
                int at=0;end=-1;
                foreach(var s in segments){int start=Math.Max(end,s.Slot),high=s.Slot+s.Count;for(int i=start;i<high;i++)refs[at++]=new VisibleRef{slot=(uint)i,level=s.PackedLevel};end=Math.Max(end,high);}
                gpu.Visible.SetData(refs,0,0,count);lastSignature=signature;lastCount=count;
            }
            var model=ModelMatrix;var view=camera.worldToCameraMatrix;
            // Only two floats per spatial region change during fades; the million-point visible
            // index array stays intact throughout a transition.
            gpu.Fades.SetData(session.Fades);
            cmd.BeginSample("Gaussian LOD sort and raster");gpu.Sort(cmd,view*model,count);
            properties.Clear();properties.SetBuffer("_Splats",gpu.Splats);properties.SetBuffer("_Visible",gpu.Visible);properties.SetBuffer("_Order",gpu.PairsA);properties.SetBuffer("_Fades",gpu.Fades);
            properties.SetMatrix("_Model",model);properties.SetMatrix("_View",view);properties.SetInt("_UseCustomProjection",0);
            properties.SetVector("_Viewport",new Vector4(camera.pixelWidth,camera.pixelHeight,0,0));
            Vector3 local=model.inverse.MultiplyPoint3x4(camera.transform.position);properties.SetVector("_LocalCamera",new Vector4(local.x,local.y,local.z,1));
            properties.SetFloat("_RenderScale",Mathf.Max(.001f,renderScale));properties.SetFloat("_OpacityMultiplier",Mathf.Max(0,opacityMultiplier));properties.SetInt("_SHDegree",Metadata.shDegree);properties.SetInt("_DebugLOD",showLODLevel?1:0);properties.SetInt("_Orthographic",camera.orthographic?1:0);
            cmd.DrawProcedural(Matrix4x4.identity,gpu.Material,0,MeshTopology.Triangles,6,count,properties);cmd.EndSample("Gaussian LOD sort and raster");
            double now=Time.realtimeSinceStartupAsDouble;if(lastDraw>0&&now-lastDraw>.001){float fps=(float)(1/(now-lastDraw));FPS=FPS==0?fps:Mathf.Lerp(FPS,fps,.1f);}lastDraw=now;
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
            if(showStatistics)GUILayout.Label($"{FPS:F1} camera FPS | {VisibleSplats:N0} splats / {VisibleChunks} chunks | GPU {GPUBytes/1048576f:F1} MiB | CPU {CPUBytes/1048576f:F1} MiB\nCache {CacheHitRate:P1} | reads {DiskReads} | tick {LastUploadMilliseconds:F2} ms");
            if(showLoadingStatus)GUILayout.Label($"Pending {PendingLoads} | resident ranges {ResidentRanges}"+(BudgetLimited?" | Budget limited":""));
            GUILayout.EndArea();
        }
    }
}
