Unity Gaussian Splat LOD 的首个桌面预览版，包版本 `0.1.0`，Git 标签 `v0.1`。

### 下载与安装

- **Unity-GaussianLOD-v0.1.unitypackage**：Unity 2022.3 先安装 URP 14、Newtonsoft JSON；使用 Assets → Import Package → Custom Package 导入。包含合成 Demo 场景和小型 SH3 数据。
- **Unity-GaussianLOD-UPM-v0.1.zip**：解压后在 Package Manager 使用 Add package from disk，选择 package.json；依赖由 UPM 解析。
- **SHA256SUMS.txt**：发行附件校验值。
- 也可在 Package Manager 使用 Git URL：`https://github.com/Hakutyan-bai/Unity-GaussianLOD.git#v0.1`。

Assets 与 UPM 安装方式选择一种，避免重复程序集。资源包的示例场景位于 `Assets/UnityGaussianSplatLOD/Samples/Demo/Demo.unity`；合成数据位于 StreamingAssets。URP 工程先执行 Tools → Gaussian Splat LOD → Install URP Render Feature。

### 功能

- Aholo native 2.1.0 的 PLY 空间分块、真实高斯融合 LOD、索引 JSON 生成及取消/输出目录保护。
- Aholo lod-splat JSON、binary PLY、SH0–3、共享记录区间及 permanentFiles。
- 真正的协方差 Gaussian 椭圆、SH 颜色、GPU 深度排序、预乘 Alpha 渲染。
- CPU/GPU 有界缓存、异步读取、增量上传、整模型粗级覆盖和渐变 LOD。
- 无损预解码快速加载缓存及缓存专用部署导出。
- 自定义 Inspector、转换窗口、合成 Demo、FPS 漫游。
- 随包保留 Aholo/EGS MIT 与原生第三方许可证。运行时无 Node、浏览器或 Web Viewer 依赖。

### 验证与限制

已验证 Unity 2022.3.62f3c1 / Windows x64 / DX12；提供 Built-in 和 URP 14 路径。发布前 UPM Editor 测试 38/38 通过；资源包另经独立工程导入检查，具体结果见仓库 Validation/release-validation.json。

已有 726 万源高斯、126 PLY、59 分区校园数据完成桌面预览验证。Windows Editor 预热后单次顺序对比，完整粗级首屏由 PLY 0.258 秒降至缓存 0.092 秒，保持 59/59 覆盖；不是全部精细 LOD 加载时间或移动端性能承诺。

**PICO 4 Ultra 的 Vulkan/XR 双眼适配、iPhone 14 的 Metal/IL2CPP 尚未完成真机验证；本版本不能作为已验证的移动/XR 插件使用。** 不支持 WebGL、HDRP、压缩 PLY/ESZ/SPZ/SOG。多模型分别排序。

发行包不包含用户校园模型、私有数据、独立相机导航 UI 或宿主工程的其他渲染插件。
