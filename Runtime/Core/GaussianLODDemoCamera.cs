using System;
using System.IO;
using UnityEngine;

namespace GaussianSplatLOD
{
    public sealed class GaussianLODDemoCamera : MonoBehaviour
    {
        public GaussianLODRenderer model;
        public float moveSpeed=10;
        public float mouseSensitivity=2;
        public float sprintMultiplier=4;
        public bool lockCursorOnStart=true;
        [Range(-89,89)] public float minRotationX=10;
        [Range(-89,89)] public float maxRotationX=60;
        [Tooltip("摄像机世界坐标 Y 的最低高度。")]
        public float minHeight=60;
        [Tooltip("摄像机世界坐标 Y 的最高高度。")]
        public float maxHeight=100;
        public float RotationX => pitch;
        float pitch,yaw;
        bool cursorWasLocked;
        int ignoreMouseThroughFrame=-1;
        double started;
        int frames;
        int renderedFrames;
        bool benchmark;
        double sum,sumSquares;
        int minVisible=int.MaxValue,maxVisible;
        long maxCPU,maxGPU;
        void Start(){started=Time.realtimeSinceStartupAsDouble;benchmark=Array.IndexOf(Environment.GetCommandLineArgs(),"-gaussianBenchmark")>=0;Application.runInBackground=true;ReadAngles();if(!benchmark){ClampHeight();if(lockCursorOnStart)CaptureCursor();}}
        void OnEnable()
        {
            // Navigation can change the Transform while this controller is paused.
            ReadAngles(false);
            cursorWasLocked=false;
            ignoreMouseThroughFrame=Time.frameCount+1;
        }
        void ReadAngles(bool clampPitch=true)
        {
            NormalizePitchLimits();
            // Recover viewing direction independently of Euler roll; always keep world-up upright.
            var forward=transform.forward;
            yaw=Mathf.Atan2(forward.x,forward.z)*Mathf.Rad2Deg;
            pitch=Mathf.Asin(Mathf.Clamp(-forward.y,-1,1))*Mathf.Rad2Deg;
            if(clampPitch)pitch=Mathf.Clamp(pitch,minRotationX,maxRotationX);
            transform.rotation=Quaternion.Euler(pitch,yaw,0);
        }
        void NormalizePitchLimits()
        {
            // Migrate the old inverted 120..170 preset to equivalent upright 10..60 limits.
            if(minRotationX>=90&&maxRotationX<=180){float lower=180-maxRotationX;maxRotationX=180-minRotationX;minRotationX=lower;}
            minRotationX=Mathf.Clamp(minRotationX,-89,89);maxRotationX=Mathf.Clamp(maxRotationX,minRotationX,89);
        }
        void OnValidate(){NormalizePitchLimits();maxHeight=Mathf.Max(minHeight,maxHeight);}
        void ClampHeight()
        {
            var p=transform.position;p.y=Mathf.Clamp(p.y,minHeight,Mathf.Max(minHeight,maxHeight));transform.position=p;
        }
        void LateUpdate(){if(!benchmark)ClampHeight();}
        public void ApplyLook(float horizontal,float vertical)
        {
            yaw+=horizontal*mouseSensitivity;
            // An external look-at may be outside the manual pitch limits. Zero input must
            // preserve that view; real vertical input can bring it smoothly back in range.
            pitch=Mathf.Clamp(pitch-vertical*mouseSensitivity,Mathf.Min(pitch,minRotationX),Mathf.Max(pitch,maxRotationX));
            transform.rotation=Quaternion.Euler(pitch,yaw,0);
        }
        public void CaptureCursor(){ReadAngles(false);cursorWasLocked=true;ignoreMouseThroughFrame=Time.frameCount+1;Cursor.lockState=CursorLockMode.Locked;Cursor.visible=false;}
        public void ReleaseCursor(){cursorWasLocked=false;Cursor.lockState=CursorLockMode.None;Cursor.visible=true;}
        void ApplyMouseLook(Vector2 delta)
        {
            // Also handle cursor locking by a UI/navigation script instead of CaptureCursor.
            if(!cursorWasLocked){ReadAngles(false);cursorWasLocked=true;ignoreMouseThroughFrame=Time.frameCount+1;}
            if(Time.frameCount<=ignoreMouseThroughFrame)return;
            ApplyLook(delta.x,delta.y);
        }
        void OnDisable(){if(!benchmark)ReleaseCursor();}
        void OnApplicationFocus(bool focused){if(!focused&&!benchmark)ReleaseCursor();}
        void Update()
        {
            if(benchmark){Benchmark();return;}
            if(Input.GetKeyDown(KeyCode.Escape)){ReleaseCursor();return;}
            if(Cursor.lockState!=CursorLockMode.Locked){cursorWasLocked=false;if(Input.GetMouseButtonDown(0)&&Application.isFocused)CaptureCursor();return;}
            ApplyMouseLook(new Vector2(Input.GetAxisRaw("Mouse X"),Input.GetAxisRaw("Mouse Y")));
            var move=new Vector3(Input.GetAxisRaw("Horizontal"),(Input.GetKey(KeyCode.E)?1:0)-(Input.GetKey(KeyCode.Q)?1:0),Input.GetAxisRaw("Vertical"));
            float speed=moveSpeed*(Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift)?sprintMultiplier:1);
            // WASD follows heading only. Looking up/down must not introduce vertical movement.
            var heading=Quaternion.Euler(0,yaw,0);
            var worldMove=heading*new Vector3(move.x,0,move.z)+Vector3.up*move.y;
            transform.position+=Vector3.ClampMagnitude(worldMove,1)*speed*Time.unscaledDeltaTime;
        }
        void Benchmark()
        {
            double elapsed=Time.realtimeSinceStartupAsDouble-started;
            // Repeatable changing distance, direction, and projected footprint. Warm up five seconds.
            Vector3 center=model.Metadata!=null?model.ModelMatrix.MultiplyPoint3x4(model.Metadata.forwardBox.Bounds.center):Vector3.zero;
            float extent=model.Metadata!=null?model.Metadata.forwardBox.Bounds.extents.magnitude:12;
            float radius=extent*(elapsed<10?2:elapsed<20?20:1.5f);float angle=(float)elapsed*.17f;
            transform.position=center+new Vector3(Mathf.Sin(angle)*radius,radius*.6f,Mathf.Cos(angle)*radius);transform.LookAt(center);
            if(elapsed>5){double dt=Time.unscaledDeltaTime;frames++;if(model.VisibleSplats>0)renderedFrames++;sum+=dt;sumSquares+=dt*dt;minVisible=Math.Min(minVisible,model.VisibleSplats);maxVisible=Math.Max(maxVisible,model.VisibleSplats);maxCPU=Math.Max(maxCPU,model.CPUBytes);maxGPU=Math.Max(maxGPU,model.GPUBytes);}
            if(elapsed<30)return;
            string path=Path.Combine(Application.persistentDataPath,"GaussianLOD-benchmark.json");
            var pipeline=UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var report=new Report{status=renderedFrames>0&&string.IsNullOrEmpty(model.LastError)?"passed":"failed",pipeline=pipeline!=null?pipeline.name:"BuiltIn",renderedFrames=renderedFrames,device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),unity=Application.unityVersion,frames=frames,seconds=sum,meanFPS=frames/Math.Max(.001,sum),frameStdDevMs=1000*Math.Sqrt(Math.Max(0,sumSquares/Math.Max(1,frames)-Math.Pow(sum/Math.Max(1,frames),2))),minVisible=minVisible,maxVisible=maxVisible,maxCPUBytes=maxCPU,maxGPUBytes=maxGPU,diskReads=model.DiskReads,error=model.LastError??"",width=Screen.width,height=Screen.height,sourceCount=model.Metadata?.counts??0};
            File.WriteAllText(path,JsonUtility.ToJson(report,true));Debug.Log("GaussianLOD benchmark: "+path);enabled=false;Application.Quit();
        }
        [Serializable] sealed class Report {public string status,pipeline,device,api,unity,error;public int frames,renderedFrames,minVisible,maxVisible,width,height;public long maxCPUBytes,maxGPUBytes,diskReads,sourceCount;public double seconds,meanFPS,frameStdDevMs;}
    }
}
