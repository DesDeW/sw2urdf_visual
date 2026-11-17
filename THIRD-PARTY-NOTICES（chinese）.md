# 第三方组件声明

本安装包内含以下第三方库。各库的许可证名称如下，完整协议文本随各自原包分发。

| 库 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| MathNet.Numerics | 4.7.0 | MIT | 矩阵与坐标变换运算 |
| CsvHelper | 7.1.1 | MS-PL OR Apache-2.0 | 配置文件的 CSV 导入导出 |
| log4net | 2.x | Apache-2.0 | 日志（`export.log`） |
| Castle.Core | 4.3.1 | Apache-2.0 | log4net 的依赖 |
| solidworkstools | 1.x | MIT | SolidWorks 辅助工具（Stephen Brawner） |

> `SolidWorks.Interop.*.dll` **不在本包内**。它们属于达索的再分发组件，安装脚本会从本机
> SolidWorks 安装目录（`api\redist`）复制，因此不随本仓库或安装包分发。

## 上游项目

本加载项是 `solidworks_urdf_exporter`（sw2urdf）1.6.1 的 fork，
Copyright (c) 2020 Stephen Brawner，以 MIT 许可证发布（见 LICENSE）。

## 商标

“SolidWorks”与“SOLIDWORKS”是 Dassault Systemes SolidWorks Corporation 的商标。
“ROS”是 Open Source Robotics Foundation 的商标。
本加载项是独立作品，与上述两方均无隶属关系，也未获得其认可。

## 合规要点

- **Apache-2.0** 组件再分发时需保留其 NOTICE 内容。
- **MS-PL** 组件以二进制形式分发时需附带许可证文本。
- **MIT** 要求在所有副本或重要部分中保留版权声明与许可声明。
- 达索的互操作程序集不属于本项目，任何情况下都不应提交到公开仓库。
