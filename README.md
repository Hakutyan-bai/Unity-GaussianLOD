# Unity Gaussian Splat LOD v0.2

Unity **2022.3 LTS / URP 14 / Windows x64** 的 Aholo Chunk LOD 转换与原生 Gaussian 流式渲染插件。也提供 Built-in Render Pipeline 路径。运行时为 C#、Compute Shader、HLSL，无 Node、浏览器或 Web Viewer 依赖。

当前版本为 **0.2.0（Git 标签 v0.2）**。本版加入异步读取、预测与优先级调度、取消与缓存保护、GPU 可见索引展开及无损部署缓存的扩展。下文公布 SUIT 校园数据的实际对照测试；**未取得 PICO 真机帧率、Android 冷启动或真机内存结果**。

## 快速使用已有模型

### 安装方式（选择一种）

- **UPM Git**：Package Manager → Add package from git URL，输入 `https://github.com/Hakutyan-bai/Unity-GaussianLOD.git#v0.2`。依赖由 UPM 解析。
- **UPM ZIP / 本地包**：解压 Release 中的 `Unity-GaussianLOD-UPM-v0.2.zip`，在 Add package from disk 选择 `package.json`；安装后显示版本 `0.2.0`。
- **Unity 资源包**：先安装 URP 14、Newtonsoft JSON 和 Unity UI（UGUI），再通过 `Assets > Import Package > Custom Package` 导入 `Unity-GaussianLOD-v0.2.unitypackage`。导入位置为 `Assets/UnityGaussianSplatLOD`，包含合成 Demo 和小型 SH3 测试数据。 在 Package Manager 的 Built-in 列表中保留 XR、VR 和 UnityWebRequest 模块；无需为桌面加载安装 OpenXR。v0.1 标签及其旧附件仍对应历史版本。

同一工程只安装一份；不要同时安装 Assets 资源包和 UPM 包，避免重复程序集。Release 不包含校园模型、独立相机导航 UI 或其他第三方渲染插件。

1. 将整个 `com.yourcompany.gaussian-splat-lod` 目录放入工程 `Packages/`；或 Package Manager → Add package from disk，选择 `package.json`。依赖 Newtonsoft JSON 和 URP 14，由 UPM 解析。
2. URP：在当前 Universal Renderer Data 添加 **GaussianLODRenderFeature**，或执行 `Tools > Gaussian Splat LOD > Install URP Render Feature`。Built-in 不需要这一步。支持 Forward Renderer；XR 采用独立 DemoPICO 测试资源，真机状态见下文。2D Renderer 和 Camera Stacking 未验证。
3. `Tools > Gaussian Splat LOD > Create Renderer`；也可给 Empty GameObject 添加 `GaussianLODRenderer`。
4. Inspector 的 **Browse Local JSON** 选择 Aholo `lod-meta.json`。JSON 中的文件必须是普通 binary 3DGS PLY。也可将项目内 JSON 拖入 **JSON Configuration**。
5. 点击 Load；先准备完整粗级模型，整体出现后逐级细化，Editor 中可用 Scene View 预览，Play Mode 自动加载。移动相机会触发 LOD 调整。已有模型无需重新切块。

## 从原始 PLY 转换

打开 `Tools > Gaussian Splat LOD > Model Converter`，选择外部 Source PLY 和新的或空的输出目录。默认 5 级、400000 点、PLY。点击 Convert。

插件直接启动随包提供的 **Aholo native 2.1.0 Windows x64**，通过真实的 `Read → AutoChunkLod → Write` Pipeline JSON 工作。默认目标比例是 100%、50%、25%、5%、1%；原生算法采用空间分块、邻域查找、高斯协方差融合、尺度补偿和 opacity 剔除，最终数量允许偏离比例。小块可能复用相同记录区间。关闭 Generate LOD 会使用单级 AutoChunkLod，仍生成索引 JSON。

转换使用专属 `.partial-<id>` 目录。完成后校验所有 PLY header、SH 阶数和记录区间，再提交数据。**非空输出目录一律拒绝，不自动清空。** Cancel 终止原生进程，原始输入不修改，未完成的临时目录及 Pipeline 保留供诊断。进度显示阶段估算和原生日志，不冒充精确百分比。窗口关闭或重编译会取消转换。

转换完成可 Open Output Folder、Import Into Project 或 Create Renderer In Scene。原生转换是内存中的离线算法，可能消耗数 GB 内存；不是 out-of-core 转换器。Unity 不读取原始模型副本；Pipeline release 在原生侧及时释放源资源。C++ 工具构建独立于 Unity 正常构建，详见 [NativeConverter.md](Documentation~/NativeConverter.md)。

## Windows 发布

加载优化：Inspector 的 **Prepare Fast Loading Cache…** 可生成无损预解码缓存，运行时跳过 PLY 数学转换；支持只导出 JSON + 缓存的部署集。已为当前 JN_LOD1 校园数据生成缓存。移动端预算、PersistentDataDirectory 模式、实测读取结果及 PICO/iPhone 尚未验证的部分，见 [MobileLoading.md](Documentation~/MobileLoading.md)。

PICO 本轮统一使用 SUIT-20261027（721 万源点、52 分区、5 级真实 LOD、SH3），已生成 141 份无损缓存及部署索引。`Tools > Gaussian Splat LOD > PICO Deployment` 提供准备、缓存专用导出与取消。独立 DemoPICO、预测/优先级调度、异步 IO、双眼路径、Inspector 中文说明和真机测试步骤见 [PICO.md](Documentation~/PICO.md)；实际通过项、短测数据、早期 DX12 离屏失败与后续普通 Play Mode 回放见 [PICO-Validation.md](Documentation~/PICO-Validation.md)。未执行 APK Build，也未宣称真机 90 Hz。

发行资源中的 Demo 是可移植的合成模型场景。本文提到的 SUIT 校园数据与宿主工程独立 DemoPICO 不随发行包提供；核心双眼渲染代码已包含，XR Origin / OpenXR 项目配置按 [PICO.md](Documentation~/PICO.md) 设置。此前的 v0.1 发布资源保持原样。

将数据原样放在 `Assets/StreamingAssets/GaussianSplatLOD/<名称>/`。Inspector 提供 **Copy Dataset Into StreamingAssets**，不会覆盖已有目录。选择该目录中的 JSON 后自动保存 StreamingAssets 相对路径。Windows Player 使用 `Application.streamingAssetsPath`，通过 FileStream 读取原始 PLY，不把大文件作为 TextAsset。允许中文与空格路径。

ExternalDirectory 模式保留磁盘绝对路径，适合开发或固定安装路径；便携发布应使用 StreamingAssets。Inspector 的 TextAsset 只用于选择 JSON，运行时以序列化路径为数据地址，因此可直接访问旁边的文件。

## v0.2 实测结果

测试日期为 **2026-10-31**，环境为 **Windows / NVIDIA GeForce RTX 4050 Laptop / Unity 2022.3.62f3c1 / URP14**。以下是桌面 Editor 测试，不是 PICO、iOS 或独立 Player 的性能承诺。GB 按十进制计算，MiB/GiB 分别为 1024²/1024³ 字节。

**实测最明确的收益是预解码缓存缩短单块读取与解码时间。整模型首屏基本没变，CPU 缓存基本不变，插件 GPU 缓冲分配减少约 8.37 MiB。没有同口径证据证明整体 FPS 提升。**

### 实际模型规模

| 项目 | 实测数据 |
| --- | ---: |
| 数据集 | SUIT-20261027 |
| 原始高斯点数 | **7,214,556** |
| 空间分区 / binary PLY 文件 | **52 / 141** |
| 真实 LOD / SH 阶数 | **5 级 / SH3** |
| 最大高精度记录区间 | **383,095 点** |
| 分块 PLY 总大小 | **3.081 GB**（3,080,718,255 字节） |
| 无损 `.gsbin` 缓存总大小 | **3.342 GB**（3,341,573,232 字节） |

源点数没有因本轮优化减少，也没有降低 SH 阶数。缓存保存 256 字节/点的预解码 GPU 属性，**不是磁盘压缩格式**；同时保留整套 PLY 和缓存会占用约 6.42 GB。不同 LOD 的存储点数不能相加当作原始模型点数。

### 三轮短测：优化前后对照

三条路径各运行 **3×30 秒**，使用相同 SUIT 数据、时间戳路线与硬预算：**DX12 / 1280×720 / 100 万可见点 / CPU 384 MiB / GPU 640 MiB / 每帧最多上传 16,384 点**。操作系统文件页缓存未清除。

| 指标 | 修改前原系统 PLY | 优化后 PLY | 优化后无损缓存 |
| --- | ---: | ---: | ---: |
| 完整粗级覆盖时间，中位数 | **0.824 s** | **0.800 s** | **0.802 s** |
| CPU 解码缓存及在途预约峰值 | 383.69 MiB | 383.77 MiB | 383.92 MiB |
| 插件 GPU 缓冲分配估算 | 527.65 MiB | 519.28 MiB | 519.28 MiB |
| 流式更新 CPU 耗时 P95 | 1.247 ms | 1.293 ms | 1.374 ms |
| 实际绘制高斯数，中位数 | 652,693 | 525,139 | 494,234 |
| 发起区间读取请求次数，三轮 | 44 / 44 / 44 | 56 / 55 / 56 | 107 / 82 / 87 |

“完整粗级覆盖”表示 52 个空间节点都有可用低精度数据，**不是全部 721 万点 LOD0 已加载**。三轮覆盖时间分别为：原系统 0.795 / 0.903 / 0.824 秒；优化后 PLY 0.856 / 0.778 / 0.800 秒；缓存 0.768 / 0.802 / 0.804 秒。

CPU 数据仅包含插件解码数组和在途预约，GPU 数据是插件缓冲分配估算，均不代表整个 Unity 进程或全部显存。预测预取增加了请求，部分请求会取消；不能把表格解释为磁盘读取次数减少。实际 LOD 选择集合和 Editor 更新节奏不同，也不能用这些数据推导相同画质的 GPU 或 FPS 提升。原始基线和冻结原算法隔离对照均保留在工作区报告中。

### 单块读取与解码

选取三个真实记录区间，每种读取路径各重复三次，共 **27 次计时读取**。下表为中位数：

| 高斯点数量 | 原版同步 PLY | 优化后异步 PLY | 无损缓存 | 缓存相对原版 |
| --- | ---: | ---: | ---: | ---: |
| 3,830 | 7.828 ms | 8.115 ms | **0.570 ms** | 13.7 倍 |
| 148,241 | 295.089 ms | 297.000 ms | **12.739 ms** | 23.2 倍 |
| 383,095 | 763.104 ms | 758.485 ms | **31.025 ms** | 24.6 倍 |

每条路径有一次不计时预热，包含首次缓存 SHA 校验；复用 header/元数据，使用暖 OS 文件缓存，算法顺序逐轮轮换，显式 GC 位于计时之外。该测试不含 GPU 上传、渲染或 Android 解包。27 次读取的全部 256B Gaussian 记录逐字节一致，包含 SH3 和协方差。

**异步 PLY 本身没有明显提高数学解码吞吐，主要收益来自缓存跳过重复解码。24.6 倍只对应这个区间的暖缓存读取与解码，不能扩展为首屏或 FPS 的提速倍率。**

### 普通 Play Mode 连续 20 分钟

优化后在 **DX12、普通 DemoPICO Play Mode、桌面单眼**下运行 1200.69 秒，XR 初始化关闭，测量期间未通过 MCP 查询 Unity。日志以 10 Hz 采样，共 11,708 条；外部进程内存每 10 秒采样。Game View 在退出后读取为 1470×728，未锁定为短测的 1280×720，不能混为同条件前后对照。

| 指标 | 实测结果 |
| --- | ---: |
| 首屏后的区域覆盖丢失 | **0 次，保持 52/52** |
| 实际绘制高斯峰值 | **999,868 点** |
| 插件 CPU 解码缓存及预约峰值 | **383.55 MiB** |
| 插件 GPU 缓冲分配估算 | **519.28 MiB** |
| 整个 Editor 进程工作集峰值 | **3.347 GiB** |
| 整个 Editor 进程私有提交内存峰值 | **5.024 GiB** |
| 首分钟 / 末分钟私有提交内存中位数 | **4.223 / 5.023 GiB** |
| 第 5～6 分钟至末分钟私有提交内存增长 | **187.16 MiB** |

私有提交内存从首分钟到末分钟增长约 **820.13 MiB**。高斯缓存预算受控，但**整个进程没有证明内存恒定或无泄漏**，也没有优化前同口径的整体进程内存对照。工作集、私有提交内存、Unity 分配和插件缓存是不同指标，不能互相替代。

预热后 10 Hz 快照的 CPU / GPU 应用帧时间 P95 分别为 **4.136 / 3.825 ms**，采样最大值为 **74.399 / 12.591 ms**。这些是桌面 FrameTimingManager 数据，可能遗漏帧间峰值，不是高斯排序或绘制单 Pass 的 GPU 时间，更不是 PICO 帧率。早期失焦 DX12 离屏回放发生过原生崩溃；另一次 DX11 离屏回放包含 48.4 秒 MCP 诊断暂停，均保留为独立记录，没有被这次普通 Play Mode 结果覆盖。

### 正确性验证和证据

- **宿主开发工程 78/78 EditMode、2/2 PlayMode 测试通过**，包括主包与宿主工程的移动导航、PICO 配置测试。包含共享区间、异步取消、取消后立即重发、缺失高精度文件保留粗级、健康区域继续细化、常驻缓存保护、排序和桌面双眼输出层测试；发行包独立导入测试数量单独记录。
- 相同 383,095 点 LOD0、SH3、80 m 距离、1920×1080 固定视图对照：2,048 / 2,073,600 像素不同，平均绝对通道差 **0.0003367/255**、最大差 **5/255**。不是逐像素完全一致，也不是整校园或 PICO 双眼画质验收。
- 本工作区原始证据为 `Reports/PICO/` 下的 `comparison.json`、`io-microbenchmark.json`、`runtime-soak-analysis.json`、对应 CSV、`regression-tests-78.json`、`playmode-tests-final.json` 和 `visual-fixture/`。发布包本身不包含用户校园数据或截图。

完整测量条件、失败记录及限制见 [PICO-Validation.md](Documentation~/PICO-Validation.md)，公开数值证据见 [v0.2 benchmark evidence](Documentation~/Benchmarks/v0.2/README.md)。**未执行 APK Build；PICO 双眼画面、90 Hz、Android 冷启动、手柄摇杆绑定及真机内存稳定性仍待验证，不报告这些项目的提升数据。**

## 参数

| 参数 | 行为 |
| --- | --- |
| Enable Rendering / Dynamic LOD / Frustum Culling | 立即生效；关闭动态 LOD 时按点预算尝试 L0 |
| Coherent LOD | 默认开启，先提升可见区域的共同精度，再分配局部细节；同时限制加载中的细化前沿，减少不同分区之间的清晰/模糊拼块感 |
| Max Visible Splats | 每个模型的绘制点数上限；不足以容纳完整粗级时明确报错，避免隐藏整块区域 |
| Render Scale | Gaussian 协方差半径倍率，非屏幕分辨率倍率 |
| Opacity Multiplier | opacity 倍率 |
| Coordinate Scale / Position / Rotation Offset | 作用于整个模型及协方差，Transform 还支持非均匀和负缩放 |
| Max GPU Memory | GPU arena + visible + radix 缓冲的 MiB 预算；改变时重建 GPU arena，无需重新读 JSON |
| Max CPU Cache | 解码数组、在途输出和约 1 MiB 读取缓冲预约；不代表整个 CLR/Unity/驱动或进程内存 |
| Preload Distance | 世界 AABB 的预加载基础距离，与移动预测走廊共同决定预取；不绘制视锥外数据 |
| Load Chunks Per Frame / Max Concurrent Loads | 每次流式 tick 新建请求上限和在途请求上限；IO / Decode 另有限流 |
| Upload Splats Per Frame | 每应用帧增量 GPU 上传点数硬上限，同时受字节、提交次数和软时间预算限制 |
| LOD Hysteresis | 目标稳定时间，单位秒；默认 0.25 s，调度间隔默认 50 ms |
| LOD Transition Seconds | 默认 0.4 秒新旧级别光学厚度渐变；旧数据在过渡结束前保留，不会先卸载再拼块 |

修改显示和变换参数无需重新导入。CPU/GPU 预算应能容纳 permanentFiles 引用区间和完整粗级底图，预算过小时明确报错，常驻数据不参与淘汰。GPU arena 可同时保留旧、新 LOD；新数据完整上传后才渐变切换。始终保留整个模型的粗级覆盖，转向或读取失败时可即时回退。连续常驻文件区间合并读取，优先准备全局粗级，不与高精度加载争抢首屏资源。

可见点预算预留约 25% 供渐变使用；根据 GPU arena 和常驻数据还会降低实际细节点预算。Inspector 显示有效上限。切换按完整分区提交，不按缓冲区顺序截断模型。若一次快速转向使绘制集合超预算，该帧绘制完整粗级集合，避免部分片区消失。

总 GPU 预算与**单个 Buffer 上限不同**：所有分配使用 64 位字节计算，Gaussian arena 限于 `min(SystemInfo.maxGraphicsBufferSize, int.MaxValue)` 内的完整记录数。即使设 GPU 4096 MiB / 可见 500 万点，也不会创建超过设备单缓冲上限的 Gaussian Buffer。总驻留和排序缓冲可能超过 2 GiB，但各个缓冲分别受限。需要超过单 arena 容量时当前版本降低有效细节预算；尚未实现多 arena 扩容。

## 数据和实现

- 严格识别 `magicCode=2500660 / type=lod-splat / version=1.0` 及官方字段。`counts` 是源 L0 数，不汇总所有 LOD。`tree` 是分区列表。
- `file` 是 files 下标；`offset/count` 是高斯记录区间。每文件 header 只解析一次；共享或重叠的区间合并缓存，相邻独立区间保持可选择读取。绘制去除重叠记录。
- binary little/big endian PLY，任意标量属性顺序，float/double/整数宽度；读取 log scale、WXYZ quaternion、opacity logit、SH0–3，并将 SH channel-major 转为 coefficient-major RGB。
- 异步后台磁盘区间解码，主线程有界上传；每高斯无 GameObject。
- GPU 全局稳定 radix-16 深度排序（每模型、每相机），3D 协方差变换、Jacobian 投影、屏幕椭圆、SH 视角颜色、指数 Gaussian 核、预乘 alpha 混合、opaque depth test。
- URP ScriptableRendererFeature 使用 2022.3 API。Resources 保留 Shader / ComputeShader，Player 不依赖 Shader.Find。

## 演示、测试和限制

`Tools > Gaussian Splat LOD > Create Demo` 生成 SH3 合成 PLY，调用同一个原生转换器并建立 `Assets/GaussianSplatLOD/Demo.unity`。进入 Play 自动锁定鼠标，直接移动鼠标转向，WASD/QE 移动，Shift 加速；ESC 释放并显示鼠标，左键点击 Game View 恢复漫游。失去窗口焦点或组件禁用时释放鼠标。已有相机可执行 `Tools > Gaussian Splat LOD > Enable FPS Roaming` 添加控制器。Test Runner 可运行包的 EditorTests；外部工程需在 manifest 的 testables 加入包名。

FPS 控制器保持世界 Y 轴朝上、Roll=0，默认 X 旋转 **10°～60°**，世界高度 Y 默认 **60～100**。Inspector 的 Min/Max Rotation X 与 Min/Max Height 可调整。旧的倒置 120°～170° 范围自动迁移到直立 10°～60°；重新锁定时根据相机 forward 重建朝向，避免等价 Euler 表示造成翻转。鼠标使用正常 FPS 方向。Gaussian Shader 直接使用 Unity 当前相机的实际投影矩阵，与天空盒及普通物体共享 render-target 翻转约定。

真实测试结果、设备、图像和已知限制见 [Validation.md](Documentation~/Validation.md)。不承诺固定 FPS。

当前不支持 ESZ/SPZ/SOG、ASCII/压缩 PLY、WebGL、HDRP、光照/阴影写入、与普通透明物体的逐像素联合排序。Android 的 APK 数据按需落盘、persistent 随机读取及 OpenXR 双眼路径已实现，PICO 真机尚未验证；iOS 也未实机构建验证。文件就绪层支持 UnityWebRequest URL 获取，不包含账号认证或网络 CDN 发布管理。Built-in 旧式 double-wide Single Pass 不作为支持路径。多个模型各自排序；重叠模型间不是统一的全局高斯排序。每模型当前共享一套流式驻留状态，多相机可分别剔除和排序，但细节选择以 targetCamera（否则最近调度相机）为准。GPU cache 为连续区间分配，极端碎片化可能暂缓新上传。原生生成的包围盒排除离群点，边界的极端高斯可能被较早剔除。

## 许可

本插件 MIT。Aholo / EGS 的原始 MIT 声明和原生依赖许可证随 `ThirdPartyNotices/` 保留。参考仓库及原生二进制来源记录见 [Research.md](Documentation~/Research.md)。
