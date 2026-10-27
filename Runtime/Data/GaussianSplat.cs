using System.Runtime.InteropServices;
using UnityEngine;
namespace GaussianSplatLOD
{
    // Shared CPU/HLSL ABI: 16 float4s, 256 bytes. SH rest is coefficient-major RGB, padded to 48 floats.
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct GaussianSplat
    {
        public const int Stride = 256;
        public Vector4 positionOpacity, covarianceA, covarianceB, color;
        public fixed float sh[48];
        public void SetSH(int i, float value) { fixed (float* p = sh) p[i] = value; }
        public float GetSH(int i) { fixed (float* p = sh) return p[i]; }
        public static GaussianSplat Create(Vector3 p, Vector3 scale, Quaternion q, float opacity, Vector3 rgb)
        {
            var r = Matrix4x4.Rotate(q);
            Vector3 x = (Vector3)r.GetColumn(0) * scale.x, y = (Vector3)r.GetColumn(1) * scale.y, z = (Vector3)r.GetColumn(2) * scale.z;
            return new GaussianSplat {
                positionOpacity = new Vector4(p.x,p.y,p.z,opacity), color = new Vector4(rgb.x,rgb.y,rgb.z,0),
                covarianceA = new Vector4(x.x*x.x+y.x*y.x+z.x*z.x,x.x*x.y+y.x*y.y+z.x*z.y,x.x*x.z+y.x*y.z+z.x*z.z,0),
                covarianceB = new Vector4(x.y*x.y+y.y*y.y+z.y*z.y,x.y*x.z+y.y*y.z+z.y*z.z,x.z*x.z+y.z*y.z+z.z*z.z,0) };
        }
    }
}
