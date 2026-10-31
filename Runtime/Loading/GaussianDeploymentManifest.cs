using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace GaussianSplatLOD
{
    /// <summary>Optional deployment index. Aholo lod-meta.json and GPU record ABI are unchanged.</summary>
    [Serializable] public sealed class GaussianDeploymentManifest
    {
        public const string FileName = "deployment-index.json";
        public int version = 1, recordStride = GaussianSplat.Stride;
        public string datasetId, jsonName, jsonSha256;
        public FileEntry[] files;
        [Serializable] public sealed class FileEntry
        {
            public int file;
            public string source, artifact, sha256;
            public long length, recordCount;
            public int shDegree;
            public RangeEntry[] ranges;
        }
        [Serializable] public sealed class RangeEntry
        {
            public int node, lod, count;
            public long offset, byteOffset;
            public GaussianBox bound;
        }
        public void Validate(GaussianLODMetadata metadata,string jsonPath)
        {
            if(version!=1||recordStride!=GaussianSplat.Stride||!IsHash(datasetId)||!IsHash(jsonSha256))throw new InvalidDataException("部署索引 version/stride/hash 无效。");
            if(jsonName!=Path.GetFileName(jsonPath)||files==null||files.Length!=metadata.files.Length)throw new InvalidDataException("部署索引不匹配 Aholo JSON。");
            if(!string.Equals(HashFile(jsonPath,CancellationToken.None),jsonSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("部署 JSON 校验失败。");
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<files.Length;i++)
            {
                var f=files[i];
                if(f==null||f.file!=i||f.source!=metadata.files[i]||f.shDegree!=metadata.shDegree||f.recordCount<0||f.length<1||!IsHash(f.sha256))throw new InvalidDataException("部署文件索引无效: "+i);
                bool cache=f.artifact==f.source+GaussianRuntimeCache.Extension;
                if((f.artifact!=f.source&&!cache)||!paths.Add(f.artifact))throw new InvalidDataException("部署 artifact 必须为原 PLY 或对应 .gsbin。");
                SafePath(Path.GetDirectoryName(jsonPath),f.artifact);
                if(cache&&(f.recordCount>(long.MaxValue-GaussianRuntimeCache.HeaderSize)/GaussianSplat.Stride||f.length!=GaussianRuntimeCache.HeaderSize+f.recordCount*GaussianSplat.Stride))throw new InvalidDataException("部署缓存长度与记录数不匹配。");
            }
        }
        public static GaussianDeploymentManifest Read(string path)
        {
            if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException("部署索引大于 64 MiB。");
            return JsonConvert.DeserializeObject<GaussianDeploymentManifest>(File.ReadAllText(path))??throw new InvalidDataException("部署索引为空。");
        }
        public static bool IsHash(string hash)
        {
            if(hash==null||hash.Length!=64)return false;
            foreach(char c in hash)if(!Uri.IsHexDigit(c))return false;
            return true;
        }
        public static string SafePath(string directory,string relative)
        {
            if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.Contains(":")||relative.Contains("\\")||Array.IndexOf(relative.Split('/'),"..")>=0)throw new InvalidDataException("部署路径超出模型目录。");
            string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string path=Path.GetFullPath(Path.Combine(root,relative));
            if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("部署路径超出模型目录。");
            return path;
        }
        public static string HashFile(string path,CancellationToken cancellation)
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan);
            using var hash=SHA256.Create();byte[] buffer=ArrayPool<byte>.Shared.Rent(65536);
            try{int n;while((n=stream.Read(buffer,0,buffer.Length))!=0){cancellation.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,n,null,0);}hash.TransformFinalBlock(Array.Empty<byte>(),0,0);return Hex(hash.Hash);}
            finally{ArrayPool<byte>.Shared.Return(buffer);}
        }
        internal static async Task<string> HashFileAsync(string path,CancellationToken cancellation)
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
            using var hash=SHA256.Create();byte[] buffer=ArrayPool<byte>.Shared.Rent(65536);
            try{int n;while((n=await stream.ReadAsync(buffer,0,buffer.Length,cancellation).ConfigureAwait(false))!=0){cancellation.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,n,null,0);}hash.TransformFinalBlock(Array.Empty<byte>(),0,0);return Hex(hash.Hash);}
            finally{ArrayPool<byte>.Shared.Return(buffer);}
        }
        public static string HashText(string value){using var sha=SHA256.Create();return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));}
        static string Hex(byte[] value)=>BitConverter.ToString(value).Replace("-","").ToLowerInvariant();
    }
}
