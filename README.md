# Marmoset Toolbag 5 简体中文汉化

Marmoset Toolbag 5 的简体中文界面补丁。汉化覆盖菜单、面板标题、按钮、下拉选项、工具提示与说明文字。

- 适用版本：**5.0.3.2（build 5032）**
- 界面字体使用软件自带的 `notosans_chinese.slug`，无需安装额外中文字体
- 支持安装、卸载，卸载后可完整还原英文

## 下载

到 [Releases](../../releases) 页面下载最新版本：

| 文件 | 说明 |
| --- | --- |
| `Toolbag5-Chinese-Patch-v1.0.0.exe` | 标准安装包，自动检测安装目录，支持静默安装 |
| `Toolbag5-Chinese-Patch-v1.0.0-Portable.zip` | 免安装绿色版，解压后运行 `安装.bat` |
| `README-zh.txt` | 使用说明 |

> 发布资产使用 ASCII 文件名（GitHub 会清空中文文件名），下载后可按需自行改名。

## 使用

### 安装包版

双击安装包，按提示安装。静默安装：

```
Toolbag5-Chinese-Patch-v1.0.0.exe /S
Toolbag5-Chinese-Patch-v1.0.0.exe /uninstall /S
```

安装日志：`C:\ProgramData\ToolbagZh\setup.log`

### 绿色版

解压后双击 `安装.bat`。绿色版不含游戏本体，只带补丁数据（约 110 KB），安装时现场写入。
其余脚本：`卸载.bat`、`切换到中文.bat`、`切换到英文.bat`、`查看状态.bat`。

绿色版不写注册表，卸载只需还原备份，整个文件夹可以拷到别的机器使用。

## 实现原理

汉化采用**等长字节替换**：只改可执行文件里界面文字的字节内容，不改变文件大小，也不修改
PE 头与节表，因此不会破坏程序结构。

难点在于部分中文译文比原文长（例如 `Scene` → `场景`）。程序按**编译期写死的字节长度**
读取这些字符串，只改字符串本身会被截断成半个汉字（场景树曾显示成「场」加方框）。补丁因此
会同时改写调用点的长度常量：

- `tools/lenfix.py` 建立 LEA 索引，定位每个字符串的引用点
- `tools/verify_lenfix.py` 用 capstone 反向反汇编，确认长度确实作为参数传入
- `tools/gen_lenfix.py` 把每个变长的字符串分类为 `widen`（可改写长度常量）、
  `pinned`（长度无法确认，禁止变长）或 `free`（附近没有长度常量，可自由变长）
- `tools/audit_len.py` 反向审计：扫描所有长度常量，确认没有遗漏的调用点

安全模型是 `REGION + SHAPE` 双闸：只改写 UI 字符串区域，且拒绝标识符形状的文本
（snake_case、camelCase 接缝、路径分隔符、`::`、分号列表等），避免误改内部键。

## 仓库结构

```
installer/      C# 安装器源码（Setup.cs、图标、manifest）
green/          绿色版脚本（batch + PowerShell）
tools/          构建与校验脚本（Python）
work/           翻译源码（*.zh.json）与长度策略（lenfix.json）
dist/           编译后的补丁数据（patch.json）与译文（english.txt）
backup/         原版 english.txt 基线，用于重建译文
```

`dist/patch.json` 是编译产物，但它只包含补丁数据（偏移、原字节、新字节、长度常量），
不包含 Marmoset 的任何二进制文件。发布工作流直接用它打包，因此 CI 不需要游戏本体。

`work/` 下的 `*.zh.json` 是译文源码：`batch_*.zh.json` 对应 `english.txt`，
`exe_*`/`t2_*`/`t3_*`/`t4_*.zh.json` 对应可执行文件里的界面文字。
`patch.json` 里的中文即由这些源码编译而来。

## 构建

### 发布构建（无需游戏本体）

```
python tools/build_release.py
```

产出 `dist/Toolbag5_汉化补丁.exe` 与 `release/` 目录（安装包、绿色版 zip、使用说明）。
需要 .NET Framework 4.8 参考程序集与 Roslyn `csc`（Visual Studio Build Tools 自带）。
脚本会先用 `vswhere` 自动定位编译器，找不到时也可用环境变量 `CSC_PATH` 指定。

构建前可先自检补丁数据：

```
python tools/check_data.py
```

### 只重建 english.txt

修改 `work/batch_*.zh.json` 后：

```
python tools/build_text.py
```

该脚本以 `backup/english.txt.orig` 为基线，逐行替换译文，保持行数、键名与顺序不变。

## 说明

本项目只分发界面翻译与打补丁工具，不包含 Marmoset Toolbag 的任何程序文件。
请先自行安装正版软件，再使用本补丁。
