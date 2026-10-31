using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianFailureContinuityTests
    {
        [UnityTest] public IEnumerator MissingDetailRetainsCoverageWithoutBlockingPeersOrEvictingPermanentCache()
        {
            string source=Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo");
            if(!File.Exists(Path.Combine(source,"lod-meta.json")))Assert.Ignore("Create Demo fixture first.");
            string directory=Path.Combine(Path.GetTempPath(),"Gaussian failure continuity "+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var go=new GameObject("Failure continuity regression");go.SetActive(false);
            var cameraObject=new GameObject("Failure continuity camera");var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;
            GaussianStreamingSession session=null;
            try
            {
                var metadata=GaussianLODMetadata.Parse(File.ReadAllText(Path.Combine(source,"lod-meta.json")));
                File.Copy(Path.Combine(source,"lod-meta.json"),Path.Combine(directory,"lod-meta.json"));
                foreach(string relative in metadata.files){string target=Path.Combine(directory,relative);Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(Path.Combine(source,relative),target);}
                var loader=new GaussianLODLoader(Path.Combine(directory,"lod-meta.json"),false);
                var renderer=go.AddComponent<GaussianLODRenderer>();renderer.loadOnEnable=false;renderer.maxGPUMemory=64;renderer.maxCPUCache=32;renderer.maxVisibleSplats=90000;
                renderer.enableAsyncLoading=false;renderer.enableDynamicLOD=false;renderer.enableFrustumCulling=false;renderer.nearQualityDistance=0;renderer.cacheRetentionSeconds=0;
                renderer.uploadSplatsPerFrame=16384;renderer.gpuUploadBudgetMilliseconds=20;renderer.maxUploadSubmissions=16;
                camera.transform.position=new Vector3(0,12,-16);camera.transform.LookAt(Vector3.zero);
                session=new GaussianStreamingSession(renderer,loader);session.InitializeCandidates();double now=100;
                for(int i=0;i<200&&!session.CoverageReady;i++){session.Tick(camera,now);now+=.02;yield return null;}
                Assert.IsTrue(session.CoverageReady);Assert.AreEqual(metadata.tree.Length,session.CoveredNodes);
                var brokenRange=metadata.tree[0].lods[2];var broken=session.Find(brokenRange);
                Assert.IsFalse(broken.Permanent);File.Move(loader.FilePath(brokenRange.file),loader.FilePath(brokenRange.file)+".missing");
                LogAssert.Expect(LogType.Error,new Regex("Gaussian LOD:.*失败"));
                bool observedFailure=false,peerAtFullDetail=false;
                for(int i=0;i<500;i++)
                {
                    session.Tick(camera,now);now+=.02;
                    if(broken.Failures>0){observedFailure=true;broken.RetryAt=now+60;}
                    Assert.AreEqual(metadata.tree.Length,session.CoveredNodes,"A failed detail request cannot remove its coarse region.");
                    foreach(var segment in session.VisibleSegments(camera))Assert.Greater(segment.Count,0);
                    for(int n=1;n<session.CurrentLevels.Length;n++)if(session.CurrentLevels[n]==0)peerAtFullDetail=true;
                    if(observedFailure&&peerAtFullDetail)break;
                    yield return null;
                }
                Assert.IsTrue(observedFailure,"The actual missing PLY must reach the loader error path.");
                Assert.IsTrue(peerAtFullDetail,"A failed node in backoff must not hold healthy peers at its refinement frontier.");
                Assert.Greater(session.CurrentLevels[0],2,"The missing detail must retain its available coarser region.");
                Assert.IsTrue(session.CoverageReady);
                renderer.maxCPUCache=1;
                // Already reserved requests may finish their bounded upload before retiring;
                // shrinking a budget cannot invalidate an in-progress buffer lease.
                for(int i=0;i<50;i++){session.Tick(camera,now);now+=.02;if(session.CPUBytes<=1048576)break;yield return null;}
                Assert.LessOrEqual(session.CPUBytes,1048576,"Pressure must evict disposable decoded arrays.");
                foreach(var node in metadata.tree){var permanent=session.Find(node.lods[metadata.levels-1]);Assert.IsTrue(permanent.Permanent);Assert.IsTrue(permanent.Ready);Assert.NotNull(permanent.CPU,"Permanent shared decoded data must survive CPU pressure.");}
            }
            finally{session?.Dispose();UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(cameraObject);if(Directory.Exists(directory))Directory.Delete(directory,true);}
        }
    }
}
