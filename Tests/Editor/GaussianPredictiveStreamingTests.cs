using NUnit.Framework;
using UnityEngine;

namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianPredictiveStreamingTests
    {
        [Test] public void AsyncFenceRequiresBothBackendCapabilities()
        {
            Assert.IsFalse(GaussianGPUResources.CanUseAsyncFence(true,false));
            Assert.IsFalse(GaussianGPUResources.CanUseAsyncFence(false,true));
            Assert.IsFalse(GaussianGPUResources.CanUseAsyncFence(false,false));
            Assert.IsTrue(GaussianGPUResources.CanUseAsyncFence(true,true));
        }
        [Test] public void WorldBoundsCacheUsesMetadataAndModelTransform()
        {
            string path=System.IO.Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo/lod-meta.json");
            if(!System.IO.File.Exists(path))Assert.Ignore("Create synthetic Demo fixture first");
            var go=new GameObject("Bounds cache regression");go.SetActive(false);var r=go.AddComponent<GaussianLODRenderer>();
            r.transform.SetPositionAndRotation(new Vector3(7,20,-15),Quaternion.Euler(180,0,0));r.transform.localScale=new Vector3(1,1,-1);
            using var session=new GaussianStreamingSession(r,new GaussianLODLoader(path));
            try{
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                typeof(GaussianStreamingSession).GetMethod("RefreshBounds",flags).Invoke(session,null);
                var bounds=(Bounds[])typeof(GaussianStreamingSession).GetField("worldBounds",flags).GetValue(session);
                for(int n=0;n<bounds.Length;n++){
                    var expected=GaussianStreamingSession.TransformBounds(session.Loader.Metadata.tree[n].bound.Bounds,r.ModelMatrix);
                    Assert.That(Vector3.Distance(expected.center,bounds[n].center),Is.LessThan(.001));
                    Assert.GreaterOrEqual(bounds[n].size.sqrMagnitude,expected.size.sqrMagnitude);
                    Assert.Greater(bounds[n].size.sqrMagnitude,0);
                }
            }finally{Object.DestroyImmediate(go);}
        }
        [Test] public void LateResultFromPreviousMovementGenerationIsDiscardedAndReservationReleased()
        {
            string path=System.IO.Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo/lod-meta.json");if(!System.IO.File.Exists(path))Assert.Ignore("Create fixture first");
            var go=new GameObject("Late result regression");go.SetActive(false);var r=go.AddComponent<GaussianLODRenderer>();
            using var session=new GaussianStreamingSession(r,new GaussianLODLoader(path));
            try{
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                var entries=(System.Collections.Generic.List<GaussianStreamingSession.Entry>)typeof(GaussianStreamingSession).GetField("entries",flags).GetValue(session);
                var e=entries.Find(entry=>!entry.Permanent);Assert.NotNull(e);
                e.Generation=-1;e.Reservation=e.Bytes+1048576;e.Job=System.Threading.Tasks.Task.FromResult(new GaussianSplat[1]);
                typeof(GaussianStreamingSession).GetProperty("CPUBytes").SetValue(session,e.Reservation);
                typeof(GaussianStreamingSession).GetProperty("InFlight").SetValue(session,1);
                typeof(GaussianStreamingSession).GetMethod("Poll",flags).Invoke(session,new object[]{1.0});
                Assert.IsNull(e.CPU);Assert.AreEqual(0,session.CPUBytes);Assert.AreEqual(0,session.InFlight);Assert.AreEqual(1,r.Metrics.DroppedResults);
            }finally{Object.DestroyImmediate(go);}
        }
        [Test] public void ResidentCapacityIsIndependentOfVisibleCountAndFitsLargestSUITRange()
        {
            int capacity=GaussianGPUResources.PlanCapacity(640,1000000,435022,2147483648L,52,2000000);
            Assert.AreEqual(2000000,capacity);
            Assert.GreaterOrEqual(capacity-435022,383095);
            Assert.LessOrEqual(capacity*(long)GaussianSplat.Stride+GaussianGPUResources.SortBytes(1000000)+52*40L,640L*1048576);
            Assert.LessOrEqual(GaussianGPUResources.PlanCapacity(640,1000000,435022,2147483648L,52,500000),500000);
        }
        [Test] public void DirectionalPredictionUsesSweptPathAndStationaryFallback()
        {
            Assert.AreEqual(2,GaussianPrediction.DistanceToSegment(new Vector3(5,0,2),Vector3.zero,new Vector3(10,0,0)),.0001);
            Assert.AreEqual(5,GaussianPrediction.DistanceToSegment(new Vector3(-5,0,0),Vector3.zero,new Vector3(10,0,0)),.0001);
            Assert.AreEqual(3,GaussianPrediction.DistanceToSegment(new Vector3(0,3,0),Vector3.zero,Vector3.zero),.0001);
        }
        [Test] public void VisibleRequestPrecedesSpeculativeRequestRegardlessOfSizeOrWait()
        {
            var visible=new GaussianStreamingSession.Entry{Tier=1,Priority=.1};var predicted=new GaussianStreamingSession.Entry{Tier=0,Priority=100};
            var compare=typeof(GaussianStreamingSession).GetMethod("ComparePriority",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            Assert.Less((int)compare.Invoke(null,new object[]{visible,predicted}),0);
        }
        [Test] public void PriorityHasCostAwarenessAndAgingPreventsLargeRequestStarvation()
        {
            var weights=new GaussianPriorityWeights();
            float urgent=weights.Score(true,1,1,true,false,0,1048576,1);
            float prefetch=weights.Score(false,0,1,false,false,0,1048576,1);
            Assert.Greater(urgent,prefetch);
            Assert.Greater(weights.Score(true,1,1,true,false,0,1048576,1),weights.Score(true,1,1,true,false,0,100*1048576,200));
            Assert.Greater(weights.Score(true,1,1,true,false,60,100*1048576,200),urgent);
        }
        [Test] public void BVHReturnsTheSameVisibleNodesAsFlatCulling()
        {
            var views=new GaussianViewContext();var projection=Matrix4x4.Perspective(60,1,.1f,100);
            views.SetViews(Matrix4x4.identity,projection,Matrix4x4.identity,projection,new Vector2(512,512));
            var bounds=new Bounds[512];for(int i=0;i<bounds.Length;i++)bounds[i]=new Bounds(new Vector3(i%32-16,i/32-8,-15),Vector3.one);
            var index=new GaussianChunkBVH(bounds);var actual=new System.Collections.Generic.List<int>();index.Query(views,actual);
            for(int i=0;i<bounds.Length;i++)Assert.AreEqual(views.TestBounds(bounds[i]),actual.Contains(i));
        }
    }
}
