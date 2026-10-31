# Changelog

## 0.2.0 / v0.2 — 2026-11-01

- 在现有流式会话中加入 FileStream 异步读取、独立 IO/解析限流、请求代次与取消、动态优先级、运动预测和瞬移失效处理。
- 扩展共享缓存、CPU/GPU 独立预算、预取占用限制、常驻数据保护、GPU 资源退休及分批上传；驻留容量与可见排序容量分离。
- 加入双眼视锥并集、GPU 可见索引展开、分层前缀扫描、排序复用及每眼排序路径。
- 增加无损部署索引、缓存专用导出、Android 按需文件落盘、诊断面板及回放日志。
- README 收录 2026-10-31 的真实 SUIT 测量：7,214,556 源点、52 分区、141 PLY、5 级真实 LOD、SH3；三轮短测、27 次读取对照及普通 Play Mode 20 分钟回放。
- 78/78 EditMode 和 2/2 PlayMode 测试通过；固定完整 LOD0 视图对照记录随验证文档说明。

实测最大 383,095 点区间的暖缓存读取与解码中位数为原版 PLY 763.104 ms / 无损缓存 31.025 ms；无损缓存在 v0.1 已存在，这不是两版缓存之间的吞吐对比。整模型完整粗级覆盖仍约 0.8 秒，CPU 缓存基本不变，GPU 缓冲分配减少约 8.37 MiB。进程私有内存仍有增长，不能宣称无泄漏。PICO/iOS 真机、APK 安装、90 Hz 与手柄绑定未完成验收。宿主工程回归与发行包独立导入验证分别记录。

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
