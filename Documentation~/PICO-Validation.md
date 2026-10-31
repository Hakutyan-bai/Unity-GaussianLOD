# SUIT / PICO 优化验证记录

记录日期：2026-10-31。运行环境为 Unity 2022.3.62f3c1、Windows、RTX 4050 Laptop、URP14。短测使用 Direct3D12，随后回归、截图和离屏长测使用 Direct3D11，最后补测正常 Play Mode Direct3D12；各自记录，不混为同 API/运行方式对照。没有连接 PICO，也没有执行 APK Build。90 Hz / 11.1 ms 是目标，尚无真机达成证据。

## 数据与实现范围

SUIT-20261027 包含 7,214,556 源点、52 空间节点、141 binary PLY、5 级真实 LOD、SH3。源 JSON SHA256：`add50148df21fd497129438255c05b756fd97530b4c3bdd64c521b5ae79f250b`。五级存储点数分别为 7,214,556 / 3,607,669 / 1,805,767 / 365,389 / 86,784；不能相加作为源模型点数。

生成了全部 141 份无损 256B/点缓存和部署索引。原 PLY 与 JSON 保留。新功能扩展原有 GaussianStreamingSession，包括真正异步读取、后台解码、分阶段限流、按共享范围去重、预测走廊、优先级与过期取消、CPU/GPU 分离预算、预取限额、完整粗级覆盖与渐变细化、双眼并集裁剪、GPU 描述符展开、分层前缀扫描、排序复用和诊断日志。缓存未使用有损量化，未降低 SH 阶数。

## 已执行测试

| 测试 | 结果 | 证据 |
|---|---|---|
| EditMode 回归 | 最终 78/78、0 失败、约 16.04 s；包含 Fence 能力门、取消后立即重发、三项 Android manifest 合并/作用域测试及缺文件细化连续性 | Reports/PICO/regression-tests-78.json；较早 72/73/74/77 项报告保留 |
| PlayMode 生命周期 | 最终 2/2 通过、0 失败 | Reports/PICO/playmode-tests-final.json；较早重载后 result 为 null 的工具记录仍保留 |
| PLY / 缓存一致性 | 记录范围、SH、协方差字节一致；校验、截断、mtime 改变与回退通过 | 回归测试内 GaussianAsyncLoadingTests / GaussianRuntimeCacheTests |
| 异步、取消与公平性 | 在途预约释放、迟到代次拒绝、并发调整、可见优先与等待公平性通过 | GaussianPredictiveStreamingTests |
| 缺文件与失败不阻塞 | 实际缺失 PLY 时完整 8 区域粗级覆盖保留、健康区域继续达到 LOD0、CPU 压力下永久数据仍保留 | GaussianFailureContinuityTests，包含在最终 78 项回归；预期错误日志为受测行为 |
| 双眼、排序、GPU 索引 | 双眼单侧可见、独立顺序缓冲、65k/131k 分层前缀、共享范围偏移通过 | GaussianXRAndIndexTests |
| 桌面 SPI 实际绘制 | DX12 纹理数组两层均出现高斯、视差检查通过 | StereoInstancedShaderDrawsBothArrayLayersWithoutSkippingSplatIndices；仅桌面 GPU 测试 |
| 真实完整 LOD0 固定视图 | DX11、383,095 点 SH3、80 m、1920×1080 原版/优化版截图比较通过 | Reports/PICO/visual-fixture/comparison.json、original-lod0.png、optimized-lod0.png；不是逐像素完全一致 |
| Android 条件编译 | Unity 自带 Roslyn C#9 使用 UNITY_ANDROID 编译完整 GaussianPICO.Editor 源码成功，包含 Android 专用 Gradle 回调分支 | Reports/PICO/android-conditional-compile.txt；不是 Gradle/APK 构建或 Android Player 编译 |
| 现有移动与导航 | 鼠标恢复方向、水平移动、触控摇杆与高度限幅相关回归通过 | 同一回归报告 |
| 原 DX12 长测 | 未通过：最后进度约 180.14 s，随后 Editor 原生崩溃 | Reports/PICO/failed-dx12-unfocused-soak/；崩溃栈涉及 BufferD3D12::UpdateInternal / SetComputeBufferData |
| DX11 20 分钟回放重测 | 完成 1200.007 s、88,216 次采样、52 节点覆盖；无 GPU/IO 错误。存在一次 MCP 诊断引起的 48.4 s Editor 停顿，进程私有内存增长，不能作为无中断/无泄漏通过 | Reports/PICO/soak-cache-dx11.json、trial1 CSV/samples、soak-comparison.csv；详见下文 |
| 正常 Play Mode DX12 20 分钟补测 | 完成 1200.689 s，11,708 条约 10 Hz 日志，首次覆盖后无覆盖丢失，测量期间无 MCP 诊断或编辑器重编译；进程内存仍有增长 | Reports/PICO/runtime-soak.json、runtime-soak-analysis.json、runtime-soak.samples.csv；桌面单眼，不是 PICO 真机 |

较早的 72 项回归记录先于 DX12 离屏长测崩溃，78 项最终回归在后续全部修复后执行；单元回归不能据此宣称 DX12 长期稳定，正常 Play Mode 长测需另看独立记录。日志中保留的 `running` 是定期进度的最后写入状态，最终运行结果以汇总报告为准，不是后台测试仍然存活的证明。缺文件测试的预期 Error 作为受测行为保留在测试证据中，结束后 Console 已清理，最终 Error/Warning 均为 0。

归档日志记录 D3D12 upload scratch 分配失败（8007000e），non-local graphics memory 约 7675 MB，超过约 7292 MB 预算；该进程当时执行失焦的 Editor 离屏回放。插件约 519 MiB 缓冲估算未包含这些驱动分配。开发中已给测试入口增加 DX12 失焦时明确终止的保护，避免无提示继续；它不是 Player 渲染性能修复，也不能据此断言所有 DX12 Player 均会触发。后续 DX11 重测须单独标明图形 API，不能覆盖此失败记录或混成同 API 对照。

Unity 官方将类似的失焦 DX12 显存不释放现象列为 UUM-86354；与本次日志一致是目前的排查推断，尚未通过引擎源码或独立最小复现证明同一根因。[官方 Issue Tracker](https://issuetracker.unity3d.com/issues/allocated-graphics-memory-does-not-get-released-when-the-editor-is-out-of-focus-while-using-d3d12-graphics-api)、[Unity 2022.3.58f1 已知问题](https://unity.com/releases/editor/whats-new/2022.3.58f1)。

DX11 重启还暴露 Unity 2022.3 的 Fence 能力差异：即使 supportsGraphicsFence 为真，在不支持 Async Compute 的设备上读取 passed 仍可能抛出 NotSupportedException。能力门改为同时要求两项支持，其余路径使用 Unity 正常命令串行保证，不从后台释放 GPU。对应新测试已包含在最终 78 项回归内；Android 仍需验证。

最后修复的文件获取竞态为：唯一消费者取消后，下载任务在下一次主线程 Tick 之前尚未变为 cancelled，新请求曾复用这个已注定取消的共享任务。现在检查共享 CTS 的取消状态并重新排队，旧请求完成后清理 linked CTS；文件长度统计也在可观察的验证任务中完成，避免主线程统计异常导致等待者一直不结束。取消后立即重发测试已通过。随后 DX11 长测已完成，结论限定如下。

Android 打包支持仅对启用 DemoPICO 的 Android 构建合并 application 层 `pvr.app.type=vr` 并确保 Minimum API 24，不引入 PICO SDK。三项 XML/作用域测试验证权限、activity、自定义 metadata 保留、重复项修正、幂等与非目标场景不处理；它们不等于已执行 Gradle/APK 构建。一次较早的共同精度测试将近景保护策略误当成远景前沿失败，已明确设置 nearQualityDistance=0 测试远景共同前沿，原断言保留；该次失败记录在 rejected-test-near-frontier-policy.json，不作为运行时崩溃或最终通过结果。

为检查当前 Editor 平台不会编译到的 Android 分支，另用 Unity 随附 Roslyn 编译器、安装的 UnityEditor.Android.Extensions.dll 和 UNITY_ANDROID 条件符号编译完整 GaussianPICO.Editor 源码，输出成功并记录 SHA256。此检查覆盖 C# API/条件语法，不生成 Gradle 工程、Android IL2CPP Player 或 APK，也不能代替设备验证。

## 同路线短测

每组 3×30 s，独立 Preview Scene，1280×720、100 万可见点、GPU 640 MiB / CPU 384 MiB、每帧最多 16,384 点上传。路线按时间包含静止、前进、转头、建筑切换、后退、瞬移和返回。没有清除 OS 页缓存。首屏指所有 52 节点的完整粗级覆盖，不是全部 LOD0 就绪。

| 运行 | 完整粗级覆盖三轮（秒） | 始终覆盖节点 | 请求次数三轮 | 插件 CPU 峰值 | GPU 分配 |
|---|---|---:|---|---:|---:|
| 修改前实际基线 PLY | 0.795 / 0.903 / 0.824 | 52 | 44 / 44 / 44 | 383.7 MiB | 527.6 MiB |
| 冻结原算法隔离对照 PLY | 0.331 / 0.264 / 0.800 | 52 | 43 / 39 / 44 | 383.7 MiB | 527.6 MiB |
| 优化后 PLY | 0.856 / 0.778 / 0.800 | 52 | 56 / 55 / 56 | 383.8 MiB | 519.3 MiB |
| 优化后无损缓存 | 0.768 / 0.802 / 0.804 | 52 | 107 / 82 / 87 | 383.9 MiB | 519.3 MiB |

冻结对照复制修改前的 I/O、调度、缓存、GPU 与 Shader 算法到 Editor 专用程序集；使用普通 Editor facade 手动驱动生命周期，避免 Editor-only MonoBehaviour 无法挂载。它不是修改前 Player 二进制。首次基线保留了修改前实际运行证据。

冻结对照前两轮 Editor 更新节奏从约 13 ms 变到约 100 ms；当前优化后三轮约 100 ms。首屏受更新节奏、上传提交机会和 OS 缓存影响，不能从表中推导首屏提速比或 FPS 提升。新调度产生了不同细化集合与更多预取/取消请求，不能将较低渲染提交耗时直接解释为相同画质下的 GPU 提速。

当前优化后 PLY / 缓存的主线程 streaming tick P95 分别约 1.293 / 1.374 ms；实际上传提交 P95 约 0.843 / 0.854 ms。时间预算 0.75 ms 在每次提交后检查，单次驱动调用不可抢占，超出并不表示字节硬上限无效。这些都是 CPU Stopwatch 时间，不是 GPU 排序或绘制时间。

优化后本轮 GPU frame time 均不可用（-1）；冻结基线局部获得的 FrameTiming 数据属于 Editor 应用帧，不能与缺失数据构造 GPU 对比。完整分布、每帧点数与 CPU 提交峰值保留在 JSON/CSV 中，不能只选 P95 忽略峰值。

## SUIT 范围读取微基准

完成三类真实范围各三轮、三条读取路径，共 27 次计时读取。原算法取冻结修改前的同步 PLY 解码，优化 PLY 使用异步文件读取和后台解码，缓存使用无损预解码数据。每次计时后对每条 256B Gaussian 逐字节检查，包含完整 SH3 和协方差，27 次全部一致。

| 范围 / 点数 | 原同步 PLY 中位数 | 优化异步 PLY 中位数 | 异步 .gsbin 中位数 |
|---|---:|---:|---:|
| chunk_0，offset 26,834，3,830 点 | 7.828 ms | 8.115 ms | 0.570 ms |
| chunk_21，offset 0，148,241 点 | 295.089 ms | 297.000 ms | 12.739 ms |
| chunk_42，offset 0，383,095 点（最大区间） | 763.104 ms | 758.485 ms | 31.025 ms |

证据：Reports/PICO/io-microbenchmark.json / .csv。使用暖 OS 页缓存、复用 header/元数据；每条路径有一次不计时预热，首次缓存 SHA 校验包含在预热中。算法顺序逐轮轮换，显式 GC 在计时外。IO/Decode 阶段计时不包含所有排队、设置和验证开销，不能简单相加替代总耗时。

这证明本机暖文件缓存条件下，跳过 PLY 数学解码显著缩短该阶段；异步 PLY 本身主要将工作移出主线程，未显示数学解码吞吐显著提高。它不包含渲染、GPU 上传、Android 文件安装或首次 SHA 校验，不能把约 31 ms 最大区间读取直接当作一帧主线程开销，也不能将此倍率扩展为首屏或设备 FPS 倍率。

## 固定近景画质比较

真实 SUIT `chunk_42.ply` 的完整 383,095 点 LOD0、SH3，在 DX11 / URP14 下以 1920×1080、距离目标约 80 m 渲染。冻结原算法与当前渲染器使用相同数据、相机、模型旋转 `(180,0,0)`、缩放 `(1,1,-1)`、Render Scale 和 opacity。关闭动态 LOD、预测和性能自适应；没有抽点、降低 SH 或削减范围。

两图有 2,048 / 2,073,600 像素不同（约 0.099%）；以 0–255 通道值计，平均绝对差 0.0003367、最大差 5、RMS 0.02254。图片并非逐像素完全一致；该视图支持当前改动未通过明显模糊化取得 CPU 提交收益，但不等于整校园、所有角度、LOD 渐变或双眼画质全部验证。

原图、优化图、相机/变换参数与两图 SHA256 都保存在 `Reports/PICO/visual-fixture/`，可独立复查。该结果没有包含 FPS 或设备性能断言。

## DX11 长测结果及限制

相同缓存、路线与硬预算完成 1200.007 秒、88,216 次 Editor 采样。首次完整粗级覆盖 0.2704 秒，覆盖准备后最少 52/52 节点；最后可见 866,433 点，2,259 次启动读取、2,232 次完成缓存读取、0 次 PLY 解码。没有记录到 GPU/IO 错误。插件 decoded/reserved 峰值 402,638,592 B（383.99 MiB），GPU 缓冲为 544,504,288 B（519.28 MiB）。Unity graphics-driver 计数峰值 1,813,991,312 B，本轮大致保持 1.813 GB。

本轮有两项不能省略的限制：

- 时间戳 404.217 秒处有一次 48,428.512 ms Editor iteration，来自测试中的 MCP 诊断命令暂停；该次 streaming tick 为 0.118 ms、render-submit 为 0.777 ms。它不属于加载工作耗时，但按墙钟驱动的路线跨过了这段时间，所以该记录不能称为 20 分钟完全无中断的连续漫游。原始样本保留，不通过删掉峰值取得“流畅”结论。
- Windows 进程私有内存首分钟中位数 5,492,703,232 B，末分钟中位数 6,192,697,344 B，增长 699,994,112 B（约 667.57 MiB），峰值 6,281,940,992 B。插件缓冲和 graphics-driver 计数受控，仍不能据此宣称整个进程内存平稳或没有泄漏。需要分离 Editor、测试采样数组、Managed/Native 分配及 Player 数据结构，再用真机 PSS 长测定位。

该轮主线程 streaming tick P95 约 0.588 ms、实际上传提交 P95 约 0.505 ms。P95 无法解释上述 48.4 秒停顿，也不能取代峰值或内存趋势；图形 API 与短测不同，且 Editor 焦点/更新节奏不同，不据此宣布 FPS 提升或 PICO 达到 90 Hz。总结是“Windows DX11 20 分钟墙钟回放完成，覆盖和读取检查通过；无中断漫游与进程内存稳定性尚未验收”。

## 正常 Play Mode DX12 补测

为排除离屏 Preview Scene 的执行差异，使用 DemoPICO 的正常应用帧循环补测 Direct3D12。XR 初始化关闭，桌面单眼；相同 SUIT、无损缓存、100 万可见点、640/384 MiB GPU/CPU 预算和时间戳路线，测量期间不执行 MCP 诊断、修改代码或重编译。完成 1200.689 秒，控制器结束报告无错误、最少 52/52 节点、最终 848,949 可见点、2,155 次读取启动、2,142 次缓存完成。

它使用正常 Game View 相机，退出后读取分辨率为 1470×728，未将渲染尺寸固定到 Preview Scene 的 1280×720，也未逐帧记录分辨率；该事后读数不能保证整段始终相同，因此不与离屏报告直接比较 GPU 吞吐。测试入口结束后恢复先前 Enter Play Mode Options 和 Application.runInBackground 设置。

后台 CSV 记录 11,708 条约 10 Hz 快照；最后时间戳 1199.915 秒，首个记录完整覆盖时间为 0.2776 秒，之后 0 条覆盖丢失。控制器每秒检查首次覆盖，故汇总 firstCoverageSeconds=1.0003 秒是较粗的检查时刻，不能与 10 Hz 首条覆盖样本混淆。记录间隔中位数 0.1018 秒、P95 0.1063 秒、最大 0.2250 秒；本次没有出现此前 48.4 秒的离屏诊断停顿，但快照间隔不能证明每个应用帧都没有卡顿。

从第 30 秒后的快照读取 FrameTimingManager：CPU frame 中位数/P95/最大为 2.842 / 4.136 / 74.399 ms，GPU frame 为 2.855 / 3.825 / 12.591 ms。这些是桌面应用帧的抽样总时间，不是 Gaussian 排序/绘制逐 pass GPU 计时，更不是 PICO FPS。上传提交快照最大 3.409 ms；P95 为 0 是因为绝大多数快照未命中上传帧，不能据此解释为上传没有成本。未清除 OS 页缓存，没有原版相同运行方式的长测对照，因此不推导性能提升倍率。

10 Hz 日志中插件 CPU decoded/reserved 最大 402,185,216 B（约 383.55 MiB）、GPU 缓冲 544,504,288 B（519.28 MiB）、Unity allocated 最大 265,425,315 B。每 10 秒外部进程采样得到 private bytes 峰值 5,394,616,320 B：首分钟中位数 4,533,960,704 B，5–6 分钟为 5,197,674,496 B，末分钟 5,393,928,192 B。首末增长约 820.13 MiB，5–6 分钟至末分钟仍增长约 187.16 MiB；最后约四分钟在 5.39 GB 左右，但整个过程不能称为内存水平稳定或无泄漏。后续应将 Editor/运行时托管堆扩容、池、缓存及原生分配分别采样，并在 Android Player 使用 PSS 验证。

最后 CSV 的近期 256 次加载统计均值 57.32 ms、P95 222.32 ms，缓存命中率约 80.86%、预取命中率 100%。这是当前计数口径下的近期窗口及请求指标，不等于所有磁盘读取或所有路线的命中结论。结束汇总与最后 10 Hz 快照之间仍有少量应用帧，因此读取次数和可见点数略有不同，原始记录都保留。

`runtime-soak-analysis.json`、原 CSV 副本、设置副本及 `runtime-process-memory.jsonl` 均在 Reports/PICO；汇总脚本为 `Tools/GaussianSplatLOD/summarize_runtime_soak.py`。原设置在元数据尚未就绪时写入 `sourcePoints=0`，不修改该原始记录；源 JSON 和结束报告已确认为 7,214,556。该次补测完成正常应用帧长路线的覆盖/错误检查，但不能抵消此前离屏 DX12 原生崩溃，也不替代 PICO 双眼、控制器、90 Hz、APK 安装及内存稳定性验收。

## 剩余瓶颈和未验证项

- 原离屏 DX12 长测崩溃；DX11 离屏重测有外部诊断停顿。正常 Play Mode DX12 补测完成 20 分钟且无覆盖丢失，但首末分钟进程私有内存仍增长约 820 MiB。独立 Player、进程内存稳定性和设备连续漫游仍需进一步验收。
- 缓存在此短测没有证明首屏显著更快；预测预取增加读取请求，CPU 384 MiB 下仍可能产生来回读取。需在真实设备 IO 延迟、路线与内存压力下调整预测距离、20% 预取限额、保留时间和并发。
- GPU 透明填充、SH3 椭圆渲染与排序的设备耗时尚无证据。CPU render-submit 不能用于区分设备 GPU 排序与 overdraw；使用 PICO Unity Profiler / Frame Timing 和设备 GPU 工具采样。
- CPU 指标只统计解码数组和预约，不含整个 Unity/Android 进程；GPU arena 为估算分配，驱动隐藏分配不在内。长期稳定性需同时检查 Android PSS、Graphics、Native 与 Managed。
- 最终诊断中 CPU frame / main / render 时间来自 FrameTimingManager，缺失时为 N/A；应用帧间隔另行记录，FPS 也按应用帧统计。早期 Editor 报告保留其原始字段口径，不把时间间隔当作 CPU 或 GPU 工作耗时。
- Android jar / UnityWebRequest 路径由 Editor file:// 集成测试验证共享获取、取消和暖缓存；实际 APK、磁盘不足、Vulkan Multiview、PICO runtime、刷新率、真实双眼图像与控制器绑定未验证。
- KHR Simple Controller Profile 不提供标准摇杆轴定义；需根据设备支持的官方 OpenXR interaction profile 绑定验证。不引入 PICO SDK。
- Demo / DemoIOS 的控制脚本、导航和模型变换保持原有逻辑；项目安装 Input System 后启用 Both。发布前仍需操作者验证两个原场景。

真机部署、Profiler、20 分钟回放和日志导出操作见 [PICO.md](PICO.md)。源数据不自动移动或删除；当前同时保留 PLY+缓存的 StreamingAssets 共约 6.42 GB，发布前应使用缓存专用导出或 persistent 外部部署。
