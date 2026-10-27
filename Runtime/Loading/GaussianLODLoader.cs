using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GaussianSplatLOD
{
    /// <summary>One metadata parse per load, one shared header per file, bounded random-access record reads.</summary>
    public sealed class GaussianLODLoader
    {
        public readonly GaussianLODMetadata Metadata;
        public readonly string Directory;
        readonly ConcurrentDictionary<int,Lazy<GaussianPLYHeader>> headers = new ConcurrentDictionary<int,Lazy<GaussianPLYHeader>>();
        readonly ConcurrentDictionary<int,Lazy<GaussianRuntimeCache.Header>> cacheHeaders = new ConcurrentDictionary<int,Lazy<GaussianRuntimeCache.Header>>();
        readonly bool preferRuntimeCache;
        long runtimeCacheReads, plyReads;
        public long RuntimeCacheReads => Interlocked.Read(ref runtimeCacheReads);
        public long PLYReads => Interlocked.Read(ref plyReads);
        public GaussianLODLoader(string jsonPath, bool preferRuntimeCache = true)
        {
            this.preferRuntimeCache = preferRuntimeCache;
            var path=Path.GetFullPath(jsonPath);
            if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException("JSON 大于 64 MiB。");
            Metadata=GaussianLODMetadata.Parse(File.ReadAllText(path)); Directory=Path.GetDirectoryName(path);
            // File existence validation does not read any point payloads.
            for(int i=0;i<Metadata.files.Length;i++) if(!File.Exists(FilePath(i))&&(!preferRuntimeCache||!File.Exists(FilePath(i)+GaussianRuntimeCache.Extension)))throw new FileNotFoundException("JSON 引用的 PLY 或预解码缓存不存在",FilePath(i));
        }
        public string FilePath(int file)
        {
            string root=Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string path=Path.GetFullPath(Path.Combine(root,Metadata.files[file]));
            if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("PLY 路径超出模型目录。");
            return path;
        }
        public GaussianPLYHeader Header(int file) => headers.GetOrAdd(file,i=>new Lazy<GaussianPLYHeader>(()=>{
            string p=FilePath(i);
            if(File.Exists(p))return GaussianPLYReader.ReadHeader(p);
            var h=GaussianRuntimeCache.Validate(p+GaussianRuntimeCache.Extension,null,Metadata.shDegree);
            return new GaussianPLYHeader{VertexCount=h.Count,SHDegree=h.SHDegree};
        })).Value;
        public GaussianSplat[] Read(GaussianRangeKey key,CancellationToken cancellation)
        {
            string path = FilePath(key.File);
            if (preferRuntimeCache)
            {
                var cached = cacheHeaders.GetOrAdd(key.File, i => new Lazy<GaussianRuntimeCache.Header>(() => {
                    try { return GaussianRuntimeCache.Validate(path + GaussianRuntimeCache.Extension, File.Exists(path)?path:null, Metadata.shDegree); }
                    catch (IOException) { return null; }
                    catch (InvalidDataException) { return null; }
                    catch (UnauthorizedAccessException) { return null; }
                })).Value;
                if (cached != null)
                {
                    try {
                        var result = GaussianRuntimeCache.ReadRange(path + GaussianRuntimeCache.Extension, cached, key.Offset, key.Count, cancellation);
                        Interlocked.Increment(ref runtimeCacheReads); return result;
                    } catch (IOException) { cacheHeaders[key.File] = new Lazy<GaussianRuntimeCache.Header>(() => null); }
                    catch (InvalidDataException) { cacheHeaders[key.File] = new Lazy<GaussianRuntimeCache.Header>(() => null); }
                }
            }
            var decoded = GaussianPLYReader.ReadRange(path,Header(key.File),key.Offset,key.Count,Metadata.shDegree,cancellation);
            Interlocked.Increment(ref plyReads); return decoded;
        }
        public Task<GaussianSplat[]> ReadAsync(GaussianRangeKey key,CancellationToken cancellation) => Task.Run(()=>Read(key,cancellation),cancellation);
        public void ValidateAllRanges()
        {
            foreach(var node in Metadata.tree) foreach(var r in node.lods) {
                var h=Header(r.file);
                if(h.SHDegree!=Metadata.shDegree || r.offset>h.VertexCount-r.count)throw new InvalidDataException("JSON/PLY 不匹配: "+r.Key);
            }
        }
    }
}
