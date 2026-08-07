# MonsterDeleter(召唤怪兽摧毁文件)

> 🎉 本项目借鉴(重写)自开源项目 [MonsterDeleter](https://github.com/531149627/MonsterDeleter)(PyQt6 版)。
> 衷心感谢原作者 [531149627](https://github.com/531149627) 的开源分享——本项目的玩法创意、
> 演出流程设计、素材组织方式均源自该项目。原项目用 Python + PyQt6 实现,本仓库用
> Electron + JS 完整重写,目标是绕开 Python/PyQt6/PyInstaller 在部分机器上的环境问题,
> 并顺带解决原版的一些体验痛点(见下文对比)。

右键文件/文件夹 → 召唤怪兽 → 怪兽走进来确认 → 一脚踢飞 → 目标进回收站。一款充满恶趣味的"文件删除助手"。

## 🔗 项目链接

- **GitHub**:https://github.com/Alan2Z/monster_deleter_electron(推荐,发布产物在此下载)
- **Gitee**:https://gitee.com/Alan-Zou/monster_deleter_electron(⚠️ 安装包产物过大,无法上传至 Gitee,请移步至 GitHub 下载)

## ✨ 功能

- **右键召唤**:在任意文件或文件夹上右键 → "召唤××怪兽摧毁" → 怪兽演出开始
- **摧毁文件夹**:文件夹同样支持右键召唤摧毁(原版只支持文件,这是本项目的增强)
- **自动定位**:自动锁定文件图标在屏幕上的位置,怪兽直奔真正的文件(不需要手动瞄准点击)
- **完整演出流程**:走路入场 → 指点确认 → 对话气泡 → 踢踹 → 爆炸 → 回收站删除(文件/文件夹均可)→ 雷欧登场 → 飞离退场
- **多角色系统**:一个文件夹 = 一个角色,素材自动扫描、配置深合并、缺素材自动回退
- **染色变体**:不换图,靠染色做出"新怪兽"
- **内置配置编辑器**:右键角色卡片 → 高亮编辑 config.jsonc(语法校验、保存即生效)
- **动画参数可调**:入场时长、爆炸触发帧、飞离时长等全部参数化
- **角色分发**:角色文件夹自包含,压缩成 zip 即可分发给其他用户
- **NSIS 安装版**:桌面快捷方式、卸载自动清理注册表、用户素材不随卸载丢失

## 🛠 技术栈

| 技术 | 用途 |
|---|---|
| [Electron](https://www.electronjs.org/) 43 | 应用框架(主进程 + 渲染进程) |
| 原生 HTML/CSS/JS | 界面与演出(无前端框架,canvas 逐帧动画) |
| [CodeMirror 5](https://codemirror.net/) | 配置编辑器(JSON 高亮 + Monokai 主题) |
| C#(.NET Framework 4.x) | 预编译的"文件图标定位"工具(枚举资源管理器列表视图,~10KB) |
| Win32 API + UI Automation | 跨进程读取资源管理器/桌面的文件列表,锁定文件图标坐标 |
| electron-builder + NSIS | 打包安装版(向导安装、桌面快捷方式、卸载清理) |
| JSONC 配置 | 自研注释剥离器,让配置文件可以带中文注释 |
| 镜像源 | 全程支持 npmmirror 国产镜像(见 `.npmrc`) |

## ⚖️ 与原项目对比

### 优点(本项目的改进)

| 维度 | 原项目(PyQt6) | 本项目(Electron) |
|---|---|---|
| 环境依赖 | Python + PyQt6 + PyInstaller,部分机器有 DLL/UCRT 环境问题 | 安装版开箱即用,无运行时依赖 |
| 目标定位 | 手动瞄准点击(点不准就炸错地方) | **自动锁定文件图标位置**(光标已偏移的痛点被解决) |
| 配置文件 | config.json(无注释,字段全靠猜) | config.jsonc(逐字段中文注释) + 内置高亮编辑器 + 语法校验 |
| 动画参数 | 写死在代码里 | 全部参数化(时长/爆炸帧/间距等,config 可调) |
| 素材位置 | exe 同目录(升级覆盖即丢) | `%APPDATA%` 用户数据目录(升级/卸载不丢) |
| 右键菜单 | 只注册不清理 | 卸载时自动清理注册表 |
| 自定义角色 | 手动改 JSON | 按钮直达素材目录 + 手册(txt/md 双格式) |

### 不足(Electron 的固有代价)

| 维度 | 原项目 | 本项目 |
|---|---|---|
| 安装包体积 | ~80MB | ~122MB |
| 内存占用 | Qt 较省 | Electron 常驻 ~150MB |
| 冷启动 | ~0.5s | ~1s |
| 技术形态 | 单文件便携 | 需安装(有卸载器) |

### 保留一致的

- 演出节奏与动画状态机(走路 → 指点 → 对话 → 踢踹 → 爆炸 → 退场)
- 5×3 spritesheet 切帧规则、染色变体、素材回退链
- 回收站删除(原 send2trash → Electron `shell.trashItem`)
- HKCU 右键菜单注册(免管理员)

## 🚀 快速开始

```bash
npm install          # 安装依赖(已配置 npmmirror 镜像)
npm start            # 开发模式运行
npm run dist         # 打包 NSIS 安装版 → dist/MonsterDeleter-Setup.exe
```

分发物只有一个文件:`dist/MonsterDeleter-Setup.exe`。

## 📁 目录结构

```
src/                 # 源码(主进程/渲染进程/角色系统/定位工具)
assets/              # 内置素材模板(首启复制到用户目录)
build/               # 打包构件(NSIS 脚本 + 定位工具 exe)
public/logo.png      # 应用图标
assets/自定义角色操作手册.txt / .md   # 用户手册(双格式)
```

用户自定义角色的素材目录:安装后 `%APPDATA%\monster-deleter\assets`(点软件内"📖 自定义角色"直达)。

## 📄 许可证

[MIT](LICENSE)

> ⚠️ 本项目仅用于娱乐和学习使用,禁止用于违法用途。
