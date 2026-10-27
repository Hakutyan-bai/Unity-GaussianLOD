using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace GaussianSplatLOD
{
    public sealed class GaussianPLYHeader
    {
        public long VertexCount, DataOffset;
        public int Stride, SHDegree;
        internal bool BigEndian;
        internal readonly Dictionary<string, Property> Properties = new Dictionary<string, Property>();
        internal struct Property { public int Offset, Size; public string Type; }
    }
    public static class GaussianPLYReader
    {
        static int Width(string type)
        {
            switch(type) {
                case "char": case "int8": case "uchar": case "uint8": return 1;
                case "short": case "int16": case "ushort": case "uint16": return 2;
                case "int": case "int32": case "uint": case "uint32": case "float": case "float32": return 4;
                case "double": case "float64": return 8;
                default: throw new InvalidDataException("不支持的 PLY property 类型: " + type);
            }
        }
        public static GaussianPLYHeader ReadHeader(string path)
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            var h = new GaussianPLYHeader();
            string element = null; long prefix = 0, elementCount = 0; int elementStride = 0, lines = 0;
            bool format = false, vertex = false, ended = false;
            while (f.Position < 1024*1024)
            {
                var line = new StringBuilder(); int c;
                while ((c = f.ReadByte()) >= 0 && c != 10) { if (c != 13) line.Append((char)c); if (line.Length > 16384) throw new InvalidDataException("PLY header 行过长。"); }
                if (c < 0) throw new EndOfStreamException("PLY header 缺少 end_header。");
                var t = line.ToString().Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
                if (++lines == 1) { if (line.ToString() != "ply") throw new InvalidDataException("文件不是 PLY。"); continue; }
                if (t.Length == 0 || t[0] == "comment" || t[0] == "obj_info") continue;
                if (t[0] == "format") {
                    if (t.Length != 3 || t[2] != "1.0" || (t[1] != "binary_little_endian" && t[1] != "binary_big_endian")) throw new InvalidDataException("需要 binary PLY 1.0；不支持 ASCII 或压缩 PLY。");
                    format = true; h.BigEndian = t[1] == "binary_big_endian";
                } else if (t[0] == "element") {
                    if (element != null && !vertex) prefix = checked(prefix + elementCount*elementStride);
                    if (t.Length != 3 || !long.TryParse(t[2], NumberStyles.None, CultureInfo.InvariantCulture, out elementCount) || elementCount < 0) throw new InvalidDataException("PLY element count 无效。");
                    element = t[1]; elementStride = 0;
                    if (element == "vertex") { if (vertex) throw new InvalidDataException("重复 vertex element。"); vertex = true; h.VertexCount = elementCount; }
                } else if (t[0] == "property") {
                    // Variable-size elements before vertex prevent random record addressing.
                    if (t.Length != 3) { if (element == "vertex" || !vertex) throw new InvalidDataException("不支持 vertex 前的 list 或压缩 PLY；请用转换器导出普通 PLY。"); continue; }
                    if (element != "vertex" && vertex) continue;
                    int size = Width(t[1]);
                    if (element == "vertex") {
                        if (h.Properties.ContainsKey(t[2])) throw new InvalidDataException("重复 PLY property: " + t[2]);
                        h.Properties.Add(t[2],new GaussianPLYHeader.Property{Offset=elementStride,Size=size,Type=t[1]});
                        h.Stride = elementStride + size;
                    }
                    elementStride += size;
                } else if (t[0] == "end_header") { h.DataOffset = checked(f.Position + prefix); ended = true; break; }
            }
            if (!ended || !format || !vertex || h.Stride < 1 || h.Stride > 4096) throw new InvalidDataException("PLY header 无效或超过 1 MiB。");
            foreach (string name in new[]{"x","y","z","scale_0","scale_1","scale_2","rot_0","rot_1","rot_2","rot_3","opacity","f_dc_0","f_dc_1","f_dc_2"})
                if (!h.Properties.ContainsKey(name)) throw new InvalidDataException("不是完整 3DGS PLY，缺少: " + name);
            int rest = 0; while (h.Properties.ContainsKey("f_rest_" + rest)) rest++;
            h.SHDegree = rest == 0 ? 0 : rest == 9 ? 1 : rest == 24 ? 2 : rest == 45 ? 3 : -1;
            if (h.SHDegree < 0) throw new InvalidDataException("SH 系数数量必须为 0/9/24/45，当前 " + rest);
            foreach (string name in h.Properties.Keys) if (name.StartsWith("f_rest_", StringComparison.Ordinal) && (!int.TryParse(name.Substring(7),out int idx) || idx < 0 || idx >= rest)) throw new InvalidDataException("SH 属性索引不连续。");
            if (h.DataOffset > f.Length || h.VertexCount > (f.Length - h.DataOffset)/h.Stride) throw new InvalidDataException("PLY vertex 数据被截断。");
            return h;
        }
        static unsafe float Scalar(byte* p, GaussianPLYHeader.Property a, bool big)
        {
            p += a.Offset;
            // Native Aholo output is little-endian float32. Avoid constructing every float
            // one byte at a time (59 properties per SH3 Gaussian). Keep the generic path.
            if (!big && BitConverter.IsLittleEndian && (a.Type == "float" || a.Type == "float32")) return *(float*)p;
            ulong bits = 0;
            if (big) { for(int i=0;i<a.Size;i++) bits=(bits<<8)|p[i]; }
            else { for(int i=a.Size-1;i>=0;i--) bits=(bits<<8)|p[i]; }
            switch(a.Type) {
                case "float": case "float32": uint b=(uint)bits; return *(float*)&b;
                case "double": case "float64": return (float)*(double*)&bits;
                case "char": case "int8": return (sbyte)bits;
                case "short": case "int16": return (short)bits;
                case "int": case "int32": return (int)bits;
                default: return bits;
            }
        }
        public static unsafe GaussianSplat[] ReadRange(string path, GaussianPLYHeader h, long offset, int count, int degree, CancellationToken cancellation)
        {
            if (offset < 0 || count < 0 || offset > h.VertexCount - count) throw new InvalidDataException("PLY 高斯记录区间越界: " + path);
            if (h.SHDegree != degree) throw new InvalidDataException($"PLY SH degree {h.SHDegree} 与 JSON {degree} 不一致: {path}");
            cancellation.ThrowIfCancellationRequested();
            var output = new GaussianSplat[count];
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,65536,FileOptions.RandomAccess);
            f.Position = checked(h.DataOffset + offset*h.Stride);
            string[] names = {"x","y","z","scale_0","scale_1","scale_2","rot_0","rot_1","rot_2","rot_3","opacity","f_dc_0","f_dc_1","f_dc_2"};
            int coefficients=(degree+1)*(degree+1)-1;
            var properties = new GaussianPLYHeader.Property[14 + coefficients*3];
            for(int i=0;i<14;i++) properties[i]=h.Properties[names[i]];
            for(int i=0;i<coefficients*3;i++) properties[14+i]=h.Properties["f_rest_"+i];
            var values = new float[properties.Length];
            int batch = Math.Max(1,65536/h.Stride);
            var bytes = new byte[batch*h.Stride];
            for(int start=0;start<count;start+=batch)
            {
                cancellation.ThrowIfCancellationRequested();
                int n=Math.Min(batch,count-start), size=n*h.Stride, read=0;
                while(read<size) { int got=f.Read(bytes,read,size-read); if(got==0)throw new EndOfStreamException(path); read+=got; }
                fixed(byte* data=bytes) for(int j=0;j<n;j++)
                {
                    for(int a=0;a<properties.Length;a++) {
                        float v=Scalar(data+j*h.Stride,properties[a],h.BigEndian);
                        // Infinite opacity logits are valid endpoints in native PLY output.
                        if(!GaussianBox.Finite(v) && !(a==10 && !float.IsNaN(v))) throw new InvalidDataException($"PLY 非有限属性，记录 {offset+start+j}, property {a}。");
                        values[a]=v;
                    }
                    Vector3 scale = new Vector3((float)Math.Exp(values[3]),(float)Math.Exp(values[4]),(float)Math.Exp(values[5]));
                    if(!GaussianBox.Finite(scale.x*scale.x)||!GaussianBox.Finite(scale.y*scale.y)||!GaussianBox.Finite(scale.z*scale.z))throw new InvalidDataException("PLY scale 溢出。");
                    double norm=Math.Sqrt((double)values[6]*values[6]+(double)values[7]*values[7]+(double)values[8]*values[8]+(double)values[9]*values[9]);
                    if(norm<1e-12)throw new InvalidDataException("PLY 四元数长度为零。");
                    Quaternion q=new Quaternion((float)(values[7]/norm),(float)(values[8]/norm),(float)(values[9]/norm),(float)(values[6]/norm));
                    var s=GaussianSplat.Create(new Vector3(values[0],values[1],values[2]),scale,q,(float)(1/(1+Math.Exp(-values[10]))),new Vector3(.5f+.2820947918f*values[11],.5f+.2820947918f*values[12],.5f+.2820947918f*values[13]));
                    for(int c=0;c<coefficients;c++) for(int rgb=0;rgb<3;rgb++) s.SetSH(c*3+rgb,values[14+rgb*coefficients+c]);
                    output[start+j]=s;
                }
            }
            return output;
        }
    }
}
