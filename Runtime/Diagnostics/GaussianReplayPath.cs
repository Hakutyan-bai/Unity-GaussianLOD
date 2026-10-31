using UnityEngine;

namespace GaussianSplatLOD.Diagnostics
{
    /// <summary>Shared timestamp-based path for Editor and device comparisons.</summary>
    public static class GaussianReplayPath
    {
        public const string Version="SUIT-30s-v1";
        public static Pose Evaluate(double seconds,Matrix4x4 matrix,Bounds bounds)
        {
            seconds%=30;
            Vector3 center=matrix.MultiplyPoint3x4(bounds.center);float radius=Mathf.Max(20,bounds.extents.magnitude);
            Vector3 wide=center+new Vector3(.15f,.65f,-1)*radius*1.5f;
            Vector3 near=center+new Vector3(-.42f,0,-.12f)*radius;near.y=Mathf.Max(60,center.y+80);
            Vector3 other=center+new Vector3(.45f,0,.16f)*radius;other.y=near.y;
            Vector3 position,look=center;
            if(seconds<5)position=wide;
            else if(seconds<12)position=Vector3.Lerp(wide,near,(float)((seconds-5)/7));
            else if(seconds<18){position=near;look=center+Quaternion.Euler(0,Mathf.Sin((float)(seconds-12)*2.5f)*70,0)*new Vector3(0,0,radius*.3f);}
            else if(seconds<24)position=Vector3.Lerp(near,wide,(float)((seconds-18)/6));
            else if(seconds<27)position=other;
            else position=Vector3.Lerp(other,wide,(float)((seconds-27)/3));
            return new Pose(position,Quaternion.LookRotation(look-position,Vector3.up));
        }
    }
}
