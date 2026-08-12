// 角色系统(port of characters.py)
// - 扫描 assets 下的角色文件夹(有走路 spritesheet 或 config.json 才算)
// - 默认配置深合并 + 素材回退链(folder → baseDir → assetsRoot)
// - settings.json:exe 同目录 → %APPDATA% 回退
// 只在主进程运行;app 引用做了容错,纯 Node 也能单测
const fs = require('fs');
const path = require('path');
const os = require('os');

let app = null;
try { app = require('electron').app; } catch { /* 纯 Node 环境下加载 */ }

const DEFAULT_CHARACTER = {
  id: 'green_monster',
  name: '大将怪兽',
  description: '绿皮大将，嚣张登场',
  sprites: {
    walk: '走路动效_spritesheet.png',
    point: '指着文件_spritesheet.png',
    kick: '踹文件动效_spritesheet.png',
    explosion: '爆炸_spritesheet.png',
    leo: '雷欧登场_spritesheet.png',
    fly: '出场飞行动效_spritesheet.png',
    point_frames: [11, 12, 13, 14],
  },
  audio: {
    bgm: 'audio/bgm(1).mp3',
    voice: 'audio/怪兽说话.mp3',
    explosion: 'audio/爆炸.MP4',
  },
  texts: {
    targeting: '请选择你要摧毁的文件',
    dialog: '喂，是这个吗？',
    choice_yes: '是的',
    choice_no: '嘤嘤嘤就是这个',
    swap: '换一只',
  },
  animation: {
    fps: 8,                    // 动画帧率
    sprite_height: 250,        // 怪兽显示高度(像素)
    walk_duration_ms: 4500,    // 走路入场时长(毫秒)
    explosion_height: 150,     // 爆炸图显示高度(像素)
    walk_y_offset: 50,         // 走路时怪兽相对目标点的垂直偏移(像素,正=偏下)
    target_gap: 30,            // 怪兽手指与目标文件的水平间距(像素)
    kick_frame: 5,             // 踢踹动画第几帧触发爆炸(0 起,默认第 6 帧)
    fly_duration_ms: 2000,     // 飞离动画时长(毫秒)
    explosion_y_offset: 40,    // 爆炸图相对目标点上移量(像素)
  },
  tint: { color: '#ffffff', strength: 0 },
  targeting: { bg_opacity: 0.35 },   // 瞄准界面背景图(targeting_bg.png)的不透明度,0~1
};
const MERGE_SECTIONS = ['sprites', 'audio', 'texts', 'animation', 'tint', 'targeting'];
const WALK_PATTERNS = ['走路动效_spritesheet', 'walk_spritesheet'];

// 支持注释的 JSON(JSONC):剥掉 // 行注释和 /* */ 块注释,字符串里的保留
function stripJsonComments(text) {
  let out = '';
  let inStr = false, esc = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (inStr) {
      out += ch;
      if (esc) esc = false;
      else if (ch === '\\') esc = true;
      else if (ch === '"') inStr = false;
      continue;
    }
    if (ch === '"') { inStr = true; out += ch; continue; }
    if (ch === '/' && text[i + 1] === '/') {          // 行注释
      while (i < text.length && text[i] !== '\n') i++;
      out += '\n';                                    // 保留换行,报错行号不乱
      continue;
    }
    if (ch === '/' && text[i + 1] === '*') {          // 块注释
      i += 2;
      while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) i++;
      i++;                                            // 跳过 */
      continue;
    }
    out += ch;
  }
  return out;
}

function deepMerge(base, override) {
  const out = { ...base };
  for (const [k, v] of Object.entries(override || {})) {
    if (MERGE_SECTIONS.includes(k) && typeof v === 'object' && v !== null && typeof base[k] === 'object') {
      out[k] = deepMerge(base[k], v);
    } else {
      out[k] = v;
    }
  }
  return out;
}

function hasWalkSprite(folder) {
  try {
    return fs.readdirSync(folder).some((f) => {
      const low = f.toLowerCase();
      return low.endsWith('.png') && WALK_PATTERNS.some((p) => low.startsWith(p));
    });
  } catch { return false; }
}

// 配置文件支持两种扩展名:.jsonc(带注释,编辑器友好)优先,.json(严格 JSON)兜底
function hasConfig(folder) {
  return ['config.jsonc', 'config.json'].some((n) => fs.existsSync(path.join(folder, n)));
}

function findAsset(dirs, name) {
  if (!name) return null;
  for (const dir of dirs) {
    if (!dir) continue;
    const cand = path.join(dir, name);
    if (name.toLowerCase().endsWith('.png')) {
      const t = cand.replace(/\.png$/i, '_transparent.png');   // 优先透明版
      if (fs.existsSync(t)) return t;
    }
    if (fs.existsSync(cand)) return cand;
  }
  return null;
}

function scanCharacters(assetsRoot) {
  let folders = [];
  try {
    folders = fs.readdirSync(assetsRoot)
      .map((e) => path.join(assetsRoot, e))
      .filter((f) => { try { return fs.statSync(f).isDirectory(); } catch { return false; } })
      .filter((f) => hasWalkSprite(f) || hasConfig(f))
      .sort();
  } catch (e) { console.error('assets scan failed', e); return []; }
  const baseDir = folders.find(hasWalkSprite) || null;

  return folders.map((folder) => {
    let config = {};
    const cfgPath = ['config.jsonc', 'config.json']
      .map((n) => path.join(folder, n))
      .find((p) => fs.existsSync(p));
    if (cfgPath) {
      try { config = JSON.parse(stripJsonComments(fs.readFileSync(cfgPath, 'utf-8'))); }
      catch (e) { console.error('config error', cfgPath, e); }
    }
    const m = deepMerge(DEFAULT_CHARACTER, config);
    const fallbacks = [folder, baseDir, assetsRoot];
    return {
      id: m.id || path.basename(folder),
      folder,
      name: m.name || path.basename(folder),
      description: m.description || `来自 "${path.basename(folder)}" 文件夹的怪兽`,
      sprites: m.sprites, audio: m.audio, texts: m.texts,
      animation: m.animation, tint: m.tint, targeting: m.targeting,
      spritePath: (key) =>
        findAsset(fallbacks, m.sprites[key]) ||
        findAsset(fallbacks, DEFAULT_CHARACTER.sprites[key]),
      audioPath: (key) => findAsset([folder, assetsRoot], m.audio[key]),
    };
  });
}

// ---------- settings.json:exe 同目录 → %APPDATA% 回退 ----------
function settingsDir() {
  let candidate;
  if (app && app.isPackaged) {
    // 用户数据目录:安装目录会被覆盖安装/卸载清掉,设置放这里才不丢
    candidate = app.getPath('userData');
  } else if (app) {
    candidate = app.getAppPath();                 // dev:项目根目录
  } else {
    candidate = process.cwd();
  }
  try {
    fs.accessSync(candidate, fs.constants.W_OK);
    return candidate;
  } catch {
    const fb = path.join(process.env.APPDATA || os.homedir(), 'MonsterDeleter');
    fs.mkdirSync(fb, { recursive: true });
    return fb;
  }
}
function settingsPath() { return path.join(settingsDir(), 'settings.json'); }

function saveLastCharacter(id) {
  try {
    const data = fs.existsSync(settingsPath())
      ? JSON.parse(fs.readFileSync(settingsPath(), 'utf-8')) : {};
    data.last_character = id;
    fs.writeFileSync(settingsPath(), JSON.stringify(data, null, 2), 'utf-8');
  } catch (e) { console.error('save settings', e); }
}
function loadLastCharacter() {
  try {
    if (fs.existsSync(settingsPath())) {
      return JSON.parse(fs.readFileSync(settingsPath(), 'utf-8')).last_character || null;
    }
  } catch (e) { console.error('load settings', e); }
  return null;
}

// ---------- 通用设置读写(与 last_character 共用 settings.json) ----------
function loadSettings() {
  try {
    if (fs.existsSync(settingsPath())) {
      return JSON.parse(fs.readFileSync(settingsPath(), 'utf-8')) || {};
    }
  } catch (e) { console.error('load settings', e); }
  return {};
}
function saveSettings(patch) {
  try {
    fs.writeFileSync(settingsPath(), JSON.stringify({ ...loadSettings(), ...patch }, null, 2), 'utf-8');
  } catch (e) { console.error('save settings', e); }
}

// 桌面手动定位开关:桌面自动定位受分辨率/缩放/编码/桌面整理软件影响,
// 可能不准,用户可在主窗口勾选后强制改为十字准星手动点击瞄准
function loadManualTargeting() { return !!loadSettings().manual_targeting; }
function saveManualTargeting(v) { saveSettings({ manual_targeting: !!v }); }

module.exports = { scanCharacters, saveLastCharacter, loadLastCharacter, loadManualTargeting, saveManualTargeting };
