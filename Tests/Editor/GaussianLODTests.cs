using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections;
using GaussianSplatLOD.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianLODTests
    {
        [Test] public void FPSResumeKeepsNavigationViewIncludingPitchOutsideManualLimits()
        {
            var go=new GameObject("FPS navigation resume regression");var c=go.AddComponent<GaussianLODDemoCamera>();
            try{
                c.CaptureCursor();c.enabled=false;
                go.transform.rotation=Quaternion.Euler(4,145,0);var arrival=go.transform.rotation;
                // Runtime OnEnable is exercised by the PlayMode cursor regression.
                c.enabled=true;c.CaptureCursor();c.ApplyLook(0,0);
                Assert.That(Quaternion.Angle(arrival,go.transform.rotation),Is.LessThan(.01),"Capturing must not clamp the look-at pose.");
                c.ApplyLook(0,-1);Assert.That(c.RotationX,Is.EqualTo(6).Within(.001),"Returning to manual limits should be gradual.");
            }finally{c.ReleaseCursor();UnityEngine.Object.DestroyImmediate(go);}
        }
        [Test] public void FPSNormalizesRollMigratesOldLimitsAndPreservesViewDirection()
        {
            var go=new GameObject("FPS pitch regression");var c=go.AddComponent<GaussianLODDemoCamera>();
            try{
                go.transform.rotation=Quaternion.Euler(17,142,180);Vector3 forward=go.transform.forward;c.minRotationX=120;c.maxRotationX=170;
                c.CaptureCursor();Assert.That(Vector3.Angle(forward,go.transform.forward),Is.LessThan(.01));Assert.Greater(Vector3.Dot(go.transform.up,Vector3.up),.9f);
                Assert.AreEqual(10,c.minRotationX);Assert.AreEqual(60,c.maxRotationX);
                c.ApplyLook(0,1000);Assert.AreEqual(10,c.RotationX);c.ApplyLook(0,-1000);Assert.AreEqual(60,c.RotationX);
                var before=go.transform.rotation;c.ReleaseCursor();c.CaptureCursor();Assert.That(Quaternion.Angle(before,go.transform.rotation),Is.LessThan(.01));
                c.ApplyLook(5,0);Assert.That(c.RotationX,Is.EqualTo(60).Within(.001));Assert.Greater(Quaternion.Angle(before,go.transform.rotation),1);
                c.ReleaseCursor();Assert.IsTrue(Cursor.visible);Assert.AreEqual(CursorLockMode.None,Cursor.lockState);
            }finally{c.ReleaseCursor();UnityEngine.Object.DestroyImmediate(go);}
        }
        static string Metadata => "{\"magicCode\":2500660,\"type\":\"lod-splat\",\"version\":\"1.0\",\"counts\":7,\"shDegree\":3,\"levels\":2,\"forwardBox\":{\"min\":[0,0,0],\"max\":[1,1,1]},\"files\":[\"共享 文件.ply\"],\"permanentFiles\":[0],\"tree\":[{\"bound\":{\"min\":[0,0,0],\"max\":[1,1,1]},\"lods\":[{\"file\":0,\"offset\":5,\"count\":2},{\"file\":0,\"offset\":5,\"count\":2}]}]}";
        [Test] public void MetadataKeepsRecordOffsetsAndSourceCount(){var m=GaussianLODMetadata.Parse(Metadata);Assert.AreEqual(7,m.counts);Assert.AreEqual(5,m.tree[0].lods[1].offset);Assert.AreEqual(0,m.permanentFiles[0]);}
        [TestCase("magicCode",0)] [TestCase("levels",0)] [TestCase("shDegree",4)]
        public void InvalidScalarRejected(string name,int value){var j=JObject.Parse(Metadata);j[name]=value;Assert.Throws<InvalidDataException>(()=>GaussianLODMetadata.Parse(j.ToString()));}
        [Test] public void MissingRangeOffsetRejected(){var j=JObject.Parse(Metadata);((JObject)j["tree"][0]["lods"][0]).Remove("offset");Assert.Throws<InvalidDataException>(()=>GaussianLODMetadata.Parse(j.ToString()));}
        [Test] public void FractionalRangeRejected(){var j=JObject.Parse(Metadata);j["tree"][0]["lods"][0]["offset"]=.5;Assert.Throws<InvalidDataException>(()=>GaussianLODMetadata.Parse(j.ToString()));}
        [Test] public void EscapingFileRejected(){var j=JObject.Parse(Metadata);j["files"][0]="../a.ply";Assert.Throws<InvalidDataException>(()=>GaussianLODMetadata.Parse(j.ToString()));}
        [Test] public void FileIndexRejected(){var j=JObject.Parse(Metadata);j["tree"][0]["lods"][0]["file"]=1;Assert.Throws<InvalidDataException>(()=>GaussianLODMetadata.Parse(j.ToString()));}
        [Test] public void GPURecordHasExpectedStride(){Assert.AreEqual(GaussianSplat.Stride,Marshal.SizeOf<GaussianSplat>());}
        [Test] public void AllocatorCoalescesAndDetectsDoubleRelease()
        {
            var arena=new GaussianSlotAllocator(100);int a=arena.Allocate(30),b=arena.Allocate(50);Assert.AreEqual(-1,arena.Allocate(21));arena.Release(a,30);arena.Release(b,50);Assert.AreEqual(100,arena.FreeCount);Assert.Throws<InvalidOperationException>(()=>arena.Release(1,1));Assert.AreEqual(0,arena.Allocate(100));
        }
        [Test] public void CovariancePreservesAnisotropyUnderRotation()
        {
            var s=GaussianSplat.Create(Vector3.zero,new Vector3(2,1,.5f),Quaternion.Euler(0,0,90),.7f,Vector3.one);
            Assert.That(s.covarianceA.x,Is.EqualTo(1).Within(1e-5));Assert.That(s.covarianceB.x,Is.EqualTo(4).Within(1e-5));Assert.That(s.covarianceB.z,Is.EqualTo(.25).Within(1e-5));
        }
        string Fixture(bool big=false)
        {
            string p=Path.Combine(Path.GetTempPath(),"Gaussian LOD 测试 "+Guid.NewGuid()+".ply");
            using var f=File.Create(p);
            var h=new StringBuilder("ply\nformat "+(big?"binary_big_endian":"binary_little_endian")+" 1.0\ncomment reordered mixed scalar properties\nelement vertex 3\nproperty uchar ignored\nproperty double x\n");
            foreach(string n in new[]{"z","y","rot_3","rot_2","rot_1","rot_0","scale_2","scale_1","scale_0","opacity","f_dc_2","f_dc_1","f_dc_0"})h.AppendLine("property float "+n);
            for(int i=0;i<45;i++)h.AppendLine("property float f_rest_"+i);h.AppendLine("end_header");
            byte[] header=Encoding.ASCII.GetBytes(h.ToString());f.Write(header,0,header.Length);
            void Write(byte[] bytes){if(big)Array.Reverse(bytes);f.Write(bytes,0,bytes.Length);}
            for(int v=0;v<3;v++){
                f.WriteByte(42);Write(BitConverter.GetBytes(10.0+v));
                foreach(float value in new[]{30f+v,20f+v,0,0,0,1,(float)Math.Log(.5),(float)Math.Log(1),(float)Math.Log(2),0,0,0,0})Write(BitConverter.GetBytes(value));
                for(int j=0;j<45;j++)Write(BitConverter.GetBytes(j*.01f+v));
            }
            return p;
        }
        [TestCase(false)] [TestCase(true)]
        public void BinaryPLYReadsMixedPropertiesRecordRangeAndSH(bool big)
        {
            string p=Fixture(big);try{
                var h=GaussianPLYReader.ReadHeader(p);Assert.AreEqual(3,h.SHDegree);Assert.AreEqual(3,h.VertexCount);
                var data=GaussianPLYReader.ReadRange(p,h,1,1,3,CancellationToken.None);var s=data[0];
                Assert.AreEqual(11,s.positionOpacity.x);Assert.AreEqual(21,s.positionOpacity.y);Assert.AreEqual(31,s.positionOpacity.z);Assert.AreEqual(.5f,s.positionOpacity.w);
                Assert.That(s.covarianceA.x,Is.EqualTo(4).Within(1e-5));Assert.That(s.GetSH(1),Is.EqualTo(1.15f).Within(1e-6));Assert.That(s.GetSH(2),Is.EqualTo(1.30f).Within(1e-6));
                Assert.Throws<InvalidDataException>(()=>GaussianPLYReader.ReadRange(p,h,3,1,3,CancellationToken.None));
                using var c=new CancellationTokenSource();c.Cancel();Assert.Throws<OperationCanceledException>(()=>GaussianPLYReader.ReadRange(p,h,0,1,3,c.Token));
            }finally{File.Delete(p);}
        }
        [Test] public void TruncatedPLYRejected(){string p=Fixture();try{using(var f=new FileStream(p,FileMode.Open,FileAccess.Write))f.SetLength(f.Length-10);Assert.Throws<InvalidDataException>(()=>GaussianPLYReader.ReadHeader(p));}finally{File.Delete(p);}}
        [Test] public void ConverterUsesActualNativePipelineAndReleasesSource()
        {
            var j=JObject.Parse(AholoConversionJob.Pipeline("C:/中文 input.ply","C:/out stage",5,400000,true));var config=j["tasks"][1]["config"];
            Assert.AreEqual("ply",(string)config["type"]);Assert.AreEqual(400000,(int)config["maxChunkCounts"]);Assert.AreEqual(.05,(double)config["levels"][3]["precision"],1e-6);Assert.IsTrue((bool)config["levels"][4]["permanent"]);Assert.AreEqual("source",(string)j["tasks"][1]["release"][0]);
            var single=JObject.Parse(AholoConversionJob.Pipeline("a.ply","out",5,400000,false));var one=single["tasks"][1]["config"]["levels"];
            Assert.AreEqual(1,((JArray)one).Count);Assert.IsFalse((bool)one[0]["permanent"],"Single LOD must remain streamable rather than pinning the full model.");
        }
        [Test] public void ConverterRejectsNonEmptyOutputBeforeProcessStarts()
        {
            string p=Fixture();string d=Path.Combine(Path.GetTempPath(),"Gaussian-output-"+Guid.NewGuid());Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,"keep.txt"),"keep");
            try{Assert.Throws<IOException>(()=>new AholoConversionJob(GaussianLODEditorUtility.NativeExecutable,p,d,5,400000,true));Assert.AreEqual("keep",File.ReadAllText(Path.Combine(d,"keep.txt")));}finally{File.Delete(p);File.Delete(Path.Combine(d,"keep.txt"));Directory.Delete(d);}
        }
        [UnityTest] public IEnumerator NativeConverterJobCommitsValidatedPLYWithoutChangingSource()
        {
            string p=Path.Combine(Path.GetTempPath(),"Gaussian native 源 "+Guid.NewGuid()+".ply"),d=Path.Combine(Path.GetTempPath(),"Gaussian-native-test-"+Guid.NewGuid());GaussianLODDemo.WriteFixture(p,8192);byte[] before=File.ReadAllBytes(p);
            using var job=new AholoConversionJob(GaussianLODEditorUtility.NativeExecutable,p,d,5,400000,true);
            try{
                double start=Time.realtimeSinceStartupAsDouble;
                while(!job.Work.IsCompleted&&Time.realtimeSinceStartupAsDouble-start<20)yield return null;
                Assert.IsTrue(job.Work.IsCompleted);Assert.IsFalse(job.Work.IsFaulted,job.Work.Exception?.ToString());Assert.IsFalse(job.Canceled);
                var loader=new GaussianLODLoader(Path.Combine(d,"lod-meta.json"));loader.ValidateAllRanges();Assert.AreEqual(5,loader.Metadata.levels);CollectionAssert.AreEqual(before,File.ReadAllBytes(p));
            }finally{File.Delete(p);if(Directory.Exists(d)){foreach(string f in Directory.GetFiles(d))File.Delete(f);Directory.Delete(d);}if(File.Exists(job.Staging+".pipeline.json"))File.Delete(job.Staging+".pipeline.json");}
        }
    }
}
