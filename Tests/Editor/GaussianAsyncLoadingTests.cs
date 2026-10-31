using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GaussianSplatLOD.Editor;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianAsyncLoadingTests
    {
        string directory,json,ply;
        [SetUp] public void Setup()
        {
            directory=Path.Combine(Path.GetTempPath(),"Gaussian async 校园 "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            ply=Path.Combine(directory,"共享 分块.ply");json=Path.Combine(directory,"lod-meta.json");
            GaussianLODDemo.WriteFixture(ply,73);
            File.WriteAllText(json,JsonConvert.SerializeObject(new{magicCode=2500660,type="lod-splat",version="1.0",counts=73,shDegree=3,levels=1,forwardBox=new{min=new[]{-10,-10,-10},max=new[]{10,10,10}},files=new[]{Path.GetFileName(ply)},permanentFiles=new[]{0},tree=new[]{new{bound=new{min=new[]{-10,-10,-10},max=new[]{10,10,10}},lods=new[]{new{file=0,offset=0,count=73}}}}}));
        }
        [TearDown] public void Cleanup(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
        static unsafe void Equal(GaussianSplat[] expected,GaussianSplat[] actual)
        {
            Assert.AreEqual(expected.Length,actual.Length);
            fixed(GaussianSplat* a=expected)fixed(GaussianSplat* b=actual)
                for(int i=0;i<expected.Length*GaussianSplat.Stride;i++)Assert.AreEqual(((byte*)a)[i],((byte*)b)[i],"byte "+i);
        }
        [UnityTest] public IEnumerator AsyncPLYRangePreservesAllAttributes()=>Await(AsyncRangesPreserveAllAttributesAndReturnStagingBuffers(false));
        [UnityTest] public IEnumerator AsyncCacheRangePreservesAllAttributes()=>Await(AsyncRangesPreserveAllAttributesAndReturnStagingBuffers(true));
        static IEnumerator Await(Task task){double deadline=Time.realtimeSinceStartupAsDouble+20;while(!task.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;Assert.IsTrue(task.IsCompleted,"Asynchronous test timeout");task.GetAwaiter().GetResult();}
        async Task AsyncRangesPreserveAllAttributesAndReturnStagingBuffers(bool cached)
        {
            if(cached)GaussianRuntimeCache.Build(ply,ply+GaussianRuntimeCache.Extension,CancellationToken.None);
            var loader=new GaussianLODLoader(json,cached);loader.ConfigureConcurrency(2,1);
            var expected=GaussianPLYReader.ReadRange(ply,GaussianPLYReader.ReadHeader(ply),11,37,3,CancellationToken.None);
            var actual=await loader.ReadAsync(new GaussianRangeKey(0,11,37),CancellationToken.None);
            Equal(expected,actual);Assert.AreEqual(0,loader.StagingBytes);Assert.AreEqual(0,loader.ActiveReads);Assert.AreEqual(0,loader.ActiveParses);
            Assert.Greater(loader.IOBytes,0);Assert.AreEqual(cached?1:0,loader.RuntimeCacheReads);Assert.AreEqual(cached?0:1,loader.PLYReads);
        }
        [UnityTest] public IEnumerator CancelledReadDoesNotReserveOrDecodeData()=>Await(CancelledReadDoesNotReserveOrDecodeDataAsync());
        async Task CancelledReadDoesNotReserveOrDecodeDataAsync()
        {
            var loader=new GaussianLODLoader(json);using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{await loader.ReadAsync(new GaussianRangeKey(0,0,73),cancel.Token);Assert.Fail("Expected cancellation");}catch(OperationCanceledException){}
            Assert.AreEqual(0,loader.IOBytes);Assert.AreEqual(0,loader.StagingBytes);
        }
        [UnityTest] public IEnumerator IndexedCacheSurvivesSourceTimestampChangeAndCorruptionFallsBackToPLY()=>Await(IndexedCacheSurvivesSourceTimestampChangeAndCorruptionFallsBackToPLYAsync());
        async Task IndexedCacheSurvivesSourceTimestampChangeAndCorruptionFallsBackToPLYAsync()
        {
            GaussianDeploymentBuilder.PrepareDataset(json,CancellationToken.None);
            File.SetLastWriteTimeUtc(ply,File.GetLastWriteTimeUtc(ply).AddHours(1));
            var loader=new GaussianLODLoader(json);await loader.ReadAsync(new GaussianRangeKey(0,0,73),CancellationToken.None);
            Assert.AreEqual(1,loader.RuntimeCacheReads);Assert.AreEqual(0,loader.PLYReads);
            using(var file=new FileStream(ply+GaussianRuntimeCache.Extension,FileMode.Open,FileAccess.ReadWrite)){file.Position=GaussianRuntimeCache.HeaderSize+100;int b=file.ReadByte();file.Position--;file.WriteByte((byte)(b^1));}
            var changed=new GaussianLODLoader(json);var data=await changed.ReadAsync(new GaussianRangeKey(0,0,73),CancellationToken.None);
            Assert.AreEqual(73,data.Length);Assert.AreEqual(0,changed.RuntimeCacheReads);Assert.AreEqual(1,changed.PLYReads);
        }
        [Test] public void DeploymentIndexPreservesRecordOffsetsAndRejectsChangedJSON()
        {
            var index=GaussianDeploymentBuilder.PrepareDataset(json,CancellationToken.None);
            Assert.AreEqual(1,index.files.Length);Assert.AreEqual(73,index.files[0].recordCount);Assert.AreEqual(48,index.files[0].ranges[0].byteOffset);
            Assert.IsTrue(GaussianDeploymentManifest.IsHash(index.datasetId));
            File.AppendAllText(json," ");Assert.Throws<InvalidDataException>(()=>new GaussianLODLoader(json));
        }
        [UnityTest] public IEnumerator DecodeLimitCanBeLoweredWithoutInvalidatingActiveLeases()=>Await(DecodeLimitCanBeLoweredWithoutInvalidatingActiveLeasesAsync());
        async Task DecodeLimitCanBeLoweredWithoutInvalidatingActiveLeasesAsync()
        {
            var limiter=new GaussianReadPipeline.Limiter(2);var a=await limiter.Acquire(CancellationToken.None);var b=await limiter.Acquire(CancellationToken.None);
            limiter.Configure(1);using var cancellation=new CancellationTokenSource();var waiting=limiter.Acquire(cancellation.Token);
            a.Dispose();Assert.IsFalse(waiting.IsCompleted);b.Dispose();using var c=await waiting;Assert.AreEqual(1,limiter.Active);
            using var cancelled=new CancellationTokenSource();var last=limiter.Acquire(cancelled.Token);cancelled.Cancel();Assert.IsTrue(last.IsCanceled);
        }
        [UnityTest] public IEnumerator LegacyURLCorruptCacheFallsBackToSourcePLY()
        {
            File.WriteAllBytes(ply+GaussianRuntimeCache.Extension,new byte[]{1,2,3,4});
            var source=new GaussianDatasetSource(new Uri(json).AbsoluteUri,Path.Combine(directory,"legacy-install"),true);
            try{
                var ready=source.OpenAsync(CancellationToken.None);double deadline=Time.realtimeSinceStartupAsDouble+15;
                while(!ready.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline){source.Tick();yield return null;}
                Assert.IsTrue(ready.IsCompleted);Assert.IsFalse(ready.IsFaulted,ready.Exception?.ToString());
                var read=ready.Result.ReadAsync(new GaussianRangeKey(0,0,73),CancellationToken.None);
                while(!read.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline){source.Tick();yield return null;}
                Assert.IsTrue(read.IsCompleted);Assert.IsFalse(read.IsFaulted,read.Exception?.ToString());Assert.AreEqual(73,read.Result.Length);
                Assert.AreEqual(1,ready.Result.PLYReads);Assert.AreEqual(0,ready.Result.RuntimeCacheReads);
            }finally{source.Dispose();}
        }
        [UnityTest] public IEnumerator LocalURLStagesOnceSharesConsumersAndReusesVerifiedFiles()
        {
            GaussianDeploymentBuilder.PrepareDataset(json,CancellationToken.None);
            string install=Path.Combine(directory,"installation");var source=new GaussianDatasetSource(new Uri(json).AbsoluteUri,install,true);
            try
            {
                var open=source.OpenAsync(CancellationToken.None);double deadline=Time.realtimeSinceStartupAsDouble+15;
                while(!open.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline){source.Tick();yield return null;}
                Assert.IsTrue(open.IsCompleted);Assert.IsFalse(open.IsFaulted,open.Exception?.ToString());var loader=open.Result;
                using var cancel=new CancellationTokenSource();var first=loader.ReadAsync(new GaussianRangeKey(0,0,20),cancel.Token);var second=loader.ReadAsync(new GaussianRangeKey(0,20,20),CancellationToken.None);
                while(source.ConsumerCount(0)<2&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                Assert.AreEqual(2,source.ConsumerCount(0));cancel.Cancel();
                while(!second.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline){source.Tick();yield return null;}
                Assert.IsTrue(second.IsCompleted);Assert.IsFalse(second.IsFaulted,second.Exception?.ToString());Assert.AreEqual(20,second.Result.Length);
                while(!first.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                Assert.IsTrue(first.IsCanceled);Assert.AreEqual(3,source.CompletedTransfers,"JSON, deployment index and one shared payload");
                Assert.AreEqual(0,loader.StagingBytes);source.Dispose();
                var warm=new GaussianDatasetSource(new Uri(json).AbsoluteUri,install,true);
                try
                {
                    var ready=warm.OpenAsync(CancellationToken.None);
                    while(!ready.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline){warm.Tick();yield return null;}
                    Assert.IsTrue(ready.IsCompleted);Assert.IsFalse(ready.IsFaulted,ready.Exception?.ToString());
                    var read=ready.Result.ReadAsync(new GaussianRangeKey(0,0,10),CancellationToken.None);
                    while(!read.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline){warm.Tick();yield return null;}
                    Assert.IsTrue(read.IsCompleted);Assert.IsFalse(read.IsFaulted,read.Exception?.ToString());Assert.AreEqual(2,warm.CompletedTransfers,"Warm install must not transfer payload again");Assert.AreEqual(1,warm.VerifiedCacheHits);
                }finally{warm.Dispose();}
            }finally{source.Dispose();}
        }
        [UnityTest] public IEnumerator CancelledLastConsumerCanReissueBeforeAcquisitionTicks()
        {
            GaussianDeploymentBuilder.PrepareDataset(json,CancellationToken.None);
            var source=new GaussianDatasetSource(new Uri(json).AbsoluteUri,Path.Combine(directory,"cancel-reissue"),true);
            try
            {
                var open=source.OpenAsync(CancellationToken.None);double deadline=Time.realtimeSinceStartupAsDouble+15;
                while(!open.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline){source.Tick();yield return null;}
                Assert.IsTrue(open.IsCompleted);Assert.IsFalse(open.IsFaulted,open.Exception?.ToString());
                var loader=open.Result;using var cancel=new CancellationTokenSource();
                var obsolete=loader.ReadAsync(new GaussianRangeKey(0,0,73),cancel.Token);
                // Do not Tick acquisition: its cancelled shared task deliberately remains pending.
                while(source.PendingFiles<1&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                Assert.AreEqual(1,source.PendingFiles);Assert.AreEqual(1,source.ConsumerCount(0));
                cancel.Cancel();
                while(!obsolete.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                Assert.IsTrue(obsolete.IsCanceled);Assert.AreEqual(0,source.ConsumerCount(0));
                var replacement=loader.ReadAsync(new GaussianRangeKey(0,0,73),CancellationToken.None);
                while(source.PendingFiles<2&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                Assert.AreEqual(2,source.PendingFiles,"A fresh consumer must queue independently of the not-yet-completed cancelled download.");
                while(!replacement.IsCompleted&&Time.realtimeSinceStartupAsDouble<deadline){source.Tick();yield return null;}
                Assert.IsTrue(replacement.IsCompleted);Assert.IsFalse(replacement.IsCanceled);Assert.IsFalse(replacement.IsFaulted,replacement.Exception?.ToString());
                Assert.AreEqual(73,replacement.Result.Length);Assert.AreEqual(3,source.CompletedTransfers,"Only JSON, index and the replacement payload complete.");
                Assert.AreEqual(0,source.PendingFiles);Assert.AreEqual(0,source.ConsumerCount(0));Assert.AreEqual(0,loader.StagingBytes);
            }finally{source.Dispose();}
        }
    }
}
