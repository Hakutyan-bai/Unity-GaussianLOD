using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianLifecycleTests
    {
        [UnityTest] public IEnumerator UnloadDuringBackgroundMetadataLoadIsIdempotent()
        {
            if(!File.Exists(Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo/lod-meta.json")))Assert.Ignore("Create Demo first.");
            var go=new GameObject("LOD lifetime test");var r=go.AddComponent<GaussianLODRenderer>();
            r.Load();r.Unload();r.Unload();yield return null;
            Assert.AreEqual(0,r.GPUBytes);Assert.AreEqual(0,r.CPUBytes);Assert.AreEqual("Unloaded",r.Status);Object.Destroy(go);yield return null;
        }
    }
}
