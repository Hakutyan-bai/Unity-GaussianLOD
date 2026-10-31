using GaussianSplatLOD.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianReplayTests
    {
        [Test]public void ReplayUsesTimestampsAndRepeatsIdentically()
        {
            var bounds=new Bounds(new Vector3(39,-16,-31),new Vector3(944,73,190));var matrix=Matrix4x4.Scale(new Vector3(1,-1,1));
            foreach(double time in new double[]{0,5,11.9,15,23.9,24,29.9}){
                var first=GaussianReplayPath.Evaluate(time,matrix,bounds);var next=GaussianReplayPath.Evaluate(time+30,matrix,bounds);
                Assert.That(Vector3.Distance(first.position,next.position),Is.LessThan(.001));Assert.That(Quaternion.Angle(first.rotation,next.rotation),Is.LessThan(.05));
                Assert.That(float.IsNaN(first.position.x)||float.IsNaN(first.rotation.x),Is.False);
            }
        }
        [Test]public void ReplayContainsAnExplicitTeleport()
        {
            var bounds=new Bounds(Vector3.zero,new Vector3(944,73,190));
            var before=GaussianReplayPath.Evaluate(23.999,Matrix4x4.identity,bounds);var after=GaussianReplayPath.Evaluate(24,Matrix4x4.identity,bounds);
            Assert.That(Vector3.Distance(before.position,after.position),Is.GreaterThan(20));
        }
        [Test]public void StationaryHeadTurnStageChangesViewWithoutMovingCamera()
        {
            var bounds=new Bounds(Vector3.zero,new Vector3(944,73,190));var a=GaussianReplayPath.Evaluate(12,Matrix4x4.identity,bounds);var b=GaussianReplayPath.Evaluate(13,Matrix4x4.identity,bounds);
            Assert.That(Vector3.Distance(a.position,b.position),Is.LessThan(.001));Assert.That(Quaternion.Angle(a.rotation,b.rotation),Is.GreaterThan(5));
        }
    }
}
