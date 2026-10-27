# Unity Gaussian Splat LOD

Unity **2022.3 LTS / URP 14 / Windows x64** 的 Aholo Chunk LOD 转换与原生 Gaussian 流式渲染插件。也提供 Built-in Render Pipeline 路径。运行时为 C#、Compute Shader、HLSL，无 Node、浏览器或 Web Viewer 依赖。

## 快速使用已有模型

### 安装方式（选择一种）

- **Unity 资源包**：先在 Unity 2022.3 的 Package Manager 安装 URP 14 和 Newtonsoft JSON（`com.unity.nuget.newtonsoft-json`），再通过 `Assets > Import Package > Custom Package` 导入 Release 中的 `Unity-GaussianLOD-v0.1.unitypackage`。导入位置为 `Assets/UnityGaussianSplatLOD`。若导入 Tests，请保留 Unity Test Framework。
- **UPM Git**：Package Manager → Add package from git URL，输入 `https://github.com/Hakutyan-bai/Unity-GaussianLOD.git#v0.1`，依赖会自动解析。
- **UPM 本地包**：解压 `Unity-GaussianLOD-UPM-v0.1.zip`，Add package from disk 选择其中的 `package.json`。

同一工程只安装一份；不要同时安装 Assets 资源包和 UPM 包，避免重复程序集。Release 不包含校园模型、独立相机导航 UI 或其他第三方渲染插件。

1. 将整个 `com.yourcompany.gaussian-splat-lod` 目录放入工程 `Packages/`；或 Package Manager → Add package from disk，选择 `package.json`。依赖 Newtonsoft JSON 和 URP 14，由 UPM 解析。
2. URP：在当前 Universal Renderer Data 添加 **GaussianLODRenderFeature**，或执行 `Tools > Gaussian Splat LOD > Install URP Render Feature`。Built-in 不需要这一步。支持 Forward Renderer，暂未验证 2D Renderer、XR 和 Camera Stacking。
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

将数据原样放在 `Assets/StreamingAssets/GaussianSplatLOD/<名称>/`。Inspector 提供 **Copy Dataset Into StreamingAssets**，不会覆盖已有目录。选择该目录中的 JSON 后自动保存 StreamingAssets 相对路径。Windows Player 使用 `Application.streamingAssetsPath`，通过 FileStream 读取原始 PLY，不把大文件作为 TextAsset。允许中文与空格路径。

ExternalDirectory 模式保留磁盘绝对路径，适合开发或固定安装路径；便携发布应使用 StreamingAssets。Inspector 的 TextAsset 只用于选择 JSON，运行时以序列化路径为数据地址，因此可直接访问旁边的文件。

## 参数

| 参数 | 行为 |
| --- | --- |
| Enable Rendering / Dynamic LOD / Frustum Culling | 立即生效；关闭动态 LOD 时按点预算尝试 L0 |
| Coherent LOD | 默认开启，先提升可见区域的共同精度，再分配局部细节；同时限制加载中的细化前沿，减少不同分区之间的清晰/模糊拼块感 |
| Max Visible Splats | 每个模型的绘制点数上限；预算不足以覆盖粗级时按投影优先保留区域 |
| Render Scale | Gaussian 协方差半径倍率，非屏幕分辨率倍率 |
| Opacity Multiplier | opacity 倍率 |
| Coordinate Scale / Position / Rotation Offset | 作用于整个模型及协方差，Transform 还支持非均匀和负缩放 |
| Max GPU Memory | GPU arena + visible + radix 缓冲的 MiB 预算；改变时重建 GPU arena，无需重新读 JSON |
| Max CPU Cache | 解码数组及在途数组预留 MiB；不含 CLR/Unity/驱动开销及每请求约 64 KiB 读取缓冲 |
| Preload Distance | 视锥外到世界 AABB 的预加载距离，不绘制视锥外数据 |
| Load Chunks Per Frame / Max Concurrent Loads | 每次流式 tick 新建请求上限及后台 IO 并发 |
| Upload Splats Per Frame | 每次 tick 的增量 GPU 上传上限 |
| LOD Hysteresis | 目标稳定后才切换；实际时间与约 100 ms 调度间隔量化有关 |
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

当前不支持 ESZ/SPZ/SOG、ASCII/压缩 PLY、Android/iOS/WebGL、HDRP、XR、URL 下载、光照/阴影写入、与普通透明物体的逐像素联合排序。多个模型各自排序；重叠模型间不是统一的全局高斯排序。每模型当前共享一套流式驻留状态，多相机可分别剔除和排序，但细节选择以 targetCamera（否则最近调度相机）为准。GPU cache 为连续区间分配，极端碎片化可能暂缓新上传。原生生成的包围盒排除离群点，边界的极端高斯可能被较早剔除。

## 许可

本插件 MIT。Aholo / EGS 的原始 MIT 声明和原生依赖许可证随 `ThirdPartyNotices/` 保留。参考仓库及原生二进制来源记录见 [Research.md](Documentation~/Research.md)。
