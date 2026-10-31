using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianCameraInputTests
    {
        [UnityTest] public IEnumerator ReacquiringCursorKeepsViewAndIgnoresWarpUntilInputSettles()
        {
            var go=new GameObject("FPS cursor regression");
            var camera=go.AddComponent<GaussianLODDemoCamera>();
            // Supply deterministic mouse deltas without OS focus or unrelated keyboard input.
            camera.enabled=false;
            var mouse=typeof(GaussianLODDemoCamera).GetMethod("ApplyMouseLook",BindingFlags.Instance|BindingFlags.NonPublic);
            Assert.IsNotNull(mouse);
            try{
                go.transform.rotation=Quaternion.Euler(4,145,0);
                var arrival=go.transform.rotation;
                camera.enabled=true;camera.ApplyLook(0,0);
                Assert.That(Quaternion.Angle(arrival,go.transform.rotation),Is.LessThan(.01),"Runtime OnEnable must synchronize navigation angles.");
                camera.enabled=false;
                camera.CaptureCursor();arrival=go.transform.rotation;
                mouse.Invoke(camera,new object[]{new Vector2(400,-300)});
                Assert.That(Quaternion.Angle(arrival,go.transform.rotation),Is.LessThan(.01));
                yield return null;
                mouse.Invoke(camera,new object[]{new Vector2(-300,400)});
                Assert.That(Quaternion.Angle(arrival,go.transform.rotation),Is.LessThan(.01),"The first frame after locking still contains cursor warp.");
                yield return null;
                mouse.Invoke(camera,new object[]{new Vector2(1,0)});
                Assert.That(Mathf.DeltaAngle(145,go.transform.eulerAngles.y),Is.EqualTo(2).Within(.01),"Normal look must resume after settling.");
                Assert.That(camera.RotationX,Is.EqualTo(4).Within(.001));

                camera.ReleaseCursor();go.transform.rotation=Quaternion.Euler(35,-100,0);arrival=go.transform.rotation;
                // DestinationCameraMover locks the cursor directly after resuming controls.
                Cursor.lockState=CursorLockMode.Locked;
                mouse.Invoke(camera,new object[]{new Vector2(500,500)});
                Assert.That(Quaternion.Angle(arrival,go.transform.rotation),Is.LessThan(.01),"External locking must also preserve the current pose.");
                yield return null;yield return null;
                mouse.Invoke(camera,new object[]{Vector2.zero});
                Assert.That(Quaternion.Angle(arrival,go.transform.rotation),Is.LessThan(.01),"Old angles must not return once the guard expires.");
            }finally{camera.ReleaseCursor();Object.Destroy(go);}
        }
    }
}
