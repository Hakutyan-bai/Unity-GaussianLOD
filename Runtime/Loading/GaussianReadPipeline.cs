using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GaussianSplatLOD
{
    /// <summary>Independent bounded IO/CPU stages. Workers never own Unity objects or GPU resources.</summary>
    internal sealed class GaussianReadPipeline
    {
        internal sealed class Limiter
        {
            sealed class Pending
            {
                public readonly TaskCompletionSource<IDisposable> Completion = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
                public CancellationTokenRegistration Cancellation;
            }
            sealed class Lease : IDisposable
            {
                Limiter owner;
                public Lease(Limiter value) { owner = value; }
                public void Dispose() { Interlocked.Exchange(ref owner, null)?.Release(); }
            }
            readonly object sync = new object();
            readonly Queue<Pending> waiting = new Queue<Pending>();
            int active, limit;
            public int Active { get { lock(sync) return active; } }
            public Limiter(int capacity) { limit = Math.Max(1, capacity); }
            public void Configure(int capacity) { lock(sync) { limit = Math.Max(1, Math.Min(8, capacity)); Grant(); } }
            public Task<IDisposable> Acquire(CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                lock(sync)
                {
                    var pending = new Pending();
                    pending.Cancellation = cancellation.Register(() => pending.Completion.TrySetCanceled());
                    waiting.Enqueue(pending); Grant(); return pending.Completion.Task;
                }
            }
            void Grant()
            {
                while(active < limit && waiting.Count > 0)
                {
                    var next = waiting.Dequeue(); next.Cancellation.Dispose();
                    if(next.Completion.Task.IsCanceled) continue;
                    active++; next.Completion.TrySetResult(new Lease(this));
                }
            }
            void Release() { lock(sync) { active--; Grant(); } }
        }
        readonly Limiter reads = new Limiter(2), decodes = new Limiter(1);
        long bytes, ioTicks, decodeTicks, staging;
        public int ActiveReads => reads.Active;
        public int ActiveParses => decodes.Active;
        public long IOBytes => Interlocked.Read(ref bytes);
        public long StagingBytes => Interlocked.Read(ref staging);
        public double IOReadMilliseconds => Interlocked.Read(ref ioTicks) * 1000.0 / Stopwatch.Frequency;
        public double DecodeMilliseconds => Interlocked.Read(ref decodeTicks) * 1000.0 / Stopwatch.Frequency;
        public void Configure(int io, int parse) { reads.Configure(io); decodes.Configure(parse); }
        public byte[] Rent(int size)
        {
            // Callers request at most 1 MiB; pool power-of-two rounding never exceeds that cap.
            if(size < 1 || size > 1048576) throw new ArgumentOutOfRangeException(nameof(size));
            var buffer = ArrayPool<byte>.Shared.Rent(size); Interlocked.Add(ref staging, buffer.Length); return buffer;
        }
        public void Return(byte[] buffer) { if(buffer == null) return; Interlocked.Add(ref staging, -buffer.Length); ArrayPool<byte>.Shared.Return(buffer); }
        public async Task ReadExactlyAsync(FileStream stream, byte[] buffer, int count, CancellationToken cancellation)
        {
            using(await reads.Acquire(cancellation).ConfigureAwait(false))
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    int at = 0;
                    while(at < count)
                    {
                        int read = await stream.ReadAsync(buffer, at, count-at, cancellation).ConfigureAwait(false);
                        if(read == 0) throw new EndOfStreamException(stream.Name);
                        at += read; Interlocked.Add(ref bytes, read);
                    }
                }
                finally { Interlocked.Add(ref ioTicks, Stopwatch.GetTimestamp()-start); }
            }
        }
        public async Task DecodeAsync(Action decode, CancellationToken cancellation)
        {
            using(await decodes.Acquire(cancellation).ConfigureAwait(false))
            {
                await Task.Run(() => {
                    cancellation.ThrowIfCancellationRequested(); long start = Stopwatch.GetTimestamp();
                    try { decode(); } finally { Interlocked.Add(ref decodeTicks, Stopwatch.GetTimestamp()-start); }
                }, cancellation).ConfigureAwait(false);
            }
        }
    }
}
