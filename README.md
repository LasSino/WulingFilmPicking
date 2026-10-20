# WulingFilmPicking 武陵选片局 - 照片初选 
武陵选片局是一个用于相机照片初选的应用。
相机佬在拍照时，经常会对一个场景拍很多张照片，其中大部分会由于对焦不稳、构图不对、或者只是单独没有其他张拍得好，而成为不需要的废片。这些照片没必要流转到LR等重量级的后期工作流中，武陵选片局就是这样一个轻量级的初选工具，用于直接筛选出需要过滤的照片。

## 当前进度

| 里程碑 | 状态 |
| --- | --- |
| 设计文档 (docs/design/) | ✅ 完成 |
| **M0 骨架**: Shell + 打开目录 + 扫描配对 + 浏览条 | ✅ 完成 (2026-10-18) |
| **M1 核心选片**: 对比区多选槽位(1-9) + 标记 + 会话持久化 + 提交处置(软删除/事务日志) + 完成对话框 | ✅ 完成 (2026-10-18) |
| **M2 RAW 与元数据**: EXIF 读取 + 方向归一化 + 两级图像源 + EXIF 对比模式(差异高亮) + 处理设置 | ✅ 完成 (2026-10-19) |
| M3 插件系统 (契约 + C#/Python 插件) | ⬜ 未开始 |
| M4 打磨与分发 (含多机型 RAW 样张测试) | ⬜ 未开始 |

## 核心功能

- **浏览条多选驱动对比区**: 缩略图条 Ctrl/Shift 多选 1-9 张,槽位与选中集实时同步,会话恢复后自动回填
- **两级图像源**: 浏览条 96px 快速缩略图,对比区按需解码全清晰度视图;EXIF Orientation 全八种方向归一化
- **标记 toggle**: ✅/🗑 浮动按钮(宿主 chrome 层,覆盖所有对比模式),激活态即视;浏览条卡片右上角状态徽点
- **EXIF 对比模式**: 每槽参数卡(机身/镜头/焦距/光圈/快门/ISO/曝光补偿/拍摄时间),跨槽差异橙色高亮
- **会话持久化**: 标记/槽位/模式/处置设置原子写入 `<目录>/.wuling/session.json`,重开即恢复
- **提交处置**: 三类标记各自动作(不动/移动/软删除/永久删除)可配置,RAW+JPEG 绑定,软删除入 `.wuling/trash/` 可还原,事务日志 `commit-log.jsonl`

## 开发环境

- .NET 8 SDK (LTS)
- Avalonia 12 + CommunityToolkit.Mvvm

## 构建与运行

```bash
dotnet build WulingFilmPicking.slnx     # 构建
dotnet test                             # 运行单元测试 (32 个)
dotnet run --project src/Wuling.App     # 启动应用
```

## 解决方案结构

```
src/
  Wuling.Core/      # 领域: PhotoPair/TagStateMachine + DirectoryScanner + SessionStore + CommitPlanner/Executor
  Wuling.Imaging/   # 图像: ThumbnailService(两级图像源+方向归一化) + ExifProvider(MetadataExtractor)
  Wuling.App/       # Avalonia 桌面应用 (MVVM; 浏览条/对比区/模式条/宿主 chrome)
tests/
  Wuling.Core.Tests/  # 扫描配对/会话/提交/EXIF+方向 单测 (样张缺失自动跳过)
docs/
  design/           # 设计文档 (原型/架构/功能/插件系统)
```

M0 已实现: RAW+JPEG 同名自动配对(大小写不敏感、优先较大 JPEG、忽略附属文件与 `.wuling/`)、自然排序、目录选择(Avalonia 12 StorageProvider)、横向浏览条 + 对比区骨架 + 标记命令。
M1 计划: 缩略图位图接入浏览条(ThumbnailsService 已就绪)、槽位 1-9 可调、会话持久化、提交处理管道。