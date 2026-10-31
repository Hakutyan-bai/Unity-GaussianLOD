# PICO 4 Ultra：加载、XR 与性能测试

本轮目标是 PICO 独立 Android 运行、90 Hz（11.11 ms 帧预算）。90 Hz 是调度目标，不是已取得的性能结果。主数据集为 SUIT-20261027：7,214,556 源高斯、52 个空间节点、141 个 PLY、5 级真实 LOD、SH3。不同 LOD 的点数不能相加作为源点数。

## 保留现有场景

Demo、DemoIOS 的漫游、目标点、坐标手性和用户画质设置不需要改动。测试使用独立 DemoPICO。它不会给每个高斯创建 GameObject，也不需要 PICO SDK。桌面/移动原控制器不挂到头部相机；XR 跟踪负责相机局部姿态，移动或回放负责 XR Origin。

OpenXR 安装使用 Unity 官方 1.16.1（Unity 2022.3 LTS+）；本项目解析为 XR Management 4.4.0、Core Utils 2.5.2、Input System 1.14.0。保留旧输入的同时启用新输入。测试场景主动初始化 XR；不要给整个项目设置自动 XR 启动，否则原 DemoIOS 也会尝试初始化头显。

## 模型准备与部署

1. 打开 `Tools > Gaussian Splat LOD > PICO Deployment`，选择 SUIT 的 `lod-meta.json`，点击 Prepare 生成无损 `.gsbin` 和 `deployment-index.json`（Editor API：`GaussianDeploymentBuilder.BeginPrepare(json)`，可查询 IsBusy/Progress/Status/Error）。SH3 和 256 字节 GPU 记录保持原样，PLY 不覆盖。
2. 优先使用缓存专用导出（`GaussianDeploymentBuilder.ExportCacheOnly(json, destination, cancellation)`，只接受新目录或空目录，JSON 最后提交）。仅部署 JSON、部署索引与 `.gsbin`，可避免 PLY 与缓存共同占用双份磁盘空间。原始 PLY 保留在制作电脑中；缺失或失效缓存的数据可选择 PLY 回退导出。
3. 设备上建议将完整模型放在 `Application.persistentDataPath/GaussianSplatLOD/SUIT-20261027/`，并在 Renderer 中选择 PersistentDataDirectory、相对 JSON 路径 `GaussianSplatLOD/SUIT-20261027/lod-meta.json`。不要写入 Windows 的 `G:` 路径。
4. 需要随 APK 携带数据时，保持 StreamingAssets 内原始结构。插件按需要通过 UnityWebRequest 将文件落到 persistent 缓存，校验成功后才供随机读取；不会将数 GB 数据装入 DownloadHandlerBuffer。
5. 数据首次安装、首次进程启动与已存在本地缓存的启动应分别记录。取消安装产生的临时文件不会作为有效缓存使用；磁盘不足与校验失败会显示明确原因。

本轮不自动执行 APK Build。SUIT 已生成 141 份缓存，总计 3,341,573,232 字节；原始 PLY 合计 3,080,718,255 字节，两套共约 6.42 GB（5.98 GiB）。当前制作目录同时保留两套数据。Unity 会把所有 StreamingAssets 文件打包，与 Build Settings 是否使用该场景无关。操作者发布前应先用上述窗口导出缓存专用目录，再将制作数据移出 StreamingAssets，按选定方式部署缓存；插件不会擅自删除或移动原始数据。模型较大时优先使用设备 persistent 目录部署，并检查设备可用磁盘空间。

## 推荐起始配置

仅新建的 PICO 测试场景使用这些初始值，不覆盖已有场景：GPU 640 MiB、CPU 384 MiB、最多驻留 200 万点、最多可见 100 万点；IO/解析/上传并发 2/1/1；每帧上传 0.75 ms、最多 4 MiB；预加载 20 m、预测 1 s、最大预测距离 60 m；缓存保留 10 s；LOD 迟滞 0.25 s、渐变 0.4 s；近景保护 40 m。

SUIT 的永久记录约 106.21 MiB，最大单区间约 93.53 MiB。CPU 预算还需要容纳正在读取的结果与临时缓冲；并发 2 不代表能够不受预算地同时读取两个最大区间。驻留容量与可见排序容量分开计算，避免旧 350k 预设无法容纳 383,095 点的 LOD0 区间。

近景保护优先于自动远景细节调整，但不能突破显存和完整切块的硬限制。预算不足时会保留可用 LOD 并说明原因；不能依赖切掉部分高斯、降低 SH 或隐藏空间区域取得表面性能。

## Inspector 参数说明

| 分组 / 字段 | 含义及建议 |
|---|---|
| Data Location / JSON Path | 本地 Editor、StreamingAssets 或 PersistentDataDirectory；后两者填写相对 JSON 路径。Prefer Runtime Cache 修改后 Reload。 |
| Streaming Origin | 指向 XR Origin；速度预测使用它的平移，视野仍使用头部相机。不要指向会随转头平移的控制器。 |
| Preload Distance / Prediction Seconds / Max Prediction Distance | 预测走廊额外距离、预测时间和最大长度；默认 20 m / 1 s / 60 m。 |
| Teleport Distance / Selection Interval / Head Turn Threshold | 默认 20 m / 0.05 s / 10°；瞬移撤销过期请求，明显转头立即重排。 |
| LOD Pixel Threshold / LOD Hysteresis / LOD Transition Seconds | 屏幕投影细节阈值、目标稳定时间、光学深度渐变时间。迟滞字段单位为秒，默认 0.25 s；渐变 0.4 s。 |
| Near Quality Distance / Coherent LOD | 40 m 内优先近景细节；共同精度推进减少建筑之间突兀的清晰度差异。硬预算仍有效。 |
| Priority Weights | 视野、距离、预测、细化收益、缓存与等待时间权重；可见请求高于纯预取，成本项避免大请求长期阻塞队列。 |
| Max Concurrent Loads / IO / Decodes / Uploads | 总在途请求及各阶段限流；起点 2 / 2 / 1 / 1。提高并发同时需要提高内存余量。 |
| GPU Upload Budget Milliseconds / Upload Bytes Per Frame | 主线程提交软时间预算 0.75 ms，硬字节上限 4 MiB；另受 Upload Splats Per Frame 和 Max Upload Submissions 限制。单次驱动调用不能被中途打断，因此实际耗时可略超时间值。 |
| Max CPU Cache / Max GPU Memory | MiB；CPU 统计包含解码数组及在途预约，未包含整个 Unity 进程、元数据或所有池化对象。GPU 统计为插件缓冲估算，不代表设备全部显存。 |
| Max Resident Splats / Max Visible Splats | 驻留 200 万、可见排序 100 万；两个上限独立。0 驻留表示按预算自动规划。硬容量修改可能等待 GPU 安全点后重建；自动调度不反复修改硬容量。 |
| Cache Retention / High / Low Watermark | 保留 10 s；CPU 高水位 90% 回收到 80%，紧急分配时可提前回收。GPU arena 保持预分配，槽位按需求 LRU 回收。永久、当前绘制和渐变资源受保护。 |
| Prefetch Memory Fraction | 在途与未使用预取最多占 CPU 预算的比例，默认 20%；前台细化需要内存时优先让出。 |
| Enable Performance Scheduling / Target Refresh Rate | 默认 PICO 启用、90 Hz；每 0.5 s 结合实际 CPU/GPU 时间、上传、积压与内存压力调整软预算。GPU 计时不可用时不根据 FPS 推断 GPU 瓶颈。 |

调节画质、预测和软预算不需要重新导入数据。模型布局、缓存格式、路径、缓存优先选择等需要 Reload 的字段由对应提示说明。

## 桌面对照测试

菜单 `Tools > Gaussian Splat LOD > Performance` 提供：

- SUIT PLY Replay：3 轮，每轮 30 秒。
- SUIT Cache Replay：相同路径、参数和时间。
- SUIT 20 Minute Soak：相同 30 秒路径循环 20 分钟。
- Cancel Replay：取消并恢复先前管线与被临时暂停的 Renderer。

Editor 测试使用独立 Preview Scene、URP14、1280×720、100 万可见点、640/384 MiB GPU/CPU，测量期间不截图、不更改用户场景。路线依次包含全景静止、前进、快速左右转向、后退、瞬移和返回。按时间戳取样，不按帧数移动。重编译会中断测试，测试时不要同时编辑脚本或更换 Package。

报告在 `Reports/PICO/`：汇总 JSON、每帧 CSV 和 samples JSON；长期运行每 60 秒更新 `*.progress.json`。文件系统页缓存未清除，必须称为 Windows Editor 对照，不能称为设备 FPS 或冷存储结果。GPU 时长为 `-1` 表示不可用，不能用渲染提交 Stopwatch 值冒充 GPU 时间。

原系统基线 `baseline-original-ply.json` 已实测三轮：完整粗级覆盖分别 0.795、0.903、0.824 秒，覆盖持续保持 52 节点；每轮 44 次 PLY 读取。Editor iteration P95 约 105–114 ms，包含编辑器节流与提交开销，不作为 Player FPS。该结果不是 PICO 实测。

短测、27 次真实范围读取微基准、真实 383,095 点 LOD0 固定视图比较与长测限制见 [PICO-Validation.md](PICO-Validation.md)。最终 78 项 EditMode 和 2 项 PlayMode 测试通过，包含缺文件时完整覆盖及健康区域继续细化。原离屏 DX12 长测崩溃，DX11 离屏长测有一次诊断停顿；随后正常 Play Mode DX12 补测完成 1200.689 秒、52 节点覆盖且无错误，约 10 Hz 记录的最大间隔 0.225 秒。该正常补测的进程私有内存首末分钟仍增长约 820 MiB，不能宣称内存无泄漏；所有结果均为桌面单眼，不能推断 PICO 90 Hz。

## 真机测试步骤

1. 开启设备开发者模式与 USB 调试，使用 Android SDK 中的 `adb devices -l` 确认设备已授权。记录系统版本、OpenXR runtime、图形 API、设备温度、刷新率与眼纹理尺寸。
2. 在 Unity Android 配置中选择 ARM64、Vulkan、Minimum API Level 至少 24；检查官方 OpenXR 的 Project Validation。Build Settings 中由操作者启用独立 DemoPICO 场景；不会自动替换原 DemoIOS。测试入口的 Editor 打包 hook 仅对 Android 且启用 DemoPICO 的构建生效：确保 API 24 下限，并在生成的 manifest 的 application 层合并 `pvr.app.type=vr`，保留原权限、activity 与其他 metadata；未实际构建 APK，需操作者检查生成物。不要为头部相机挂 FPS 鼠标脚本。当前测试资源启用 KHR Simple Controller Profile，它没有标准摇杆轴定义；设备 runtime、手柄交互 profile 和实际摇杆映射需要真机补验，不能仅根据桌面 WASD 或包安装认定控制器已可用。
3. 由操作者构建、安装测试 APK，将相同版本的数据部署到 persistent 目录。开启开发构建、Profiler 连接和 Frame Timing Stats。确认两眼都显示、右/左眼边缘节点都不会被裁掉，转头时椭圆、颜色与深度一致。
4. 先不开自动降级，以同数据、同画质预算记录 PLY 与 `.gsbin`；再单独测试自适应模式。每种短测至少三轮，首安装与重复启动分组。
5. Renderer 挂载 `GaussianStreamingDebugPanel`，开启 World Space 并指定 XR 相机。面板显示帧/CPU/GPU、排队/读取/解析/上传数、均值/P95、缓存/预取命中、内存及调度原因；关闭后不刷新文本。桌面 F8 可切换。
6. `GaussianBenchmarkReplay` 指定 Renderer、XR 相机和 XR Origin。默认不自动运行；在测试 UI 或脚本中调用 `Begin()`，20 分钟测试设 Duration Seconds=1200。回放只驱动 Origin，不覆盖真实头部跟踪；实际头部位置与角度会写日志。因此人工转头试验须结合实际姿态记录比较，而不能称逐像素一致的自动双眼回放。
7. CSV 保存在 `Application.persistentDataPath/GaussianSplatLODReports/`，后台批量写入。退出或停止会排空已接收队列；队列上限 2048，丢样数和写入错误可读取。通过 `adb pull` 取回对应应用目录日志；Android 权限限制时用开发构建 `run-as <包名>` 导出。
8. 持续前进、快速转头、多栋建筑切换、近远 LOD、折返缓存、瞬移分别观察；再连续漫游 20 分钟。比较预热后 CPU/GPU 缓存、Unity 分配内存以及 Android 进程 PSS；Unity allocated 不等于进程 PSS。
9. 在测试数据副本中验证缺文件、损坏缓存、取消、磁盘空间不足；记录失败区域是否维持粗级以及其他区域能否继续细化。不要破坏唯一的原始 PLY。

## 结果解释

- 上传提交耗时高：降低后台每帧上传预算，检查主线程等待和驱动同步。
- CPU 排队/解析高：核对是否命中 `.gsbin`，对比 IO 与解析时间及并发预约。
- GPU 时间高、IO 空闲：检查 GPU 排序和透明 overdraw；提高读取并发不会解决渲染饱和。
- 缓存反复淘汰：检查驻留上限、临时缓冲与保留时间，而非仅提高并发。
- GPU 计时不可用：标为 N/A，并结合 Unity Profiler/设备工具分析；不能把全部低帧率归因于磁盘。

CPU Frame / Main / Render 和 GPU 时间取自实际 FrameTimingManager，缺失时显示 N/A；应用帧间隔与 FPS 单独统计。应用帧间隔包含等待、节流和其他工作，不能直接当作 CPU 有效工作时间。`LastUploadMilliseconds` 保留原有兼容口径（整个 streaming tick）；Actual Upload 单独统计真正的上传提交。

在设备连接、APK 安装及完整测试之前，PICO 双眼画面、90 Hz 是否达成、控制器绑定、存储冷启动和长期内存稳定性均属于待验证项。

当前 Built-in 支持单眼和 Multi Pass；旧式 double-wide Single Pass 没有作为支持路径。URP 的 SPI / Multiview 路径已补齐每眼矩阵、观察方向、实例索引和层输出，但 Android Vulkan Multiview 仍需设备验证。双眼平行时共用排序，非平行视轴使用独立顺序缓冲。不能用桌面纹理数组单元测试替代头显画面验收。
