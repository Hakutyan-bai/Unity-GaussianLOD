Unity Gaussian Splat LOD v0.2，UPM 包版本 `0.2.0`。本版扩展流式调度、异步读取、GPU 索引与排序、Android 文件获取和 XR 渲染路径，并公开完整的实测条件与限制。

### 下载与安装

- **Unity-GaussianLOD-v0.2.unitypackage**：Unity 2022.3 先安装 URP 14、Newtonsoft JSON 和 UGUI；通过 Assets → Import Package → Custom Package 导入。包含可移植的合成 SH3 Demo，不包含校园模型。保留 Built-in 的 XR、VR、UnityWebRequest 模块。
- **Unity-GaussianLOD-UPM-v0.2.zip**：解压后在 Package Manager 选择 Add package from disk，打开 `package.json`，依赖由 UPM 解析。
- **Git UPM**：`https://github.com/Hakutyan-bai/Unity-GaussianLOD.git#v0.2`。
- **release-validation.json / SHA256SUMS.txt**：独立导入验证结果及发行附件校验值。

Assets 和 UPM 安装方式二选一，避免重复程序集。URP 工程需为 Universal Renderer 添加 GaussianLODRenderFeature，可通过 Tools → Gaussian Splat LOD → Install URP Render Feature 操作。升级前先移除旧版的同一套插件代码，保留模型数据目录。

### 相对 v0.1 的变化

- 文件 IO 使用 `FileStream.ReadAsync`，IO、解码独立限流，GPU 上传同时受点数、字节数、提交次数和软时间预算限制。
- 加入运动预测走廊、动态优先级、转头重排、独立请求取消、瞬移代次失效和迟到结果丢弃。
- 加入在途内存预约、缓存保留与回收水位、预取占用限制、独立最大驻留点数、GPU 资源退休保护；保留完整粗级覆盖、近景保护及渐变细化。
- 可见区间描述符在 GPU 展开，排序前缀扫描改为分层扫描；点集与观察轴满足条件时复用排序。
- 新增部署索引和内容校验、Android APK 文件按需落盘、共享获取与取消；保留旧 `.gsbin` 和 PLY 回退。
- 新增双眼裁剪并集、逐眼矩阵/SH 方向/投影、立体输出层及必要时逐眼排序；增加分阶段诊断、P95、世界空间面板和回放日志。
- 修复示例相机在导航后重新锁定鼠标时视角跳动。

`.gsbin` 无损缓存、真实多级 LOD、SH3、高斯协方差渲染、完整粗级覆盖、渐变、共享缓存和单 Buffer 大小保护在 v0.1 中已经存在，不能算作本版首次新增。Aholo `lod-meta.json` 与 256 字节 GPU 记录格式保持兼容。

### 实测数据

测量日期 2026-10-31，环境为 Windows Editor / Unity 2022.3.62f3c1 / RTX 4050 Laptop / URP14。数据集 SUIT-20261027：**7,214,556 源高斯、52 分区、141 PLY、5 级真实 LOD、SH3**。PLY 总大小 3.081 GB，无损缓存 3.342 GB，未减点或降低 SH。

| 指标 | 原系统 PLY | 优化后 PLY | 优化后无损缓存 |
| --- | ---: | ---: | ---: |
| 三轮完整粗级覆盖中位数 | 0.824 s | 0.800 s | 0.802 s |
| CPU 解码缓存及预约峰值 | 383.69 MiB | 383.77 MiB | 383.92 MiB |
| 插件 GPU 缓冲分配估算 | 527.65 MiB | 519.28 MiB | 519.28 MiB |

短测为 DX12、1280×720、每条路径 3×30 秒、100 万可见预算、CPU/GPU 384/640 MiB。LOD 选择集合与 Editor 更新节奏有差异，OS 页缓存未清除。首屏指全部 52 个分区的完整粗级可用，不是全部 LOD0 加载完成；没有证据证明整体首屏或 FPS 显著提高。

最大 383,095 点区间，三次暖文件缓存读取/解码中位数为：**原版同步 PLY 763.104 ms、优化后异步 PLY 758.485 ms、无损缓存 31.025 ms**。缓存相对 PLY 约 24.6 倍，仅限该阶段；`.gsbin` 在 v0.1 已存在，**不能将此倍率当作 v0.1 缓存对 v0.2 缓存、整模型首屏或 FPS 的提升**。27 次读取的全部 256B 高斯记录逐字节一致，包含 SH3 与协方差。

普通 DX12 Play Mode 连续运行 **1200.69 秒**，首屏后保持 **52/52 覆盖、0 次丢失**；插件 CPU 峰值 383.55 MiB、GPU 缓冲估算 519.28 MiB。整个 Editor 进程私有提交内存峰值 5.024 GiB，首末分钟中位数增长约 **820.13 MiB**；从第 5～6 分钟到末分钟仍增长 **187.16 MiB**，不能宣称整体内存恒定或无泄漏。普通回放使用桌面单眼、10 Hz 日志采样，不代表设备 FPS 或所有帧的峰值。

开发工程已通过 **78/78 EditMode、2/2 PlayMode** 测试，包含宿主导航及 PICO 配置测试。本次 v0.2 另在 Windows DX11 的独立工程验证：**UPM 与资源包重新导入各通过 70/70 EditMode、2/2 PlayMode**；合成 Demo 实际出图并完成卸载。详见附件 `release-validation.json`。独立批处理曾在导入完成后的原生退出阶段卡住，终止该验证进程后重开工程完成测试；未将该退出过程标为成功。相同完整 383,095 点 LOD0 固定视图对照没有明显画质回退，图像并非逐像素一致。数值证据保存在仓库 `Validation/v0.2/`，不公开用户校园模型或截图。

### 验证边界与已知问题

- **没有执行 APK Build，没有 PICO/iPhone 真机结果，也没有已验证的 90 Hz、Android 冷启动、双眼设备画面或手柄摇杆绑定结论。** 当前 KHR Simple Controller Profile 不提供标准摇杆轴，需要设备侧补验绑定。
- 独立 DemoPICO 是开发宿主工程的 SUIT 测试场景，**不包含在通用 UPM/资源包内**。发行包提供合成 Demo；核心 XR 路径并不等于设备适配验收。
- 早期失焦 DX12 Editor 离屏回放曾因原生上传暂存分配耗尽而崩溃；测试入口增加失焦保护。后续普通 Play Mode 连续回放完成，不能据此抹去早期失败或宣称所有 DX12/Android 场景稳定。
- 另一次 DX11 离屏长测包含约 48.4 秒 MCP 诊断暂停，作为独立、有条件的记录保留，不冒充无中断漫游。
- CPU/GPU 缓存预算不是整个进程 RAM/显存上限。透明填充、排序、托管大数组/进程内存增长仍需在设备上分别定位。
- 不支持 WebGL、HDRP、ESZ/SPZ/SOG 或压缩 PLY；多个模型分别排序。Aholo 原生转换器仅用于 Windows Editor 离线处理。

运行时无浏览器、Web Viewer、Node 或 PICO SDK 依赖。Aholo/EGS MIT 及原生第三方许可证随包保留。完整操作与限制见 README、Documentation~/PICO.md 和 Documentation~/PICO-Validation.md。
