// 角色选择窗口 —— 卡片渲染 + 选择即保存
// 换角模式(?swap=1):演出中"换一只"打开,选中后通知演出窗口并关闭
const SWAP_MODE = new URLSearchParams(location.search).get('swap') === '1';

const cardsEl = document.getElementById('cards');
const statusEl = document.getElementById('status');
const emptyEl = document.getElementById('empty');
const randomBtn = document.getElementById('random');
const manualToggle = document.getElementById('manual-toggle');

// 桌面手动定位开关:读取上次选择,勾选即持久化(见 show.js:手动模式下召唤直接出十字准星)
window.api.getManualTargeting().then((v) => { manualToggle.checked = !!v; });
manualToggle.onchange = () => window.api.saveManualTargeting(manualToggle.checked);

let chars = [];

// 预览图 = 走路 spritesheet 第一帧(5×3 切片,高 110,同原版 preview_pixmap)
async function previewUrl(path) {
  if (!path) return null;
  const img = new Image();
  await new Promise((res, rej) => {
    img.onload = res;
    img.onerror = rej;
    img.src = window.api.toFileUrl(path);
  });
  const fw = img.naturalWidth / 5, fh = img.naturalHeight / 3;
  const cv = document.createElement('canvas');
  cv.width = fw; cv.height = fh;
  cv.getContext('2d').drawImage(img, 0, 0, fw, fh, 0, 0, fw, fh);
  return cv.toDataURL('image/png');
}

async function renderCards(last) {
  for (const c of chars) {
    const card = document.createElement('div');
    card.className = 'card' + (c.id === last ? ' selected' : '');

    const url = await previewUrl(c.paths.sprites.walk);
    if (url) {
      const img = document.createElement('img');
      img.src = url;
      card.appendChild(img);
    }

    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = c.name;

    const desc = document.createElement('div');
    desc.className = 'desc';
    desc.textContent = c.description;

    card.append(name, desc);
    card.onclick = () => selectCharacter(c, card);
    cardsEl.appendChild(card);
  }
}

async function selectCharacter(c, card) {
  await window.api.saveLastCharacter(c.id);
  window.api.updateContextMenuName(c.name);   // 右键菜单名跟随所选角色
  if (SWAP_MODE) {
    window.api.swapCharacterSelected(c.id);   // 通知主进程转给演出窗口
    window.close();
    return;
  }
  document.querySelectorAll('.card.selected').forEach((el) => el.classList.remove('selected'));
  card.classList.add('selected');
  statusEl.textContent = `当前默认角色：${c.name}`;
}

async function init() {
  chars = await window.api.scanCharacters();
  if (!chars.length) {
    cardsEl.style.display = 'none';
    randomBtn.style.display = 'none';
    emptyEl.style.display = 'block';
    return;
  }
  const last = await window.api.getLastCharacter();
  await renderCards(last);

  const cur = chars.find((c) => c.id === last) || chars[0];
  if (cur) statusEl.textContent = `当前默认角色：${cur.name}`;

  randomBtn.onclick = () => {
    const c = chars[Math.floor(Math.random() * chars.length)];
    selectCharacter(c, cardsEl.children[chars.indexOf(c)]);
  };
}
init();

// 自定义角色:打开 assets 目录(手册和怪兽文件夹都在里面)
const customBtn = document.getElementById('custom-btn');
customBtn.onclick = async () => {
  const err = await window.api.openAssetsDir();
  if (err) statusEl.textContent = '打开 assets 目录失败:' + err;
};

// ---------- 右键角色卡片:编辑配置文件(CodeMirror) ----------
const ctxMenu = document.getElementById('ctx-menu');
const configModal = document.getElementById('config-modal');
const modalTitle = document.getElementById('modal-title');
const errEl = document.getElementById('config-err');
let ctxChar = null;   // 当前右键的角色
let editor = null;

document.addEventListener('contextmenu', (e) => {
  const card = e.target.closest('.card');
  if (!card) return;
  e.preventDefault();
  ctxChar = chars[Array.from(cardsEl.children).indexOf(card)];
  if (!ctxChar) return;
  ctxMenu.style.display = 'block';
  ctxMenu.style.left = Math.min(e.clientX, innerWidth - 190) + 'px';
  ctxMenu.style.top = Math.min(e.clientY, innerHeight - 80) + 'px';
});
document.addEventListener('click', () => { ctxMenu.style.display = 'none'; });

// 与 characters.js 相同的注释剥离(保存前校验 JSONC 用)
function stripComments(text) {
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
    if (ch === '/' && text[i + 1] === '/') {
      while (i < text.length && text[i] !== '\n') i++;
      out += '\n';
      continue;
    }
    if (ch === '/' && text[i + 1] === '*') {
      i += 2;
      while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) i++;
      i++;
      continue;
    }
    out += ch;
  }
  return out;
}

document.getElementById('ctx-edit-config').onclick = async () => {
  ctxMenu.style.display = 'none';
  errEl.textContent = '';
  const r = await window.api.readCharacterConfig(ctxChar.folder);
  modalTitle.textContent = `${ctxChar.name} - ${r.ok ? r.file : '无配置文件(保存将创建)'}`;
  if (!editor) {
    editor = CodeMirror.fromTextArea(document.getElementById('config-editor'), {
      mode: { name: 'javascript', json: true },
      theme: 'monokai',
      lineNumbers: true,
      lineWrapping: true,
      tabSize: 2,
    });
  }
  editor.setValue(r.ok ? r.content : r.template);
  configModal.style.display = 'flex';
  editor.refresh();
};

document.getElementById('modal-close').onclick = () => { configModal.style.display = 'none'; };
configModal.addEventListener('click', (e) => { if (e.target === configModal) configModal.style.display = 'none'; });

document.getElementById('config-save').onclick = async () => {
  errEl.textContent = '';
  const content = editor.getValue();
  try {
    JSON.parse(stripComments(content));   // 保存前校验
  } catch (e) {
    errEl.textContent = '❌ 语法错误:' + e.message;
    return;
  }
  const r = await window.api.writeCharacterConfig(ctxChar.folder, content);
  if (r.ok) {
    location.reload();   // 刷新卡片(下次召唤的参数下次扫描即生效)
  } else {
    errEl.textContent = '❌ 保存失败:' + (r.message || '未知错误');
  }
};

// 如何卸载:弹出可关闭的说明卡片
const uninstallModal = document.getElementById('uninstall-modal');
document.getElementById('uninstall-btn').onclick = () => { uninstallModal.style.display = 'flex'; };
document.getElementById('uninstall-close').onclick = () => { uninstallModal.style.display = 'none'; };
uninstallModal.addEventListener('click', (e) => { if (e.target === uninstallModal) uninstallModal.style.display = 'none'; });

// Esc:standalone 关闭窗口(应用退出);换角模式只关窗口,演出窗口继续(原版 keyPressEvent)
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if (uninstallModal.style.display === 'flex') { uninstallModal.style.display = 'none'; return; }
    window.close();
  }
});
