// Aholo Chunk LOD 1.0 schema, Copyright © 2026 MANYCORE, INC. MIT; see ThirdPartyNotices.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GaussianSplatLOD
{
    [Serializable] public sealed class GaussianBox
    {
        public float[] min, max;
        [JsonIgnore] public Bounds Bounds => new Bounds((Min + Max) * .5f, Max - Min);
        [JsonIgnore] public Vector3 Min => new Vector3(min[0], min[1], min[2]);
        [JsonIgnore] public Vector3 Max => new Vector3(max[0], max[1], max[2]);
        public void Validate(string at)
        {
            if (min == null || max == null || min.Length != 3 || max.Length != 3) throw new InvalidDataException(at + ": min/max 必须是三元数组。");
            for (int i = 0; i < 3; i++) if (!Finite(min[i]) || !Finite(max[i]) || min[i] > max[i]) throw new InvalidDataException(at + ": 无效或非有限包围盒。");
        }
        internal static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
    }
    [Serializable] public sealed class GaussianLODRange
    {
        public int file;
        public long offset;
        public int count;
        [JsonIgnore] public GaussianRangeKey Key => new GaussianRangeKey(file, offset, count);
    }
    [Serializable] public sealed class GaussianLODNode
    {
        public GaussianBox bound;
        public GaussianLODRange[] lods;
    }
    [Serializable] public sealed class GaussianLODMetadata
    {
        public int magicCode;
        public string type, version;
        public long counts;
        public int shDegree, levels;
        public GaussianBox forwardBox;
        public string[] files;
        public int[] permanentFiles;
        public GaussianLODNode[] tree;
        public void Validate()
        {
            if (magicCode != 2500660) throw new InvalidDataException("magicCode 必须为 2500660。");
            if (type != "lod-splat" || version != "1.0") throw new InvalidDataException("仅支持 Aholo lod-splat version 1.0。");
            if (counts < 0 || shDegree < 0 || shDegree > 3 || levels < 1 || levels > 32) throw new InvalidDataException("counts / shDegree / levels 无效。");
            if (files == null || files.Length == 0 || tree == null || tree.Length == 0 || tree.Length > 1000000 || permanentFiles == null || forwardBox == null) throw new InvalidDataException("缺少 files/tree/permanentFiles/forwardBox。");
            forwardBox.Validate("forwardBox");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                string p = files[i];
                if (string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) || p.Contains(":") || p.Contains("\\") || Array.IndexOf(p.Split('/'), "..") >= 0 || !p.EndsWith(".ply", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"files[{i}]: 需要模型目录中的相对 PLY 路径，当前版本不解码 ESZ/SPZ/SOG。");
                if (!paths.Add(p)) throw new InvalidDataException("files 包含重复路径: " + p);
            }
            foreach (int p in permanentFiles) if (p < 0 || p >= files.Length) throw new InvalidDataException("permanentFiles 索引越界。");
            for (int n = 0; n < tree.Length; n++)
            {
                var node = tree[n];
                if (node?.bound == null || node.lods == null || node.lods.Length != levels) throw new InvalidDataException($"tree[{n}]: bound 缺失或 LOD 数量不等于 levels。");
                node.bound.Validate($"tree[{n}].bound");
                foreach (var r in node.lods)
                    if (r == null || r.file < 0 || r.file >= files.Length || r.offset < 0 || r.count < 0 || r.offset > long.MaxValue - r.count)
                        throw new InvalidDataException($"tree[{n}]: file/offset/count 无效。");
            }
            // counts is the original L0 count, not a sum of LOD storage. Native opacity pruning can reduce written L0.
        }
        public static GaussianLODMetadata Parse(string json)
        {
            try
            {
                var root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                foreach (string field in new[] { "magicCode", "type", "version", "counts", "shDegree", "levels", "forwardBox", "files", "permanentFiles", "tree" })
                    if (root[field] == null || root[field].Type == JTokenType.Null) throw new InvalidDataException("JSON 缺少字段: " + field);
                foreach (var node in (JArray)root["tree"])
                {
                    if (node["bound"] == null || !(node["lods"] is JArray)) throw new InvalidDataException("tree 节点缺少 bound/lods。");
                    foreach (var r in (JArray)node["lods"]) foreach (string f in new[] { "file", "offset", "count" })
                        if (r[f]?.Type != JTokenType.Integer) throw new InvalidDataException("LOD 区间缺少整数字段: " + f);
                }
                foreach (string f in new[] { "magicCode", "counts", "shDegree", "levels" })
                    if (root[f].Type != JTokenType.Integer) throw new InvalidDataException(f + " 必须是整数。");
                var m = root.ToObject<GaussianLODMetadata>(); m.Validate(); return m;
            }
            catch (InvalidDataException) { throw; }
            catch (Exception e) { throw new InvalidDataException("Aholo JSON 解析失败: " + e.Message, e); }
        }
    }
    public readonly struct GaussianRangeKey : IEquatable<GaussianRangeKey>
    {
        public readonly int File, Count;
        public readonly long Offset;
        public GaussianRangeKey(int file, long offset, int count) { File = file; Offset = offset; Count = count; }
        public bool Equals(GaussianRangeKey b) => File == b.File && Offset == b.Offset && Count == b.Count;
        public override bool Equals(object o) => o is GaussianRangeKey b && Equals(b);
        public override int GetHashCode() => unchecked((File * 397 ^ Offset.GetHashCode()) * 397 ^ Count);
        public override string ToString() => $"file={File}, offset={Offset}, count={Count}";
    }
}
