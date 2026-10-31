using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GaussianSplatLOD
{
    /// <summary>Shared metadata/headers, true asynchronous range IO and independent bounded CPU decoding.</summary>
    public sealed class GaussianLODLoader
    {
        public readonly GaussianLODMetadata Metadata;
        public readonly string Directory;
        readonly ConcurrentDictionary<int,Lazy<GaussianPLYHeader>> headers=new ConcurrentDictionary<int,Lazy<GaussianPLYHeader>>();
        readonly ConcurrentDictionary<int,Lazy<GaussianRuntimeCache.Header>> cacheHeaders=new ConcurrentDictionary<int,Lazy<GaussianRuntimeCache.Header>>();
        readonly ConcurrentDictionary<int,Lazy<Task>> verified=new ConcurrentDictionary<int,Lazy<Task>>();
        readonly GaussianReadPipeline pipeline=new GaussianReadPipeline();
        readonly bool preferRuntimeCache;
        readonly Func<int,CancellationToken,Task> ensureFile;
        readonly GaussianDeploymentManifest deployment;
        long runtimeCacheReads,plyReads;
        public long RuntimeCacheReads=>Interlocked.Read(ref runtimeCacheReads);
        public long PLYReads=>Interlocked.Read(ref plyReads);
        public int ActiveReads=>pipeline.ActiveReads;
        public int ActiveParses=>pipeline.ActiveParses;
        public long IOBytes=>pipeline.IOBytes;
        public long StagingBytes=>pipeline.StagingBytes;
        public double IOReadMilliseconds=>pipeline.IOReadMilliseconds;
        public double DecodeMilliseconds=>pipeline.DecodeMilliseconds;
        public bool HasDeploymentIndex=>deployment!=null;
        public bool RequiresAsyncFileGate=>ensureFile!=null;
        public GaussianLODLoader(string jsonPath,bool preferRuntimeCache=true):this(jsonPath,preferRuntimeCache,false,null){}
        internal GaussianLODLoader(string jsonPath,bool preferRuntimeCache,bool deferredFileValidation,Func<int,CancellationToken,Task> ensureFile)
        {
            this.preferRuntimeCache=preferRuntimeCache;this.ensureFile=ensureFile;
            var path=Path.GetFullPath(jsonPath);
            if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException("JSON 大于 64 MiB。");
            Metadata=GaussianLODMetadata.Parse(File.ReadAllText(path));Directory=Path.GetDirectoryName(path);
            string index=Path.Combine(Directory,GaussianDeploymentManifest.FileName);
            if(preferRuntimeCache&&File.Exists(index)){deployment=GaussianDeploymentManifest.Read(index);deployment.Validate(Metadata,path);}
            // Existing public constructor keeps strict validation; APK staging uses the internal gate.
            if(!deferredFileValidation)for(int i=0;i<Metadata.files.Length;i++)
                if(!File.Exists(FilePath(i))&&(!preferRuntimeCache||!File.Exists(FilePath(i)+GaussianRuntimeCache.Extension)))throw new FileNotFoundException("JSON 引用的 PLY 或预解码缓存不存在",FilePath(i));
        }
        public void ConfigureConcurrency(int io,int decode)=>pipeline.Configure(io,decode);
        public string FilePath(int file)
        {
            if(file<0||file>=Metadata.files.Length)throw new InvalidDataException("PLY file 索引越界。");
            return GaussianDeploymentManifest.SafePath(Directory,Metadata.files[file]);
        }
        bool IndexedCache(int file)=>deployment!=null&&deployment.files[file].artifact==Metadata.files[file]+GaussianRuntimeCache.Extension;
        GaussianRuntimeCache.Header CacheHeader(int file)
        {
            return cacheHeaders.GetOrAdd(file,i=>new Lazy<GaussianRuntimeCache.Header>(()=>{
                string path=FilePath(i);
                try{return GaussianRuntimeCache.Validate(path+GaussianRuntimeCache.Extension,IndexedCache(i)?null:File.Exists(path)?path:null,Metadata.shDegree);}
                catch(IOException){return null;}catch(InvalidDataException){return null;}catch(UnauthorizedAccessException){return null;}
            })).Value;
        }
        public GaussianPLYHeader Header(int file)=>headers.GetOrAdd(file,i=>new Lazy<GaussianPLYHeader>(()=>{
            string path=FilePath(i);
            if(File.Exists(path))return GaussianPLYReader.ReadHeader(path);
            var h=GaussianRuntimeCache.Validate(path+GaussianRuntimeCache.Extension,null,Metadata.shDegree);
            return new GaussianPLYHeader{VertexCount=h.Count,SHDegree=h.SHDegree};
        })).Value;
        Task VerifyIndexedCacheAsync(int file)
        {
            if(!IndexedCache(file)||ensureFile!=null)return Task.CompletedTask; // The staging gate already checks the content hash.
            return verified.GetOrAdd(file,i=>new Lazy<Task>(()=>Task.Run(async()=>{
                var entry=deployment.files[i];string path=GaussianDeploymentManifest.SafePath(Directory,entry.artifact);
                if(new FileInfo(path).Length!=entry.length||!string.Equals(await GaussianDeploymentManifest.HashFileAsync(path,CancellationToken.None).ConfigureAwait(false),entry.sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("运行时缓存内容校验失败: "+entry.artifact);
            }))).Value;
        }
        public GaussianSplat[] Read(GaussianRangeKey key,CancellationToken cancellation)
        {
            if(ensureFile!=null)throw new InvalidOperationException("按需部署的数据请使用 ReadAsync，避免阻塞主线程的 UnityWebRequest。");
            string path=FilePath(key.File);
            if(preferRuntimeCache)
            {
                var cached=CacheHeader(key.File);
                if(cached!=null)
                {
                    try{VerifyIndexedCacheAsync(key.File).GetAwaiter().GetResult();var result=GaussianRuntimeCache.ReadRange(path+GaussianRuntimeCache.Extension,cached,key.Offset,key.Count,cancellation);Interlocked.Increment(ref runtimeCacheReads);return result;}
                    catch(IOException){cacheHeaders[key.File]=new Lazy<GaussianRuntimeCache.Header>(()=>null);}
                    catch(InvalidDataException){cacheHeaders[key.File]=new Lazy<GaussianRuntimeCache.Header>(()=>null);}
                }
            }
            var decoded=GaussianPLYReader.ReadRange(path,Header(key.File),key.Offset,key.Count,Metadata.shDegree,cancellation);Interlocked.Increment(ref plyReads);return decoded;
        }
        public Task<GaussianSplat[]> ReadAsync(GaussianRangeKey key,CancellationToken cancellation)
        {
            // The initial worker also guarantees cached header/array setup never runs on Unity's context.
            return Task.Run(()=>ReadAsyncCore(key,cancellation),cancellation);
        }
        async Task<GaussianSplat[]> ReadAsyncCore(GaussianRangeKey key,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(ensureFile!=null)await ensureFile(key.File,cancellation).ConfigureAwait(false);
            string path=FilePath(key.File);
            if(preferRuntimeCache)
            {
                var cached=CacheHeader(key.File);
                if(cached!=null)
                {
                    try
                    {
                        await VerifyIndexedCacheAsync(key.File).ConfigureAwait(false);cancellation.ThrowIfCancellationRequested();
                        var result=await GaussianRuntimeCache.ReadRangeAsync(path+GaussianRuntimeCache.Extension,cached,key.Offset,key.Count,pipeline,cancellation).ConfigureAwait(false);
                        Interlocked.Increment(ref runtimeCacheReads);return result;
                    }
                    catch(IOException){cacheHeaders[key.File]=new Lazy<GaussianRuntimeCache.Header>(()=>null);}
                    catch(InvalidDataException){cacheHeaders[key.File]=new Lazy<GaussianRuntimeCache.Header>(()=>null);}
                }
            }
            var decoded=await GaussianPLYReader.ReadRangeAsync(path,Header(key.File),key.Offset,key.Count,Metadata.shDegree,pipeline,cancellation).ConfigureAwait(false);
            Interlocked.Increment(ref plyReads);return decoded;
        }
        public void ValidateAllRanges()
        {
            foreach(var node in Metadata.tree)foreach(var r in node.lods)
            {
                var h=Header(r.file);
                if(h.SHDegree!=Metadata.shDegree||r.offset>h.VertexCount-r.count)throw new InvalidDataException("JSON/PLY 不匹配: "+r.Key);
            }
        }
    }
}
