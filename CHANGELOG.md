# Changelog

## 0.1.0 / GitHub v0.1

- Unity 2022.3 / URP 14 / Windows x64 首个预览版本，另提供 Built-in 渲染路径。
- Aholo native 2.1.0 PLY 空间分块与 AutoChunkLod，保留 MIT 和依赖许可证。
- Aholo lod-splat JSON、共享记录区间、permanentFiles、binary PLY 与 SH0–3。
- 协方差 Gaussian 椭圆投影、SH 颜色、稳定 GPU radix 深度排序及预乘 Alpha。
- 异步加载、CPU/GPU 缓存预算、完整粗级覆盖、共同 LOD 前沿及光学厚度渐变。
- 单 Buffer 大小保护、Unity 投影矩阵对齐、Scene/Game View 预览。
- 无损预解码加载缓存、缓存专用部署集导出、移动端调优预算和 PersistentDataDirectory 路径。
- Editor 转换窗口、自定义 Inspector、生成式合成 Demo 和 FPS 漫游控制器。
- 支持 UPM 和 Assets .unitypackage 两种安装方式。

限制：Android/Vulkan/XR、iOS/Metal 尚未完成真机适配验证；不支持 WebGL/HDRP 或压缩 PLY/ESZ/SPZ/SOG。性能记录仅代表文档中列出的桌面测试。参见 Documentation~/MobileLoading.md 与 Validation.md。
