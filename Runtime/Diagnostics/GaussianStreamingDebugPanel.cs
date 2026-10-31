using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace GaussianSplatLOD.Diagnostics
{
    [DisallowMultipleComponent]
    public sealed class GaussianStreamingDebugPanel : MonoBehaviour
    {
        public GaussianLODRenderer model;
        public Camera viewingCamera;
        public bool visible=true;
        public bool worldSpace;
        [Range(.1f,2)] public float refreshSeconds=.25f;
        public KeyCode toggleKey=KeyCode.F8;
        [Min(.5f)] public float viewingDistance=2;
        Canvas canvas;Text text;Font font;
        readonly StringBuilder builder=new StringBuilder(1400);
        readonly GaussianMetricReader reader=new GaussianMetricReader();
        double nextRefresh;
        void EnsurePanel()
        {
            if(canvas!=null)return;
            var panel=new GameObject("Gaussian Streaming Diagnostics",typeof(RectTransform),typeof(Canvas));panel.transform.SetParent(transform,false);
            canvas=panel.GetComponent<Canvas>();canvas.sortingOrder=500;
            var body=new GameObject("Statistics",typeof(RectTransform),typeof(Image));body.transform.SetParent(panel.transform,false);
            var rect=body.GetComponent<RectTransform>();rect.sizeDelta=new Vector2(760,510);rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(0,1);rect.anchoredPosition=new Vector2(16,-16);
            var background=body.GetComponent<Image>();background.color=new Color(.015f,.03f,.05f,.88f);background.raycastTarget=false;
            var label=new GameObject("Text",typeof(RectTransform),typeof(Text));label.transform.SetParent(body.transform,false);
            rect=label.GetComponent<RectTransform>();rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=new Vector2(18,14);rect.offsetMax=new Vector2(-18,-14);
            text=label.GetComponent<Text>();text.raycastTarget=false;text.fontSize=21;text.horizontalOverflow=HorizontalWrapMode.Wrap;text.verticalOverflow=VerticalWrapMode.Overflow;text.color=new Color(.8f,.94f,1);
            font=Font.CreateDynamicFontFromOSFont(new[]{"Roboto","Noto Sans","Arial"},21);text.font=font;
            ConfigureCanvas();
        }
        void ConfigureCanvas()
        {
            if(worldSpace){canvas.renderMode=RenderMode.WorldSpace;var rect=(RectTransform)canvas.transform;rect.sizeDelta=new Vector2(800,550);rect.localScale=Vector3.one*.0018f;}
            else{canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.transform.localScale=Vector3.one;}
        }
        void Update()
        {
            if(Input.GetKeyDown(toggleKey))visible=!visible;
            if(!visible){if(canvas!=null&&canvas.gameObject.activeSelf)canvas.gameObject.SetActive(false);return;}
            if(model==null)return;
            EnsurePanel();if(!canvas.gameObject.activeSelf)canvas.gameObject.SetActive(true);
            if((canvas.renderMode==RenderMode.WorldSpace)!=worldSpace)ConfigureCanvas();
            double now=Time.realtimeSinceStartupAsDouble;if(now<nextRefresh)return;nextRefresh=now+refreshSeconds;
            reader.Refresh(model);builder.Clear();
            builder.Append("GAUSSIAN STREAMING  |  ").Append(model.Status).AppendLine();
            builder.Append("FPS ").Append(Ms(reader.Number("FPS"))).Append("   CPU ").Append(Ms(reader.Number("CPUFrameMilliseconds"))).Append(" ms   GPU ").Append(Ms(reader.Number("GPUMilliseconds"))).AppendLine(" ms");
            builder.Append("Splats ").Append(model.VisibleSplats.ToString("N0")).Append("   visible ranges ").Append(model.VisibleChunks).Append("   resident ").Append(model.ResidentRanges).AppendLine();
            builder.Append("Coverage ").Append(model.CoveredNodes).Append('/').Append(model.Metadata?.tree.Length??0).Append("   transitions ").Append(model.TransitioningNodes).AppendLine();
            builder.Append("Queued ").Append(Ms(reader.Number("QueueCount"))).Append("  IO ").Append(Ms(reader.Number("ReadingCount"))).Append("  decode ").Append(Ms(reader.Number("DecodingCount"))).Append("  upload ").Append(Ms(reader.Number("UploadingCount"))).AppendLine();
            builder.Append("Load mean/P95 ").Append(Ms(reader.Number("MeanLoadMilliseconds"))).Append('/').Append(Ms(reader.Number("P95LoadMilliseconds"))).AppendLine(" ms");
            builder.Append("Schedule ").Append(Ms(reader.Number("SchedulingMilliseconds"))).Append(" ms  upload submit ").Append(Ms(reader.Number("UploadMilliseconds"))).AppendLine(" ms");
            builder.Append("CPU cache/reserved ").Append((model.CPUBytes/1048576.0).ToString("F1")).Append(" / ").Append(model.maxCPUCache).AppendLine(" MiB");
            builder.Append("GPU allocated estimate ").Append((model.GPUBytes/1048576.0).ToString("F1")).Append(" / ").Append(model.maxGPUMemory).AppendLine(" MiB");
            builder.Append("Cache hit ").Append(Percent(reader.Number("CacheHitRate"))).Append("  preload hit ").Append(Percent(reader.Number("PreloadHitRate"))).AppendLine();
            builder.Append("Reads ").Append(model.DiskReads).Append("  binary ").Append(model.RuntimeCacheReads).Append("  PLY ").Append(model.PLYReads).AppendLine();
            builder.Append("Cancelled ").Append(Ms(reader.Number("CancelledRequests"))).Append("  dropped ").Append(Ms(reader.Number("DroppedResults"))).AppendLine();
            builder.Append("IO/decode total ").Append(Ms(reader.Number("IOReadMilliseconds"))).Append('/').Append(Ms(reader.Number("DecodeMilliseconds"))).AppendLine(" ms");
            builder.Append("Policy: ").Append(reader.Text("PerformanceReason"));
            if(!string.IsNullOrEmpty(model.LastError))builder.AppendLine().Append(model.LastError);
            text.text=builder.ToString();
        }
        static string Ms(double n)=>n<0?"N/A":n.ToString("F1");
        static string Percent(double n)=>n<0?"N/A":n.ToString("P1");
        void LateUpdate()
        {
            if(!visible||!worldSpace||canvas==null)return;
            var camera=viewingCamera!=null?viewingCamera:Camera.main;if(camera==null)return;
            var head=camera.transform;canvas.transform.SetPositionAndRotation(head.position+head.forward*viewingDistance-head.right*.72f+head.up*.47f,head.rotation);
        }
        void OnDisable(){if(canvas!=null)canvas.gameObject.SetActive(false);}
        void OnDestroy(){if(canvas!=null)Destroy(canvas.gameObject);if(font!=null)Destroy(font);}
    }
}
