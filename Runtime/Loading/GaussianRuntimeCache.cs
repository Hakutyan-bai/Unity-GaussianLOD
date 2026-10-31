using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GaussianSplatLOD
{
    /// <summary>Lossless, little-endian GPU ABI cache. Optional sidecar; the Aholo PLY stays authoritative.</summary>
    public static class GaussianRuntimeCache
    {
        public const string Extension = ".gsbin";
        public const int HeaderSize = 48;
        const int Version = 1;
        const string Magic = "GLDCACHE";
        public sealed class Header
        {
            public long Count, SourceLength, SourceTicks;
            public int SHDegree;
        }
        public static Header Validate(string cachePath, string sourcePath, int degree)
        {
            if (!BitConverter.IsLittleEndian) throw new NotSupportedException("Runtime cache requires a little-endian CPU.");
            using var f = File.OpenRead(cachePath);
            using var reader = new BinaryReader(f, Encoding.ASCII, true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic || reader.ReadInt32() != Version || reader.ReadInt32() != GaussianSplat.Stride)
                throw new InvalidDataException("Runtime cache magic/version/stride mismatch.");
            int sh = reader.ReadInt32();
            if (reader.ReadInt32() != 0) throw new InvalidDataException("Runtime cache flags mismatch.");
            var h = new Header { SHDegree = sh, Count = reader.ReadInt64(), SourceLength = reader.ReadInt64(), SourceTicks = reader.ReadInt64() };
            if (sh != degree || h.Count < 0 || h.Count > (long.MaxValue - HeaderSize) / GaussianSplat.Stride ||
                f.Length != HeaderSize + h.Count * GaussianSplat.Stride)
                throw new InvalidDataException("Runtime cache stale, truncated or incompatible; regenerate it from the current PLY.");
            if(sourcePath!=null) {
                var source=new FileInfo(sourcePath);
                if(source.Length!=h.SourceLength||source.LastWriteTimeUtc.Ticks!=h.SourceTicks)
                    throw new InvalidDataException("Runtime cache source PLY changed; regenerate it.");
            }
            return h;
        }
        public static unsafe GaussianSplat[] ReadRange(string path, Header h, long offset, int count, CancellationToken cancellation)
        {
            if (offset < 0 || count < 0 || offset > h.Count - count) throw new InvalidDataException("Runtime cache record range out of bounds.");
            cancellation.ThrowIfCancellationRequested();
            var output = new GaussianSplat[count];
            if (count == 0) return output;
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            f.Position = checked(HeaderSize + offset * GaussianSplat.Stride);
            long total = (long)count * GaussianSplat.Stride, done = 0;
            byte[] bytes = new byte[(int)Math.Min(1024 * 1024,total)];
            fixed (GaussianSplat* destination = output)
            fixed (byte* source = bytes)
                while (done < total)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int size = (int)Math.Min(bytes.Length, total - done), read = 0;
                    while (read < size) { int n = f.Read(bytes, read, size - read); if (n == 0) throw new EndOfStreamException(path); read += n; }
                    Buffer.MemoryCopy(source, (byte*)destination + done, total - done, size);
                    done += size;
                }
            return output;
        }
        public static unsafe void Build(string sourcePath, string destination, CancellationToken cancellation, Action<float> progress = null)
        {
            if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("Cannot overwrite source PLY.");
            if (!BitConverter.IsLittleEndian) throw new NotSupportedException("Cache generation requires little-endian CPU.");
            var source = new FileInfo(sourcePath);
            long length = source.Length, ticks = source.LastWriteTimeUtc.Ticks;
            var h = GaussianPLYReader.ReadHeader(sourcePath);
            string temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var f = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                {
                    using (var writer = new BinaryWriter(f, Encoding.ASCII, true))
                    {
                        writer.Write(Encoding.ASCII.GetBytes(Magic)); writer.Write(Version); writer.Write(GaussianSplat.Stride);
                        writer.Write(h.SHDegree); writer.Write(0); writer.Write(h.VertexCount); writer.Write(length); writer.Write(ticks);
                    }
                    byte[] bytes = new byte[32768 * GaussianSplat.Stride];
                    for (long at = 0; at < h.VertexCount; at += 32768)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(32768, h.VertexCount - at);
                        var data = GaussianPLYReader.ReadRange(sourcePath, h, at, n, h.SHDegree, cancellation);
                        fixed (GaussianSplat* p = data)
                        fixed (byte* b = bytes) Buffer.MemoryCopy(p, b, bytes.Length, (long)n * GaussianSplat.Stride);
                        f.Write(bytes, 0, n * GaussianSplat.Stride);
                        progress?.Invoke((float)(at + n) / Math.Max(1, h.VertexCount));
                    }
                }
                source.Refresh();
                if (source.Length != length || source.LastWriteTimeUtc.Ticks != ticks) throw new IOException("Source PLY changed during cache generation.");
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static async Task<GaussianSplat[]> ReadRangeAsync(string path,Header h,long offset,int count,
            GaussianReadPipeline pipeline,CancellationToken cancellation)
        {
            if(offset<0||count<0||offset>h.Count-count)throw new InvalidDataException("Runtime cache record range out of bounds.");
            cancellation.ThrowIfCancellationRequested();var output=new GaussianSplat[count];if(count==0)return output;
            long total=(long)count*GaussianSplat.Stride,done=0;
            byte[] bytes=pipeline.Rent((int)Math.Min(1048576,total));
            try
            {
                using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
                stream.Position=checked(HeaderSize+offset*GaussianSplat.Stride);
                while(done<total)
                {
                    int size=(int)Math.Min(bytes.Length,total-done);long destinationOffset=done;
                    await pipeline.ReadExactlyAsync(stream,bytes,size,cancellation).ConfigureAwait(false);
                    await pipeline.DecodeAsync(()=>CopyDecodedBytes(bytes,output,destinationOffset,size,cancellation),cancellation).ConfigureAwait(false);
                    done+=size;
                }
            }
            finally{pipeline.Return(bytes);}
            return output;
        }
        static unsafe void CopyDecodedBytes(byte[] source,GaussianSplat[] destination,long byteOffset,int count,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            fixed(byte* input=source)fixed(GaussianSplat* output=destination)
                Buffer.MemoryCopy(input,(byte*)output+byteOffset,(long)destination.Length*GaussianSplat.Stride-byteOffset,count);
        }
    }
}
