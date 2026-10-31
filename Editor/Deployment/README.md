# PICO 数据准备与安装层

`GaussianDeploymentBuilder.BeginPrepare(jsonPath)` 在后台生成所有无损 `.ply.gsbin` 和 `deployment-index.json`。原始 PLY、Aholo JSON 与 SH3/GPU 256B 布局保持不变。`Progress`、`Status`、`Error` 可在准备期间轮询，`Cancel()` 保留已经完成的缓存。

部署索引记录每个文件的长度、SHA256、记录数，以及节点 AABB、LOD、记录偏移和字节偏移。不要把多级 LOD 的存储点数当作源模型点数。`ExportCacheOnly` 可导出不含原始 PLY 的设备数据，目标目录必须为空，JSON 最后提交，取消时不会发布完整数据集。

运行时 `GaussianDatasetSource` 只负责文件就绪，不进行 LOD 调度。Unity 主线程调用 `Tick()` / `Dispose()`，内部通过 UnityWebRequest + DownloadHandlerFile 获取 APK 中被请求的文件，下载到 persistent 目录同文件系统的 partial 文件；完整校验后原子发布。多个节点引用同一文件只获取一次；取消一个消费者不会中断其他消费者。安装内容通过索引校验，复制导致的 PLY 时间戳改变不会误判快速缓存失效。

已安装文件使用数据版本、SHA256、长度与本地修改时间的验证记录，后续启动只重新获取小型 JSON / 索引，按需复用大文件。缺索引的旧数据仍可逐文件加载，但无法识别 JSON 不变、点数据被替换的更新，因此设备部署应使用生成的索引。APK 内文件需要先按文件落盘，不能直接对 jar 文件使用 Aholo 记录偏移。

普通本地路径继续使用现有 Loader；`file://` URL 走与 Android 相同的安装队列，便于 Editor 集成测试。没有在本模块中调用 APK Build，也没有真机带宽、90Hz 或系统长期内存保证。
