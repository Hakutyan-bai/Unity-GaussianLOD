# 快速加载与 PICO / iPhone 部署准备

当前完成的是可在 Windows Editor 验证的加载优化。PICO 4 Ultra 的 Android/Vulkan/XR 双眼渲染和 iPhone 14 的 iOS/Metal/IL2CPP 尚未完成真机适配验证，不属于已交付的平台支持。现有 Shader 尚无 single-pass stereo 宏及逐眼协方差投影，不能把单目预览当成 VR 已支持。

## 已实现的加载优化

`GaussianPLYReader` 对 Aholo 常用的 little-endian float32 属性直接读取；其他标量类型、属性重排和 big-endian 保留通用读取路径。

`GaussianRuntimeCache` 将 PLY 预解码为与 GPU ABI 一致的 `.ply.gsbin`，保留完整 float32 协方差、位置、透明度、颜色和 SH。运行时按 JSON 的记录 offset/count 定位，分批读取并复制到有界解码数组，跳过 exp、四元数归一化、协方差生成、SH 重排。仍采用原有异步请求、共享区间缓存、整模型粗级覆盖和增量 GPU 上传。

这是**无损预计算缓存**，不压缩 GPU 记录；每点仍为 256 字节。不降低模型精度，不改变现有 Aholo JSON、分块或 LOD。完整校园缓存（126 文件）额外占用 3,363,860,128 字节。不能用其 CPU 读取速度推断移动 GPU FPS。

## 使用

1. 选择 GaussianLODRenderer，点击 **Prepare Fast Loading Cache…**；也可使用 `Tools > Gaussian Splat LOD > Prepare Fast Loading Cache`。
2. 选择已有 lod-meta.json，点击 **Prepare Cache**。任务在后台按文件和 32768 点批次执行；Cancel/关闭窗口取消，未完成临时文件删除，原 PLY 保留。
3. Renderer 开启 **Prefer Runtime Cache**（默认开），Reload。Inspector 显示 Fast cache reads / PLY decodes。
4. 桌面侧同时有原 PLY 时，根据版本、GPU stride、SH、文件长度、原 PLY 大小和修改时间判断缓存是否有效。失效/缺失自动回退 PLY；修改源模型后重新生成。修改 Prefer Runtime Cache 需 Reload。
5. **Export Cache-Only Dataset…** 导出到新的或空的目录，只复制 JSON 和 `.ply.gsbin`，避免移动发布同时携带 PLY 和缓存。原始模型目录不会删除 PLY。导出最后提交 JSON；取消的输出保留已复制文件，需另选空目录重试。

缓存专用数据集中，JSON 的 files 仍保留原 `.ply` 名称；本插件自动读取相应 `.ply.gsbin`，无需 `.ply` 本体。此部署集供本插件使用；需要 Aholo Viewer 时使用保留完整 PLY 的源目录。没有 PLY 时缓存损坏会报错，无法回退；应整目录、按版本原子更新部署数据。

缓存只保存数据，不能作为任意二进制文件加载接口。支持 little-endian CPU；Windows x64、Android ARM64 和 iPhone ARM64 采用这一端序，后两者仍需真机测试。

## 移动参数起点

Inspector 提供两档按钮，**不会自动覆盖已有桌面场景预算**：

| 参数 | PICO 4 Ultra 起始值 | iPhone 14 起始值 |
| --- | --- | --- |
| Max Visible Splats | 350,000 | 200,000 |
| Max GPU Memory | 384 MiB | 256 MiB |
| Max CPU Cache | 256 MiB | 192 MiB |
| Concurrent Loads | 2 | 2 |
| Upload Splats Per Frame | 8,192 | 8,192 |
| Preload Distance | 10 | 10 |

这不是设备安全上限或性能承诺。每模型分别占用预算，驱动、排序、托管可见索引、渲染目标、XR 双眼纹理和其他场景资源还会占用内存；移动 CPU/GPU 共用物理内存。完整粗级与 permanentFiles 超过预算仍会明确报错，不以丢失片区换取启动成功。更小上传批次降低卡顿风险，但可能延长精细 LOD 的准备时间。

PICO 建议测试 Android ARM64 + Vulkan；iPhone 测试 iOS ARM64 + Metal。原生 Aholo 转换器仍仅在 Windows Editor 离线运行，移动应用只读取转换/缓存结果。

## 数据路径

- Windows / iOS 的普通 StreamingAssets 文件目录仍支持直接随机区间读取；iOS 尚未完成 Player 构建和文件路径实测。
- Android APK 内 StreamingAssets 返回 jar URL，不能使用本插件的 FileStream 随机寻址。现在会给出明确错误，而不是把 URL 转成错误的 Windows 路径。[Unity 2022.3 说明](https://docs.unity3d.com/2022.3/Documentation/Manual/StreamingAssets.html)
- 新增 **PersistentDataDirectory**：将完整的缓存专用部署集预先放到 `Application.persistentDataPath/GaussianSplatLOD/<模型>/`，jsonPath 设置为 `GaussianSplatLOD/<模型>/lod-meta.json`。由应用的数据安装器/下载器负责首次部署；本次未实现 APK 提取、网络下载或 Android Asset Delivery。不要启动时把数 GB 全部读进 byte[]。
- 同时带 PLY 和缓存跨设备复制时要保留原 PLY 文件修改时间，否则将安全回退 PLY。缓存专用导出无需 PLY 时间戳，适合分发。

## 已执行验证

Windows Editor 2022.3.62f3c1、i7-14650HX，对用户 JN_LOD1 数据生成 126 个缓存，实际耗时 32.674 秒。读取对比先预热两条路径，然后交替执行 5 次取中位数，包含结果数组分配：

| 文件 / 测试记录数 | PLY 解码 | 缓存读取 | 比值 |
| --- | --- | --- | --- |
| chunk_0 / 81,718 | 164.929 ms | 6.742 ms | 24.46× |
| chunk_1 / 100,000 | 200.870 ms | 7.902 ms | 25.42× |
| chunk_2 / 40,826 | 81.576 ms | 3.136 ms | 26.02× |

这仅测后台 CPU + 已预热的文件系统读取，**不是整个模型启动加速倍数**，不包括 GPU 上传、渲染、冷启动闪存读取及设备热降频。原始机器报告：工程 `Reports/GaussianSplatLOD/loading-cache-benchmark.json`。可调用 `GaussianLoadingBenchmark.Begin(jsonPath)` 重跑。

单元测试覆盖完整 SH3 记录逐字节一致性、非零记录偏移、修改/截断拒绝、取消不发布、原文件保护、缓存失效/关闭回退、缓存专用导出及移动预算。真机首屏时间、峰值内存、逐眼正确性和持续帧率尚未测量。

本次 10 项针对性 EditMode 测试全部通过。另执行两轮真实 GPU 预览（Built-in / DX12、512 MiB GPU、100 万可见预算、同一校园数据），每轮包含近景、远景及非均匀/负缩放与旋转场景：

| 路径 | 完整粗级首屏 | 发布后最少覆盖分区 | 近景后台读取 |
| --- | --- | --- | --- |
| PLY（关闭缓存） | 0.258 秒 | 59 / 59 | 21 次 PLY 解码 |
| 预解码缓存 | 0.092 秒 | 59 / 59 | 21 次缓存读取，0 次 PLY 解码 |

每轮各完成 3 个非空 GPU 输出验证，并发生了 LOD 渐变；没有观察到分区覆盖丢失。数据来自一次顺序执行的 Editor 对比，操作系统文件缓存已预热，不能作为冷启动、统计稳定的性能承诺或真机结果。原始报告为 `Reports/GaussianSplatLOD/preview-builtin-loading-ply.json` 与 `preview-builtin-loading-cache.json`；测试结果为 `loading-cache-tests.json`。本次未重建 Windows Player、Android Player 或 iOS Player。

后续移动优化应优先处理逐眼投影/排序、压缩 GPU 数据布局（当前 256 字节/点）、基于屏幕覆盖的每点剔除，以及按 GPU 时间限制上传；不能仅增加 IO 并发或沿用 4096 MiB 桌面预算。
