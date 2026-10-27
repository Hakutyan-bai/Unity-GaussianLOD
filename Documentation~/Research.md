# Aholo 调研与格式兼容记录

调研源：<https://github.com/manycoretech/aholo-viewer>，本地只读克隆 `Tools/aholo-source`，revision `fab5d30d0dcb8f9f7f6cc80914a68aaae5a73d17`。子模块 EGS revision `78aa5d8f31fff80079e979e10b255cbbbb2415b4`。官方 native npm 版本 2.1.0；下载包 SHA512 已与 registry integrity 比较通过，来源元数据位于 `Tools/AholoNative/npm-metadata.json`。

核对内容：

1. `packages/splat-transform/README.md`：原生程序可直接读 Pipeline 文件或 stdin，无 Node 依赖。不是将 npm CLI 参数直接交给 native。
2. `packages/splat-transform-native/source/src/pipeline/tasks/auto_chunk_lod.cpp`：`type` 直接参与 `chunk_N.<type>` 命名；默认 5 级，3/4 级 merged/permanent；倒序安排 merged 布局，先前已分配区间可复用；JSON offset 是记录偏移。
3. `source/src/pipeline/lod_support.cpp`：空间块并行生成、原始输入移动、LOD 相邻级逐级生成；小块共享已有级别。
4. `source/src/splat/splat_lod.cpp`：KD 邻域、样本概率/协方差代价、加权高斯融合，协方差包含中心间二阶矩；opacity 剔除和 scaleBoost。此算法由官方 native 程序执行，无另造简化器。
5. `source/src/io/ply.cpp`：普通 binary PLY、exp(scale)、sigmoid(opacity)、WXYZ quaternion、f_dc 与 channel-major f_rest；Write 将其编码回标准 3DGS PLY。merged preserve_order 必须生效，不能在 Unity 侧重新 Morton 排列文件区间。
6. `website/src/content/manual/zh-CN/chunk-lod.md` 与 `splat-transform.md`：schema、初始块点数与 merged 文件可能超上限、默认比例、原生 API、Write 会清空已有目录。
7. `packages/renderer/src/splat-utils.ts`、`splat-loader.ts`：实际实现转发至 EGS；进一步阅读 EGS `packages/utils/splat-utils/lod/index.ts` 与 ResourceManager，核对粗级覆盖、投影面积/当前点数优先、预算、滞回及延迟替换。
8. EGS `SplattingMaterial.ts`、`SplatPrecalculateMaterial.ts`：协方差椭圆、SH、预乘 alpha、透明深度原则。Unity 重新实现 GPU 数据布局和渲染，不复制 WebGL/TypeScript 运行时代码。

官方推荐 ESZ，但 PLY bundle 路径已通过真实 native 2.1.0 转换验证。插件当前只实现该普通 PLY 路径。常见 schema magicCode 十六进制 `0x262834`，十进制 `2500660`。forwardBox 是约 80% 稠密分布框，不能假设其涵盖所有极端点。

本工程原有 `package/Runtime/GaussianSplatting.asmdef` 的 `GS_ENABLE_URP` 版本条件从空字符串改为 `17.0.0`，避免安装 URP14 时编译只支持 Unity6 的旧 URP 文件。该调整不属于新包的运行时依赖；独立安装本包不需要 Aras 包。
