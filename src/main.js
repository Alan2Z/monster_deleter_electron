// 大将怪兽摧毁 - 主进程
// 职责:生命周期 / 右键菜单注册 / 启动模式判断 / 窗口创建 / IPC / 回收站删除
const { app, BrowserWindow, ipcMain, shell, screen, Menu, dialog } = require('electron');
const { execFile } = require('child_process');
const path = require('path');
const fs = require('fs');
const characters = require('./characters');

// 必须在 ready 之前设置
app.commandLine.appendSwitch('autoplay-policy', 'no-user-gesture-required');  // 防音频被拦
app.setAppUserModelId('com.example.monsterdeleter');                          // 任务栏图标

// 去掉窗口自带的 File/Edit/View/Window 默认菜单栏
Menu.setApplicationMenu(null);

const MENU_KEY = 'HKCU\\Software\\Classes\\*\\shell\\SummonMonster';

// ---------- 右键菜单注册(只在打包后执行) ----------
// 结构要和原版一致:
//   HKCU\Software\Classes\*\shell\SummonMonster          默认值=菜单显示名,Icon=图标
//   HKCU\Software\Classes\*\shell\SummonMonster\command  默认值="{exe}" "%1"
// portable 目标下 process.execPath 指向临时解压目录,必须用 PORTABLE_EXECUTABLE_FILE
// 先删后写:清掉旧版本(含 Python 版)留下的孤儿子键,保证每次都是干净注册
// 菜单显示名跟随当前默认角色(用户换角色后同步更新)
function contextMenuLabel() {
  const lastId = characters.loadLastCharacter();
  if (lastId) {
    const cur = characters.scanCharacters(assetsDir()).find((c) => c.id === lastId);
    if (cur) return `召唤${cur.name}摧毁`;
  }
  return '召唤大将怪兽摧毁';
}
function registerContextMenu() {
  const exe = process.env.PORTABLE_EXECUTABLE_FILE || process.execPath;
  const cmd = `"${exe}" "%1"`;
  const reg = (args) => execFile('reg', ['add', ...args], () => {});
  execFile('reg', ['delete', MENU_KEY, '/f'], () => {
    reg([MENU_KEY, '/ve', '/d', contextMenuLabel(), '/f']);
    reg([MENU_KEY, '/v', 'Icon', '/d', `"${exe}",0`, '/f']);
    reg([`${MENU_KEY}\\command`, '/ve', '/d', cmd, '/f']);
  });
}

// ---------- 启动模式 ----------
// 从 argv 里找被召唤的文件:必须是存在的【文件】(排除 exe 自身、目录、命令行开关)
const SELF = [process.execPath, process.env.PORTABLE_EXECUTABLE_FILE].filter(Boolean);
function findTargetFile(argv) {
  return argv.find((a) => {
    if (!a || a.startsWith('-') || SELF.includes(a)) return false;
    try { return fs.statSync(a).isFile(); } catch { return false; }
  });
}

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', (e, argv) => {
    const file = findTargetFile(argv);
    if (!file) return;
    if (app.isReady()) openShowWindow(file);
    else app.whenReady().then(() => openShowWindow(file));
  });
  app.whenReady().then(() => {
    ensureUserAssets();                           // 首启把内置素材复制到用户目录
    if (app.isPackaged) registerContextMenu();  // dev 模式注册没意义
    const file = findTargetFile(process.argv);
    if (file) openShowWindow(file);   // 右键召唤:带文件参数
    else openCharacterWindow();       // 双击 exe:角色选择窗口
  });
}

app.on('window-all-closed', () => {
  // 换角模式:角色窗口关了但演出窗口还活着,不能退
  if (!showWin || showWin.isDestroyed()) app.quit();
});

// ---------- 角色选择窗口 ----------
function openCharacterWindow(swapMode = false) {
  const win = new BrowserWindow({
    width: 1020, height: 660,
    minWidth: 920, minHeight: 560,
    title: '召唤怪兽摧毁文件',
    alwaysOnTop: swapMode,            // 演出中换角要置顶
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });
  win.loadFile(path.join(__dirname, 'renderer', 'character.html'), { query: { swap: swapMode ? '1' : '0' } });
}

// ---------- 演出窗口(透明全屏,唯一一个) ----------
let showWin = null;

// 精确锁定文件图标在屏幕上的位置:枚举资源管理器/桌面的列表视图按文件名匹配。
// 右键菜单点击时光标已偏移到菜单项上,不能用光标当目标。
// 预编译 C# 工具(find-file-rect.exe,~10ms 启动),比 PowerShell 冷启动快 ~1s
function findFilePosition(file) {
  return new Promise((resolve) => {
    const tool = app.isPackaged
      ? path.join(process.resourcesPath, 'app.asar.unpacked', 'build', 'find-file-rect.exe')
      : path.join(app.getAppPath(), 'build', 'find-file-rect.exe');
    execFile(tool, [file], { timeout: 8000 }, (err, stdout) => {
      if (err) { resolve(null); return; }
      const m = stdout.trim().match(/^(-?\d+)\s+(-?\d+)$/);
      resolve(m ? { x: parseInt(m[1], 10), y: parseInt(m[2], 10) } : null);   // 物理像素
    });
  });
}

function openShowWindow(targetFile) {
  if (showWin && !showWin.isDestroyed()) showWin.close();   // 重复召唤时换新
  // 演出窗口开到光标所在的那块屏(右键菜单就在那)
  const cp = screen.getCursorScreenPoint();
  const display = screen.getDisplayNearestPoint(cp);
  const { bounds } = display;
  let pendingPos = null;
  showWin = new BrowserWindow({
    x: bounds.x, y: bounds.y,
    width: bounds.width, height: bounds.height,
    frame: false, transparent: true, alwaysOnTop: true,
    skipTaskbar: true, hasShadow: false, resizable: false,
    backgroundColor: '#00000000',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });
  showWin.setAlwaysOnTop(true, 'screen-saver');   // 压过全屏应用
  showWin.loadFile(path.join(__dirname, 'renderer', 'show.html'));
  showWin.webContents.once('did-finish-load', () => {
    // 定位若已完成则直接带上,否则渲染端先显示"定位中"
    showWin.webContents.send('init-show', { targetFile, targetPos: pendingPos });
  });
  // 与窗口加载并行定位文件图标;失败退回光标位置(总比没有好)
  findFilePosition(targetFile).then((pos) => {
    if (!showWin || showWin.isDestroyed()) return;
    const scale = display.scaleFactor;
    const px = pos ? pos.x / scale : cp.x;   // 物理 → DIP
    const py = pos ? pos.y / scale : cp.y;
    const tp = { x: px - bounds.x, y: py - bounds.y };   // 窗口内坐标
    if (showWin.webContents.isLoading()) pendingPos = tp;      // 还没加载完,交给 init-show
    else showWin.webContents.send('auto-target', tp);
  });
}

// ---------- 素材路径 ----------
// 打包后用户素材在 %APPDATA%\monster-deleter\assets(用户数据目录):
// 1) asar 归档清单是打包快照,后加文件不可见(asar 补丁的 readdir 只返回清单条目)
// 2) app.asar.unpacked\assets 是程序文件,覆盖安装会被整体替换,用户素材必丢
// 所以首启时把内置素材复制到用户数据目录,之后扫描/打开都在那里,安装/卸载都不动它。
function assetsDir() {
  return app.isPackaged
    ? path.join(app.getPath('userData'), 'assets')
    : path.join(app.getAppPath(), 'assets');
}
// 首次启动:把内置素材复制到用户数据目录(已存在则跳过,不覆盖用户改动)
function ensureUserAssets() {
  if (!app.isPackaged) return;
  const dst = assetsDir();
  if (fs.existsSync(dst)) return;
  const src = path.join(process.resourcesPath, 'app.asar.unpacked', 'assets');
  if (!fs.existsSync(src)) return;
  fs.mkdirSync(path.dirname(dst), { recursive: true });
  fs.cpSync(src, dst, { recursive: true });
}

// ---------- IPC ----------
// scanCharacters 的结果含函数(spritePath 闭包),结构化克隆会失败,
// 所以在这里展平成纯数据:paths.sprites / paths.audio 里是已解析的绝对路径
const SPRITE_KEYS = ['walk', 'point', 'kick', 'explosion', 'leo', 'fly'];
const AUDIO_KEYS = ['bgm', 'voice', 'explosion'];

ipcMain.handle('scan-characters', () => {
  return characters.scanCharacters(assetsDir()).map((c) => ({
    id: c.id, name: c.name, description: c.description,
    folder: c.folder,
    sprites: c.sprites, texts: c.texts, animation: c.animation, tint: c.tint,
    paths: {
      sprites: Object.fromEntries(SPRITE_KEYS.map((k) => [k, c.spritePath(k)])),
      audio: Object.fromEntries(AUDIO_KEYS.map((k) => [k, c.audioPath(k)])),
    },
  }));
});
ipcMain.handle('get-last-character', () => characters.loadLastCharacter());
ipcMain.handle('save-last-character', (e, id) => characters.saveLastCharacter(id));
// 换角色后同步右键菜单显示名(只在打包版生效,dev 不注册菜单)
ipcMain.handle('update-context-menu-name', (e, name) => {
  if (!app.isPackaged) return null;
  execFile('reg', ['add', MENU_KEY, '/ve', '/d', `召唤${name}摧毁`, '/f'], () => {});
  return `召唤${name}摧毁`;
});
ipcMain.handle('trash-file', (e, file) => {   // 回收站(文件已不存在则跳过,同原版 exists 检查)
  if (!file || !fs.existsSync(file)) return { ok: false, reason: 'not-found' };
  return shell.trashItem(file)
    .then(() => ({ ok: true }))
    .catch((err) => ({ ok: false, reason: err.message }));
});

// 打开 assets 目录给用户自定义角色(与扫描目录同一位置,用户数据目录,安装/卸载不动)
ipcMain.handle('open-assets-dir', () => shell.openPath(assetsDir()));   // 返回空字符串 = 成功

// 读取角色 config 文件内容(右键卡片编辑用;没有 config 时给默认模板)
ipcMain.handle('read-character-config', (e, folder) => {
  for (const n of ['config.jsonc', 'config.json']) {
    const p = path.join(folder, n);
    if (fs.existsSync(p)) {
      return { ok: true, file: n, content: fs.readFileSync(p, 'utf-8') };
    }
  }
  // 模板 = 内置 dajiang 的完整注释模板
  const template = path.join(assetsDir(), 'dajiang_monster', 'config.jsonc');
  return {
    ok: false,
    template: fs.existsSync(template) ? fs.readFileSync(template, 'utf-8') : '{\n  // 在此填写配置\n}',
  };
});

// 写回角色 config(编辑器保存)
ipcMain.handle('write-character-config', (e, folder, content) => {
  try {
    fs.writeFileSync(path.join(folder, 'config.jsonc'), content, 'utf-8');
    return { ok: true };
  } catch (err) {
    return { ok: false, message: err.message };
  }
});

ipcMain.on('open-character-window-for-swap', () => openCharacterWindow(true));
ipcMain.on('swap-character-selected', (e, charId) => {
  if (showWin && !showWin.isDestroyed()) showWin.webContents.send('swap-done', charId);
});
ipcMain.on('show-window-close', () => app.quit());
