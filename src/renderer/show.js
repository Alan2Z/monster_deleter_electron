// 演出窗口:狙击 → 走路入场 → 指点 → 对话 → 踢踹 → 爆炸 → 雷欧登场 → 飞离 → 退出
// 行为对齐 Python 原版 main.py(MonsterDeleter 类),画在透明全屏窗口里:
// 三个 canvas(背景/怪兽/爆炸)+ 对话气泡 + 按钮

const bg = document.getElementById('bg');
const monsterCv = document.getElementById('monster');
const explosionCv = document.getElementById('explosion');
const bubble = document.getElementById('bubble');
const choices = document.getElementById('choices');
const btnYes = document.getElementById('btn-yes');
const btnNo = document.getElementById('btn-no');
const btnSwap = document.getElementById('btn-swap');
const bgm = document.getElementById('bgm');
const sfx = document.getElementById('sfx');
const boom = document.getElementById('boom');
const msg = document.getElementById('msg');

// 音量与原版一致(QMediaPlayer:bgm 0.5 / sfx 1.0 / 爆炸 0.3)
bgm.volume = 0.5;
sfx.volume = 1.0;
boom.volume = 0.3;

const dpr = devicePixelRatio;
function fitCanvas(cv) {
  cv.width = innerWidth * dpr;
  cv.height = innerHeight * dpr;
  cv.style.width = innerWidth + 'px';
  cv.style.height = innerHeight + 'px';
  cv.getContext('2d').setTransform(dpr, 0, 0, dpr, 0, 0);
}
[bg, monsterCv, explosionCv].forEach(fitCanvas);

function loadImage(src) {
  return new Promise((res, rej) => {
    const img = new Image();
    img.onload = () => res(img);
    img.onerror = () => rej(new Error('图片加载失败: ' + src));
    img.src = src;
  });
}
function playAudio(el, p) {
  if (!p) return;
  const url = window.api.toFileUrl(p);
  if (el.src !== url) el.src = url;   // 同源则续播(换角时 BGM 不打断,同原版 _apply_audio)
  el.play().catch(() => {});
}

// ---------- SpriteAnimator(canvas 逐帧动画,对应 Qt 的 SpriteAnimator) ----------
class SpriteAnimator {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.frames = [];
    this.i = 0;
    this.loop = true;
    this.flip = false;
    this.tint = null;
    this.timer = null;
    this.onFrame = null;   // 关键帧回调(Qt 的 frameChanged 信号)
    this.onEnd = null;
    this.x = 0; this.y = 0;   // 精灵绘制位置(像素)
    this.scale = 1;
  }
  get w() { return this.frames[0] ? this.frames[0].width * this.scale : 0; }
  get h() { return this.frames[0] ? this.frames[0].height * this.scale : 0; }
  async loadSpritesheet(url, cols = 5, rows = 3, frameIndices = null, targetHeight = 250) {
    const img = await loadImage(url);
    const fw = img.naturalWidth / cols, fh = img.naturalHeight / rows;
    this.frames = [];
    for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) {
      const off = document.createElement('canvas');
      off.width = fw; off.height = fh;
      off.getContext('2d').drawImage(img, c * fw, r * fh, fw, fh, 0, 0, fw, fh);
      this.frames.push(off);
    }
    if (frameIndices) this.frames = frameIndices.map((i) => this.frames[i]).filter(Boolean);
    this.scale = targetHeight / this.frames[0].height;
    this.i = 0;
  }
  play(fps = 8, loop = true) {
    this.loop = loop; this.i = 0; this.draw();
    clearInterval(this.timer);
    this.timer = setInterval(() => this.next(), 1000 / fps);
  }
  next() {
    this.i++;
    if (this.i >= this.frames.length) {
      if (this.loop) { this.i = 0; }
      else {
        this.i = this.frames.length - 1;
        clearInterval(this.timer);
        this.onEnd?.();
        return;
      }
    }
    this.draw();
    this.onFrame?.(this.i);
  }
  draw() {
    const f = this.frames[this.i];
    if (!f) return;
    const ctx = this.ctx;
    ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.save();
    ctx.translate(this.x, this.y);
    ctx.scale(this.flip ? -1 : 1, 1);
    ctx.drawImage(f, this.flip ? -this.w : 0, 0, this.w, this.h);
    if (this.tint) {   // 染色变体:source-atop 叠加(对应 QGraphicsColorizeEffect)
      ctx.globalCompositeOperation = 'source-atop';
      ctx.globalAlpha = this.tint.strength;
      ctx.fillStyle = this.tint.color;
      ctx.fillRect(this.flip ? -this.w : 0, 0, this.w, this.h);
      ctx.globalAlpha = 1;
      ctx.globalCompositeOperation = 'source-over';
    }
    ctx.restore();
  }
  setTint(color, strength) {
    this.tint = strength > 0.01 ? { color, strength } : null;
    this.draw();
  }
}

// ---------- 缓动移动(QPropertyAnimation + QEasingCurve) ----------
const EASING = {
  'out-quad': (t) => 1 - (1 - t) ** 2,   // OutQuad
  'in-quad': (t) => t * t,               // InQuad
};
function animateTo(anim, x0, y0, x1, y1, ms, easing, done) {
  const t0 = performance.now();
  (function tick(now) {
    const t = Math.min(1, (now - t0) / ms);
    const e = EASING[easing](t);
    anim.x = x0 + (x1 - x0) * e;
    anim.y = y0 + (y1 - y0) * e;
    anim.draw();
    if (t < 1) requestAnimationFrame(tick); else done?.();
  })(t0);
}

// ---------- 瞄准遮罩(对应参考项目 paint_background + paintEvent) ----------
const bgCtx = bg.getContext('2d');
let bgAlpha = 0;            // 当前淡入进度(0 → bgOpacity)
let bgOpacity = 0.35;       // 背景图目标不透明度,来自角色 config 的 targeting.bg_opacity
let bgImg = null;           // 本角色 targeting_bg.png(缺失退化为黑色遮罩)

async function loadBgImage() {
  if (!char || !char.targetBg || bgImg) return;
  try { bgImg = await loadImage(window.api.toFileUrl(char.targetBg)); } catch { bgImg = null; }
  if (bgAlpha > 0.01) drawBg();   // 淡入过程中加载完,补画一帧
}

// 背景图按【原本尺寸】居中绘制(不拉伸铺满),透明度跟随淡入到 bgOpacity;
// 图片缺失时退化为黑色遮罩(参考项目 QColor(0,0,0,160) × opacity)
function drawBg() {
  bgCtx.clearRect(0, 0, bg.width, bg.height);
  if (bgAlpha <= 0.01) return;
  if (bgImg) {
    bgCtx.globalAlpha = bgAlpha;   // 0 → bgOpacity(config 可调,默认 0.35)
    const w = bgImg.naturalWidth, h = bgImg.naturalHeight;   // 原图尺寸,不缩放
    bgCtx.drawImage(bgImg, (bg.width - w) / 2, (bg.height - h) / 2, w, h);
    bgCtx.globalAlpha = 1;
  } else {
    bgCtx.fillStyle = `rgba(0, 0, 0, ${0.627 * bgAlpha})`;
    bgCtx.fillRect(0, 0, bg.width, bg.height);
  }
  // 白色加粗提示文字居中,透明度随遮罩(参考项目 30pt bold ≈ 40px)
  if (char) {
    bgCtx.globalAlpha = Math.min(1, bgAlpha / bgOpacity);
    bgCtx.fillStyle = '#ffffff';
    bgCtx.font = "bold 40px 'Segoe UI', 'Microsoft YaHei', sans-serif";
    bgCtx.textAlign = 'center';
    bgCtx.textBaseline = 'middle';
    bgCtx.fillText(char.texts.targeting, bg.width / 2, bg.height / 2);
    bgCtx.globalAlpha = 1;
  }
}
function fadeBg(to, ms, done) {
  const t0 = performance.now();
  const from = bgAlpha;
  (function tick(now) {
    const t = Math.min(1, (now - t0) / ms);
    bgAlpha = from + (to - from) * (1 - (1 - t) ** 2);
    drawBg();
    if (t < 1) requestAnimationFrame(tick); else done?.();
  })(t0);
}

// ---------- 全局状态 ----------
let targetPos = null;
let char = null;
let targetFile = null;
let chars = [];
let showStarted = false;   // 开演后忽略迟到的定位/点击,防止重复开演
let manualMode = false;    // 本次召唤是否走手动瞄准(开关开启 且 目标是桌面文件)

const monsterAnim = new SpriteAnimator(monsterCv);
const explosionAnim = new SpriteAnimator(explosionCv);

// ---------- 入口:主进程发来目标文件 ----------
window.api.onInitShow(async (d) => {
  targetFile = d.targetFile;
  chars = await window.api.scanCharacters();
  if (!chars.length) {
    msg.style.display = 'block';
    return;
  }
  const last = await window.api.getLastCharacter();
  char = chars.find((c) => c.id === last) || chars[0];
  const op = char.targeting && char.targeting.bg_opacity != null ? Number(char.targeting.bg_opacity) : 0.35;
  bgOpacity = Math.min(1, Math.max(0, op));   // 瞄准背景图不透明度(config 可调,默认 0.35)
  loadBgImage();   // 预加载瞄准背景图(手动兜底时用,与定位并行)

  // 手动定位开关只对桌面目标生效:桌面文件 → 直接出十字准星手动点击;
  // 文件夹里的文件 → 照常自动定位(不受开关影响)。
  if (await window.api.getManualTargeting() && d.onDesktop) {
    manualMode = true;
    initTargeting(char);
    return;
  }

  if (d.targetPos) { startShowNow(d.targetPos); return; }
  if (pendingTarget) { startShowNow(pendingTarget); return; }
  if (d.failed) initTargeting(char);   // 定位失败 → 手动瞄准兜底(十字准星 + 点击)
  // 否则:主进程还在定位(窗口保持透明,定位完成直接开演,无需提示)
});

// 主进程定位到文件图标后发来精确坐标(可能早于 init-show 到达,先存着)
let pendingTarget = null;
window.api.onAutoTarget((pos) => {
  if (showStarted || manualMode) return;
  if (!char) { pendingTarget = pos; return; }
  startShowNow(pos);
});

// 定位彻底失败:切手动瞄准,让用户自己点(不瞎猜光标位置)
window.api.onAutoTargetFailed(() => {
  if (showStarted || manualMode) return;
  if (char) initTargeting(char);
});

// 自动瞄准开演:直接开演(怪兽从屏幕外走进来本身就是入场)
function startShowNow(pos) {
  if (showStarted) return;
  showStarted = true;
  targetPos = pos;
  document.body.style.cursor = 'default';
  bubble.style.display = 'none';
  startShow();
}

// ---------- 狙击瞄准(对应 init_targeting_ui + paintEvent;仅定位失败兜底用) ----------
function initTargeting(c) {
  document.body.style.cursor = `url('data:image/svg+xml;utf8,${encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40"><circle cx="20" cy="20" r="12" fill="none" stroke="red" stroke-width="2"/><path d="M20 0v8M20 32v8M0 20h8M32 20h8" stroke="red" stroke-width="2"/></svg>`
  )}') 20 20, crosshair`;
  fadeBg(bgOpacity, 800);   // 原版 fade_in:800ms → 配置的不透明度(默认 0.35)

  window.addEventListener('click', (e) => {   // 原版 mousePressEvent(左键)
    if (showStarted) return;
    showStarted = true;
    targetPos = { x: e.clientX, y: e.clientY };
    document.body.style.cursor = 'default';
    fadeBg(0, 500, startShow);   // 原版 fade_out:500ms
  }, { once: true });
}

// ---------- 演出状态机(对应 start_phase1_walk → phase5) ----------
async function startShow() {
  const m = monsterAnim;
  m.onFrame = null;
  m.onEnd = null;
  playAudio(bgm, char.paths.audio.bgm);   // 原版:phase1 开始播 BGM(此时已有点击手势)

  const h = char.animation.sprite_height;
  const y = targetPos.y - h / 2 + char.animation.walk_y_offset;

  m.setTint(char.tint.color, char.tint.strength);
  await m.loadSpritesheet(window.api.toFileUrl(char.paths.sprites.walk), 5, 3, null, h);
  // 目标太靠左时从右边进场:默认终点在目标左边(怪兽面向右指向文件),
  // 若目标贴着屏幕左缘,终点会变成负坐标,怪兽整只走到屏幕外(卡在左边)。
  // 此时镜像翻面、从右侧进场,终点在目标右边,脸朝左指着文件。
  const gap = char.animation.target_gap;
  const fromRight = targetPos.x - m.w - gap < 0;
  const startX = fromRight ? innerWidth + m.w : -m.w;       // 原版 start_x = -width:整宽在屏幕外
  const endX = fromRight ? targetPos.x + gap : targetPos.x - m.w - gap;
  m.flip = fromRight;
  m.x = startX; m.y = y;
  m.play(char.animation.fps, true);         // 边走边播走路动画(原版 play + move 并行)
  animateTo(m, startX, y, endX, y, char.animation.walk_duration_ms, 'out-quad', async () => {
    playAudio(sfx, char.paths.audio.voice);   // 原版:指点动画开始播 SFX
    await m.loadSpritesheet(window.api.toFileUrl(char.paths.sprites.point), 5, 3, char.sprites.point_frames, h);
    m.play(char.animation.fps, false);
    m.onEnd = showDialog;
  });
}

function showDialog() {   // 原版 show_dialog:气泡在怪兽中心上方,按钮在正下方
  const m = monsterAnim;
  bubble.textContent = char.texts.dialog;
  bubble.style.transform = 'none';
  bubble.style.left = `${m.x + m.w / 2 - 80}px`;
  bubble.style.top = `${m.y - 60}px`;
  bubble.style.display = 'block';

  btnYes.textContent = char.texts.choice_yes;
  btnNo.textContent = char.texts.choice_no;
  btnSwap.textContent = char.texts.swap;
  choices.style.left = `${m.x + m.w / 2 - 130}px`;
  choices.style.top = `${m.y + m.h - 20}px`;
  choices.style.display = 'flex';

  btnYes.onclick = btnNo.onclick = () => {
    choices.style.display = 'none';
    bubble.style.display = 'none';
    startKick();   // 原版 choiceMade → start_phase3_kick
  };
  btnSwap.onclick = () => {
    choices.style.display = 'none';
    bubble.style.display = 'none';
    window.api.swapCharacter();   // 主进程打开角色窗口(换角模式)
  };
}

async function startKick() {
  const m = monsterAnim;
  await m.loadSpritesheet(window.api.toFileUrl(char.paths.sprites.kick), 5, 3, null, char.animation.sprite_height);
  m.onFrame = (i) => { if (i === char.animation.kick_frame) triggerExplosion(); };   // 原版:第 6 帧爆炸
  m.onEnd = async () => {
    m.onFrame = null;   // 原版 on_kick_finished 里 disconnect 两个信号
    await m.loadSpritesheet(window.api.toFileUrl(char.paths.sprites.leo), 5, 3, null, char.animation.sprite_height);
    m.play(char.animation.fps, false);
    m.onEnd = async () => {
      await m.loadSpritesheet(window.api.toFileUrl(char.paths.sprites.fly), 5, 3, null, char.animation.sprite_height);
      m.play(char.animation.fps, true);
      animateTo(m, m.x, m.y, innerWidth + 200, m.y, char.animation.fly_duration_ms, 'in-quad', () => window.api.closeApp());
    };
  };
  m.play(char.animation.fps, false);
}

function triggerExplosion() {   // 对应原版 trigger_explosion
  playAudio(boom, char.paths.audio.explosion);
  const ex = explosionAnim;
  ex.setTint(char.tint.color, char.tint.strength);
  if (!char.paths.sprites.explosion) return;
  ex.loadSpritesheet(window.api.toFileUrl(char.paths.sprites.explosion), 5, 3, null, char.animation.explosion_height)
    .then(() => {
      ex.x = targetPos.x - ex.w / 2;
      ex.y = targetPos.y - ex.h / 2 - char.animation.explosion_y_offset;   // 原版:略高于文件图标
      ex.onEnd = () => ex.ctx.clearRect(0, 0, ex.canvas.width, ex.canvas.height);   // 播完隐藏
      ex.play(char.animation.fps, false);
    })
    .catch(() => {});
  window.api.trashFile(targetFile).then((r) => {   // 同步爆炸时机,同原版
    if (!r || !r.ok) console.warn('回收站删除失败:', r && r.reason);
  });
}

// 换角:主进程通知 → 换角色 → 重新演出(原版 on_character_selected → start_phase1_walk)
window.api.onSwapDone(async (charId) => {
  const next = chars.find((c) => c.id === charId);
  if (!next) return;
  char = next;
  startShow();
});

// Esc 退出(对应 keyPressEvent → on_app_exit)
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') window.api.closeApp();
});
