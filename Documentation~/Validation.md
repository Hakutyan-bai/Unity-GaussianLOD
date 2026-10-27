# 验证与支持范围

## 已验证环境

- Unity 2022.3.62f3c1，Windows x64，NVIDIA GeForce RTX 4050 Laptop GPU。
- Built-in 与 URP 14 桌面渲染路径；Direct3D 12 已执行真实 GPU 输出验证。
- 原生转换器：Aholo Windows x64 native 2.1.0，真实 Pipeline 调用。

## 数据与实测

真实校园已有分块：7,261,819 源高斯、126 PLY、59 空间分区、5 LOD、SH3。完成近景、远景、变换场景的 GPU 预览；发布完整粗级后保留 59/59 分区覆盖，发生了 LOD 渐变。校园模型及其截图不随公开仓库或发行包发布。

另一个 7,214,556 点原始 SH3 PLY，经真实原生转换生成 141 PLY、52 分区，离线转换耗时约 59.25 秒。LOD 实际数量由原生融合算法决定，并非数组截取。

快速加载对比：Windows Editor 预热后的一轮顺序测试，完整粗级首屏 PLY 0.258 秒、预解码缓存 0.092 秒。两条路径均覆盖 59/59 分区。单独的后台读入/解码抽测缓存约快 24～26 倍；这不是整体启动、全部高精度加载或真机 FPS 的加速倍数。细节和参数见 [MobileLoading.md](MobileLoading.md)。

此前 Windows Player 1280×720、约 25 秒可见渲染采样（预热 5 秒），测得约 17.4 FPS，实际绘制点数随视角在约 8.6 万～69.1 万变化。它使用当时的桌面版本与参数，不代表最新源码、移动设备或性能承诺。隐藏窗口导致未绘制的采样不计为有效渲染成绩。

## 测试覆盖

Editor 测试覆盖 Aholo JSON、字段/索引/记录区间验证、混合类型及 big-endian PLY、SH/协方差、共享缓存和分配器、GPU 容量限制、GPU 深度排序、光学厚度渐变、Unity 投影对齐、转换器成功/取消/输出保护、无损缓存一致性与回退、缓存专用部署集导出。PlayMode 测试覆盖组件加载/卸载与 GPU 资源生命周期。

发行构建的汇总及资源包导入检查写入 `Validation/release-validation.json`（仓库）或发行说明；未执行的检查不标记为通过。

## 尚未验证或支持

- PICO 4 Ultra 的 Android/Vulkan、XR 逐眼投影、single-pass stereo 尚未完成适配验证。
- iPhone 14 的 Metal、IL2CPP、iOS Player 与真机内存/持续帧率尚未验证。
- 不支持 HDRP、WebGL、ASCII/压缩 PLY、ESZ/SPZ/SOG、URL 下载和 Android APK 数据自动安装。
- 多模型分别排序；不同模型之间及与普通透明物体之间没有逐像素联合排序。
- 固定 256 字节 GPU 高斯记录，缓存为有界连续区间分配；极端碎片化可能暂缓细化。

发行版是 v0.1 桌面预览版。移动端配置按钮仅提供调优起点，不构成移动平台兼容性声明。
