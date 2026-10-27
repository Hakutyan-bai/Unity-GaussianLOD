using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using GaussianSplatLOD.Editor;
using Newtonsoft.Json;

namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianRuntimeCacheTests
    {
        string source, cache;
        [SetUp] public void Setup()
        {
            source=Path.Combine(Path.GetTempPath(),"Gaussian 缓存 "+Guid.NewGuid()+".ply");cache=source+GaussianRuntimeCache.Extension;
            GaussianLODDemo.WriteFixture(source,73);
        }
        [TearDown] public void Cleanup(){File.Delete(source);File.Delete(cache);}
        [Test] public unsafe void CacheRangeIsBitIdenticalToPLYIncludingSHAndCovariance()
        {
            GaussianRuntimeCache.Build(source,cache,CancellationToken.None);
            var h=GaussianRuntimeCache.Validate(cache,source,3);
            var cached=GaussianRuntimeCache.ReadRange(cache,h,11,37,CancellationToken.None);
            var decoded=GaussianPLYReader.ReadRange(source,GaussianPLYReader.ReadHeader(source),11,37,3,CancellationToken.None);
            fixed(GaussianSplat* a=cached)fixed(GaussianSplat* b=decoded)
                for(int i=0;i<cached.Length*GaussianSplat.Stride;i++)Assert.AreEqual(((byte*)b)[i],((byte*)a)[i],"byte "+i);
            Assert.Throws<InvalidDataException>(()=>GaussianRuntimeCache.ReadRange(cache,h,72,2,CancellationToken.None));
        }
        [Test] public void CacheRejectsModifiedSourceAndTruncatedData()
        {
            GaussianRuntimeCache.Build(source,cache,CancellationToken.None);
            long ticks=File.GetLastWriteTimeUtc(source).Ticks;File.SetLastWriteTimeUtc(source,new DateTime(ticks+TimeSpan.TicksPerSecond,DateTimeKind.Utc));
            Assert.Throws<InvalidDataException>(()=>GaussianRuntimeCache.Validate(cache,source,3));
            File.SetLastWriteTimeUtc(source,new DateTime(ticks,DateTimeKind.Utc));
            using(var f=File.OpenWrite(cache))f.SetLength(f.Length-1);
            Assert.Throws<InvalidDataException>(()=>GaussianRuntimeCache.Validate(cache,source,3));
        }
        [Test] public void CancellationDoesNotPublishOrOverwriteSource()
        {
            var before=File.ReadAllBytes(source);using var c=new CancellationTokenSource();c.Cancel();
            Assert.Throws<OperationCanceledException>(()=>GaussianRuntimeCache.Build(source,cache,c.Token));
            Assert.IsFalse(File.Exists(cache));CollectionAssert.AreEqual(before,File.ReadAllBytes(source));
            Assert.Throws<IOException>(()=>GaussianRuntimeCache.Build(source,source,CancellationToken.None));
        }
        [Test] public void MobilePresetsRemainBoundedAndKeepCoherentCoverage()
        {
            var go=new GameObject("Mobile profile test");go.SetActive(false);
            try{var r=go.AddComponent<GaussianLODRenderer>();r.ApplyMobilePreset(true);Assert.AreEqual(350000,r.maxVisibleSplats);Assert.AreEqual(384,r.maxGPUMemory);r.ApplyMobilePreset(false);Assert.AreEqual(200000,r.maxVisibleSplats);Assert.AreEqual(256,r.maxGPUMemory);Assert.IsTrue(r.coherentLOD&&r.enableAsyncLoading&&r.preferRuntimeCache);}finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        [TestCase(false,false)] [TestCase(true,false)] [TestCase(true,true)]
        public void LoaderUsesValidCacheAndFallsBackWhenStaleOrDisabled(bool prefer,bool stale)
        {
            string json=source+".json";
            try {
                GaussianRuntimeCache.Build(source,cache,CancellationToken.None);
                if(stale)File.SetLastWriteTimeUtc(source,File.GetLastWriteTimeUtc(source).AddSeconds(2));
                File.WriteAllText(json,JsonConvert.SerializeObject(new{magicCode=2500660,type="lod-splat",version="1.0",counts=73,shDegree=3,levels=1,forwardBox=new{min=new[]{-10,-10,-10},max=new[]{10,10,10}},files=new[]{Path.GetFileName(source)},permanentFiles=new int[0],tree=new[]{new{bound=new{min=new[]{-10,-10,-10},max=new[]{10,10,10}},lods=new[]{new{file=0,offset=0,count=73}}}}}));
                var loader=new GaussianLODLoader(json,prefer);var result=loader.Read(new GaussianRangeKey(0,7,11),CancellationToken.None);
                Assert.AreEqual(11,result.Length);Assert.AreEqual(prefer&&!stale?1:0,loader.RuntimeCacheReads);Assert.AreEqual(prefer&&!stale?0:1,loader.PLYReads);
            }finally{File.Delete(json);}
        }
        [Test] public void CacheOnlyExportLoadsWithoutPLYAndDoesNotOverwriteDestination()
        {
            string json=source+".json",destination=Path.Combine(Path.GetTempPath(),"Gaussian export "+Guid.NewGuid());
            try {
                File.WriteAllText(json,JsonConvert.SerializeObject(new{magicCode=2500660,type="lod-splat",version="1.0",counts=73,shDegree=3,levels=1,forwardBox=new{min=new[]{-10,-10,-10},max=new[]{10,10,10}},files=new[]{Path.GetFileName(source)},permanentFiles=new int[0],tree=new[]{new{bound=new{min=new[]{-10,-10,-10},max=new[]{10,10,10}},lods=new[]{new{file=0,offset=0,count=73}}}}}));
                GaussianRuntimeCacheWindow.ExportDataset(json,destination,CancellationToken.None);
                Assert.IsFalse(File.Exists(Path.Combine(destination,Path.GetFileName(source))));
                var loader=new GaussianLODLoader(Path.Combine(destination,Path.GetFileName(json)));loader.ValidateAllRanges();
                Assert.AreEqual(11,loader.Read(new GaussianRangeKey(0,7,11),CancellationToken.None).Length);Assert.AreEqual(1,loader.RuntimeCacheReads);
                Assert.Throws<IOException>(()=>GaussianRuntimeCacheWindow.ExportDataset(json,destination,CancellationToken.None));
                Assert.IsTrue(File.Exists(source));
            }finally{File.Delete(json);if(Directory.Exists(destination))Directory.Delete(destination,true);}
        }
    }
}
