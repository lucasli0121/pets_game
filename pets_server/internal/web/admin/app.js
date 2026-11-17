/* 宠屋养成记 管理后台 —— 零构建单页应用，无第三方依赖。
   数据流：登录 → 取草稿(或复制生效态) → 各配置页编辑草稿 → 保存草稿 → 查看 diff → 发布。
   编辑态与发布态分离，页面上所有改动都只作用于草稿。 */

const API = '/api/admin';
const LS_TOKEN = 'pets_admin_token';
const LS_WHO = 'pets_admin_who';

let state = {
  token: localStorage.getItem(LS_TOKEN) || '',
  me: null,
  draft: null,      // {config:{...}, version, updatedBy, updatedAt}
  dirty: false,
  page: 'food',
  releases: [],
};

/* ---------------- 基础 ---------------- */

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));

function h(v) {
  return String(v == null ? '' : v)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function toast(msg, isErr) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'toast show' + (isErr ? ' err' : '');
  clearTimeout(t._tid);
  t._tid = setTimeout(() => (t.className = 'toast'), 2600);
}

async function api(path, opts = {}) {
  const res = await fetch(API + path, {
    method: opts.method || 'GET',
    headers: Object.assign(
      { 'Content-Type': 'application/json' },
      state.token ? { Authorization: 'Bearer ' + state.token } : {}
    ),
    body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
  });
  let json;
  try { json = await res.json(); } catch (e) { throw new Error('返回不是 JSON（HTTP ' + res.status + '）'); }
  if (res.status === 401) { logout(true); throw new Error(json.msg || '登录已失效'); }
  if (json.code !== 0) throw new Error(json.msg || ('错误码 ' + json.code));
  return json.data;
}

async function apiQuiet(path, opts = {}) {
  try { return await api(path, opts); } catch (e) { toast(e.message, true); return null; }
}

/* ---------------- 登录 ---------------- */

async function doLogin(e) {
  e.preventDefault();
  const body = { username: $('#username').value.trim(), password: $('#password').value };
  try {
    const data = await api('/login', { method: 'POST', body });
    state.token = data.token;
    state.me = data.admin;
    localStorage.setItem(LS_TOKEN, data.token);
    localStorage.setItem(LS_WHO, JSON.stringify({ name: data.admin.username, role: data.admin.role }));
    boot();
  } catch (err) {
    $('#loginHint').textContent = err.message;
  }
}

function logout(silent) {
  state.token = ''; state.me = null; state.draft = null; state.dirty = false;
  localStorage.removeItem(LS_TOKEN); localStorage.removeItem(LS_WHO);
  $('#shell').classList.add('hidden');
  $('#loginView').classList.remove('hidden');
  if (!silent) toast('已退出登录');
}

/* ---------------- 路由 ---------------- */

const ROLE_CN = { super: '超级管理员', config: '配置运营', readonly: '只读运营' };

const PAGES = [
  { group: '配置', id: 'food', title: 'A2 · 配置 - 食品', sub: '数值改动先存草稿，发布后客户端下次启动生效' },
  { group: '配置', id: 'scene', title: 'A3 · 配置 - 场景', sub: '价格、缩略图、启用状态与排序' },
  { group: '配置', id: 'points', title: 'A4 · 配置 - 积分与互动', sub: '初始值、签到、升级、广告、上限与衰减速率' },
  { group: '配置', id: 'ad', title: 'A5 · 配置 - 广告位', sub: '激励视频发奖，插屏只记曝光不发奖' },
  { group: '配置', id: 'flags', title: 'A6 · 配置 - 功能开关', sub: '灰度开关，客户端按 key 读取' },
  { group: '配置', id: 'level', title: 'A12 · 配置 - 等级', sub: '逐级配置升级经验与奖励，服务端只读此表' },
  { group: '发布', id: 'publish', title: 'A7 · 发布与回滚', sub: '查看 diff、发布新版本、回滚到历史版本' },
  { group: '运营', id: 'dashboard', title: 'A10 · 数据看板', sub: '积分通胀率是 V1 最该盯的指标' },
  { group: '运营', id: 'users', title: 'A8 · 用户查询', sub: '按 openid / 昵称查用户，查看积分、宠物、流水' },
  { group: '运营', id: 'audit', title: 'A11 · 操作日志', sub: '谁在什么时候改了什么' },
  { group: '帮助', id: 'apidoc', title: '接口文档', sub: '全部 32 个接口的字段说明，可直接在线调试' },
];

function renderNav() {
  let html = '';
  let lastGroup = '';
  for (const p of PAGES) {
    if (p.group !== lastGroup) { html += `<div class="nav-group">${h(p.group)}</div>`; lastGroup = p.group; }
    const active = state.page === p.id ? ' active' : '';
    html += `<a class="nav-item${active}" href="#${p.id}" data-page="${p.id}">${h(p.title.replace(/^A\d+ · /, ''))}</a>`;
  }
  $('#nav').innerHTML = html;
  $$('#nav .nav-item').forEach(a => a.onclick = (e) => { e.preventDefault(); go(a.dataset.page); });
}

function go(page) {
  state.page = page;
  location.hash = page;
  renderNav();
  render();
}

function render() {
  const meta = PAGES.find(p => p.id === state.page) || PAGES[0];
  $('#pageTitle').textContent = meta.title;
  $('#pageSub').textContent = meta.sub;
  $('#topActions').innerHTML = '';
  $('#dirtyBar').classList.toggle('hidden', !state.dirty);
  const view = $('#view');
  view.innerHTML = '<div class="empty">加载中…</div>';
  const fn = RENDER[state.page];
  if (fn) fn(view);
}

/* ---------------- 草稿 ---------------- */

async function ensureDraft(force) {
  if (state.draft && !force) return state.draft;
  const data = await api('/config/draft');
  state.draft = data;
  return data;
}

function markDirty() {
  state.dirty = true;
  $('#dirtyBar').classList.remove('hidden');
}

async function saveDraft() {
  const r = await apiQuiet('/config/draft', { method: 'PUT', body: { config: state.draft.config } });
  if (!r) return false;
  state.dirty = false;
  $('#dirtyBar').classList.add('hidden');
  toast('草稿已保存（尚未生效）');
  return true;
}

async function showDiff() {
  const data = await apiQuiet('/config/diff');
  if (!data) return;
  const rows = data.changes.map(c => `
    <tr>
      <td>${h(c.table)}</td>
      <td class="mono">${h(c.itemId)}</td>
      <td class="mono">${h(c.field)}</td>
      <td class="before">${h(c.before)}</td>
      <td class="after">${h(c.after)}</td>
    </tr>`).join('');
  openModal('本次改动（' + data.count + ' 处）', data.count
    ? `<div class="table-wrap"><table class="diff-table"><thead><tr><th>表</th><th>条目</th><th>字段</th><th>发布前</th><th>发布后</th></tr></thead><tbody>${rows}</tbody></table></div>`
    : '<div class="empty">草稿与线上一致，没有改动。</div>',
    [{ text: '关闭', fn: closeModal }]);
}

async function doPublish() {
  const note = prompt('填写变更说明（会写进发布记录）：', '');
  if (note === null) return;
  if (state.dirty) {
    const okSave = await saveDraft();
    if (!okSave) return;
  }
  const r = await apiQuiet('/config/publish', { method: 'POST', body: { note } });
  if (!r) return;
  toast('已发布 v' + r.version + '：' + r.hint);
  state.draft = null;
  state.dirty = false;
  $('#dirtyBar').classList.add('hidden');
  render();
}

/* ---------------- 弹窗 ---------------- */

function openModal(title, bodyHtml, actions) {
  $('#modalTitle').textContent = title;
  $('#modalBody').innerHTML = bodyHtml;
  const foot = $('#modalFoot');
  foot.innerHTML = '';
  (actions || []).forEach(a => {
    const b = document.createElement('button');
    b.className = 'btn ' + (a.cls || '');
    b.textContent = a.text;
    b.onclick = a.fn;
    foot.appendChild(b);
  });
  $('#modal').classList.remove('hidden');
}
function closeModal() { $('#modal').classList.add('hidden'); }

/* ---------------- 通用表格编辑 ---------------- */

function bindEdits(root, onChange) {
  $$('[data-path]', root).forEach(inp => {
    inp.addEventListener('change', () => {
      const path = inp.dataset.path;
      let val;
      if (inp.type === 'checkbox') val = inp.checked;
      else if (inp.type === 'number') val = Number(inp.value) || 0;
      else val = inp.value;
      setPath(state.draft.config, path, val);
      onChange && onChange(path, val);
      markDirty();
    });
  });
}

function setPath(obj, path, val) {
  const parts = path.split('.');
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i++) cur = cur[parts[i]];
  cur[parts[parts.length - 1]] = val;
}

function numCell(path, val, min, step) {
  return `<input type="number" value="${h(val)}" data-path="${h(path)}" min="${min == null ? 0 : min}" ${step ? 'step="' + step + '"' : ''}>`;
}
function textCell(path, val) {
  return `<input type="text" value="${h(val)}" data-path="${h(path)}">`;
}
function checkCell(path, val) {
  return `<input type="checkbox" data-path="${h(path)}" ${val ? 'checked' : ''}>`;
}

/* ---------------- 页面：食品 ---------------- */

const RENDER = {};

RENDER.food = async (view) => {
  const d = await ensureDraft();
  const foods = d.config.food || [];
  const rows = foods.map((f, i) => `
    <tr>
      <td class="mono">${h(f.id)}</td>
      <td>${textCell(`food.${i}.name`, f.name)}</td>
      <td>
        <select data-path="food.${i}.category">
          ${['MAIN', 'SNACK', 'SPECIAL'].map(c => `<option value="${c}" ${f.category === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select>
      </td>
      <td class="num">${numCell(`food.${i}.price`, f.price)}</td>
      <td class="num">${numCell(`food.${i}.satiety`, f.satiety, 0)}</td>
      <td class="num">${numCell(`food.${i}.happiness`, f.happiness, 0)}</td>
      <td class="num">${numCell(`food.${i}.exp`, f.exp, 0)}</td>
      <td>${textCell(`food.${i}.icon`, f.icon)}</td>
      <td class="num">${numCell(`food.${i}.sort`, f.sort)}</td>
      <td>${checkCell(`food.${i}.enabled`, f.enabled)}</td>
      <td class="inline-actions">
        <button class="btn sm ghost" data-act="del-food" data-i="${i}">删除</button>
      </td>
    </tr>`).join('');

  view.innerHTML = `
    <div class="card">
      <div class="card-head">
        <h3>食物配置（${foods.length} 项）</h3>
        <div class="row">
          <button class="btn sm" id="batchPrice">批量改价</button>
          <button class="btn sm primary" id="addFood">新增食物</button>
        </div>
      </div>
      <div class="card-body tight">
        <div class="table-wrap">
          <table>
            <thead><tr>
              <th>ID</th><th>名称</th><th>分类</th><th>价格</th><th>饱腹</th>
              <th>快乐</th><th>经验</th><th>图标</th><th>排序</th><th>上架</th><th></th>
            </tr></thead>
            <tbody>${rows || '<tr><td colspan="11" class="empty">还没有食物</td></tr>'}</tbody>
          </table>
        </div>
      </div>
    </div>
    <p class="hint">校验：价格 ≥ 0；饱腹/快乐 0–100；经验 0–1000；至少保留一个上架食物。</p>`;

  bindEdits(view, (path, val) => {
    if (path.endsWith('.satiety') || path.endsWith('.happiness')) {
      if (val > 100) toast('饱腹/快乐增量超过 100，保存时会被拒', true);
    }
  });

  $$('[data-act="del-food"]', view).forEach(b => b.onclick = () => {
    state.draft.config.food.splice(Number(b.dataset.i), 1);
    markDirty(); render();
  });

  $('#addFood', view).onclick = () => {
    const id = prompt('新食物 id（英文/数字/下划线，如 food_fish）：', 'food_new');
    if (!id) return;
    state.draft.config.food.push({
      id, name: '新食物', category: 'MAIN', price: 10,
      satiety: 20, happiness: 5, exp: 5, icon: 'icon_' + id,
      sort: (state.draft.config.food.length + 1), enabled: true, minLevel: 1,
    });
    markDirty(); render();
  };

  $('#batchPrice', view).onclick = () => {
    const pct = Number(prompt('全部食物价格按百分比调整（如 10 表示涨 10%，-10 表示降 10%）：', '0'));
    if (!pct) return;
    state.draft.config.food.forEach(f => { f.price = Math.max(0, Math.round(f.price * (1 + pct / 100))); });
    markDirty(); render();
  };
};

/* ---------------- 页面：场景 ---------------- */

RENDER.scene = async (view) => {
  const d = await ensureDraft();
  const list = d.config.scene || [];
  // 老草稿可能没有 layout 字段（这批数据是后加的），
  // 不补一个空对象的话 setPath 会在 scene.0.layout.orthographicSize 上中途拿到 undefined。
  list.forEach(s => { if (!s.layout) s.layout = {}; });

  const rows = list.map((s, i) => `
    <tr>
      <td class="mono">${h(s.id)}</td>
      <td>${textCell(`scene.${i}.name`, s.name)}</td>
      <td class="num">${numCell(`scene.${i}.price`, s.price)}</td>
      <td>${textCell(`scene.${i}.thumb`, s.thumb)}</td>
      <td>${textCell(`scene.${i}.scenePath`, s.scenePath || '')}</td>
      <td>${textCell(`scene.${i}.sceneKey`, s.sceneKey)}</td>
      <td class="num">${numCell(`scene.${i}.sort`, s.sort)}</td>
      <td>${checkCell(`scene.${i}.enabled`, s.enabled)}</td>
      <td class="inline-actions"><button class="btn sm ghost" data-act="del-scene" data-i="${i}">删除</button></td>
    </tr>`).join('');

  // 每张背景图的实测构图数据点。分组呈现，因为它们是三组语义不同的量：
  // 取景（相机）、地面（两条线）、活动区、光照。
  const L = (i, k) => list[i].layout[k];
  const F = (i, k, label, val, hint) => `
    <div class="field">
      <label>${h(label)}</label>
      ${numCell(`scene.${i}.layout.${k}`, val, 0, 'any')}
      ${hint ? `<span class="hint">${h(hint)}</span>` : ''}
    </div>`;

  const layoutCards = list.map((s, i) => `
    <div class="card">
      <div class="card-head"><h3>${h(s.id)} · 实测构图数据点</h3></div>
      <div class="card-body">
        <div class="grid-form">
          ${F(i, 'orthographicSize', '正交可视半高 (m)', L(i, 'orthographicSize'), '决定狗占画面多少。1.9 时 0.76m 的狗约占 20%。')}
          ${F(i, 'cameraPitch', '相机俯角 (°)', L(i, 'cameraPitch'), '0=水平看。俯视构图才看得到地面。')}
          ${F(i, 'cameraDistance', '相机距离 (m)', L(i, 'cameraDistance'), '正交下不影响大小，只影响裁剪范围。')}
          ${F(i, 'floorFarLine', '地板最远端', L(i, 'floorFarLine'), '只作自检基准，不参与相机计算。')}
          ${F(i, 'dogGroundLine', '狗脚底线', L(i, 'dogGroundLine'), '相机按它反推高度，必须小于地板最远端。')}
          ${F(i, 'wanderHalfX', '活动区半宽 (m)', L(i, 'wanderHalfX'))}
          ${F(i, 'wanderHalfZ', '活动区半深 (m)', L(i, 'wanderHalfZ'))}
          ${F(i, 'wanderCenterX', '活动区中心 X 偏移 (m)', L(i, 'wanderCenterX'), '用来避开画面里的家具。')}
          ${F(i, 'wanderCenterZ', '活动区中心 Z 偏移 (m)', L(i, 'wanderCenterZ'))}
          ${F(i, 'lightEulerX', '主光俯角 (°)', L(i, 'lightEulerX'), '必须与背景图光源一致，否则有两套影子。')}
          ${F(i, 'lightEulerY', '主光水平角 (°)', L(i, 'lightEulerY'))}
          ${F(i, 'lightEulerZ', '主光翻滚 (°)', L(i, 'lightEulerZ'))}
          ${F(i, 'lightColorR', '主光 R', L(i, 'lightColorR'))}
          ${F(i, 'lightColorG', '主光 G', L(i, 'lightColorG'))}
          ${F(i, 'lightColorB', '主光 B', L(i, 'lightColorB'))}
          ${F(i, 'ambientIntensity', '环境光强度', L(i, 'ambientIntensity'), '夜间必须压低，否则宠物亮得像贴纸。')}
          ${F(i, 'shadowOpacity', '假阴影不透明度', L(i, 'shadowOpacity'), '0=不显示。')}
        </div>
        <div class="field">
          <label>实测记录</label>
          <input type="text" data-path="scene.${i}.layout.layoutNote"
                 value="${h(L(i, 'layoutNote') || '')}"
                 placeholder="量的是什么东西、怎么量的、哪几列一致">
          <span class="hint">写清楚量法，下一张图照着量，不用再靠猜。</span>
        </div>
      </div>
    </div>`).join('');

  view.innerHTML = `
    <div class="card">
      <div class="card-head">
        <h3>场景配置（${list.length} 项）</h3>
        <button class="btn sm primary" id="addScene">新增场景</button>
      </div>
      <div class="card-body tight">
        <div class="table-wrap"><table>
          <thead><tr><th>ID</th><th>名称</th><th>价格</th><th>缩略图路径</th><th>场景图路径</th><th>场景资源 key</th><th>排序</th><th>启用</th><th></th></tr></thead>
          <tbody>${rows || '<tr><td colspan="9" class="empty">还没有场景</td></tr>'}</tbody>
        </table></div>
      </div>
    </div>
    <p class="hint">
      资源定位是「远程优先、本地兜底」：客户端先下载「场景图路径」拼出的 CDN 地址，
      拉不到（没配 CDN、离线、网络失败）再按「场景资源 key」加载安装包里的图。
      两列填的都是<strong>相对路径</strong>，不能填 http:// 开头的完整 URL，也不能带 <code>..</code>；
      CDN 根地址由服务端环境变量 <code>ASSET_BASE_URL</code> 决定，不在这里配。
      场景图路径与场景资源 key 至少要有一个非空。
    </p>
    <p class="hint">必须保留至少一个价格为 0 且启用的场景，否则新用户无处可去。</p>
    ${layoutCards}
    <p class="hint">
      <strong>地板最远端</strong>与<strong>狗脚底线</strong>是两个不同的量，不能混用：
      前者量的是墙脚线（户外量远处地面与植被的交界），只是构图自检的参考基准；
      后者才是相机反推高度的目标。拿前者当对齐目标，等于强迫狗永远站在贴墙处。
      狗脚底线必须小于地板最远端，否则狗会悬空或贴住墙脚，发布时会被服务端拦下。
    </p>`;

  bindEdits(view);
  $$('[data-act="del-scene"]', view).forEach(b => b.onclick = () => {
    state.draft.config.scene.splice(Number(b.dataset.i), 1);
    markDirty(); render();
  });
  $('#addScene', view).onclick = () => {
    const id = prompt('新场景 id（如 park_night）：', 'park_night');
    if (!id) return;
    state.draft.config.scene.push({
      id, name: '新场景', price: 100,
      thumb: `scenes/thumb/${id}.jpg`, scenePath: `scenes/${id}.jpg`,
      sceneKey: id, sort: state.draft.config.scene.length + 1, enabled: true, minLevel: 1,
      // 新场景先给一套保守默认值：室内白天那张图的实测值。
      // 背景图画完必须重新量地面线 —— 默认值只保证画面不废，不保证好看。
      layout: {
        orthographicSize: 1.9, cameraPitch: 12, cameraDistance: 3.2,
        floorFarLine: 0.433, dogGroundLine: 0.30,
        wanderHalfX: 0.95, wanderHalfZ: 0.70, wanderCenterX: -0.30, wanderCenterZ: -0.10,
        lightEulerX: 42, lightEulerY: -35, lightEulerZ: 0,
        lightColorR: 1, lightColorG: 0.97, lightColorB: 0.90,
        ambientIntensity: 1.15, shadowOpacity: 0.32,
        layoutNote: '',
      },
    });
    markDirty(); render();
  };
};

/* ---------------- 页面：积分 + 互动 ---------------- */

RENDER.points = async (view) => {
  const d = await ensureDraft();
  const p = d.config.points || {};
  const it = d.config.interaction || {};
  const F = (name, label, hint) => `
    <div class="field">
      <label>${h(label)}</label>
      ${numCell('points.' + name, p[name])}
      ${hint ? `<span class="hint">${h(hint)}</span>` : ''}
    </div>`;

  // 新用户赠送清单。下拉只列「上架」的食物 ——
  // 送一个下架商品给新用户，背包里会出现一个商店买不到、也解释不清的条目。
  // 服务端 ValidateSnapshot 会再校验一次 id 必须存在于食物列表。
  const foods = (d.config.food || []).filter(f => f.enabled);
  const gifts = p.initialItems || [];
  const giftRows = gifts.map((g, i) => `
    <tr>
      <td>
        <select data-path="points.initialItems.${i}.itemId">
          ${foods.map(f => `<option value="${h(f.id)}"${f.id === g.itemId ? ' selected' : ''}>${h(f.name)}（${h(f.id)}）</option>`).join('')}
        </select>
      </td>
      <td class="num">${numCell(`points.initialItems.${i}.count`, g.count, 1)}</td>
      <td class="inline-actions"><button class="btn sm ghost" data-act="del-gift" data-i="${i}">删除</button></td>
    </tr>`).join('');

  view.innerHTML = `
    <div class="card">
      <div class="card-head"><h3>积分规则</h3><span class="badge primary">仅超管可改</span></div>
      <div class="card-body">
        <div class="grid-form">
          ${F('initial', '初始积分', '首次领养时发放，走流水留痕')}
          ${F('checkinReward', '签到奖励 / 天')}
          ${F('checkinDailyLimit', '签到每日上限')}
          ${F('levelupReward', '升级奖励（等级表未配时兜底）')}
          ${F('adReward', '广告奖励 / 次', '上限 500')}
          ${F('adDailyLimit', '广告每日上限')}
          ${F('personalityChangeCost', '性格修改费用')}
          ${F('personalityChangeCooldownHours', '性格修改冷却（小时）')}
          ${F('dailyProduceCap', '每日总产出上限', '0 = 不限制；用于压制通胀')}
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-head">
        <h3>新用户赠送食品（${gifts.length} 项）</h3>
        <div class="row"><button class="btn sm" id="addGift">新增一项</button></div>
      </div>
      <div class="card-body tight">
        <div class="table-wrap"><table>
          <thead><tr><th>商品</th><th>数量</th><th></th></tr></thead>
          <tbody>${giftRows || '<tr><td colspan="3" class="hint">当前不赠送。点右上角「新增一项」添加。</td></tr>'}</tbody>
        </table></div>
      </div>
    </div>

    <div class="card">
      <div class="card-head"><h3>状态阈值与衰减</h3></div>
      <div class="card-body">
        <div class="grid-form">
          ${F('satietyHungry', '饥饿阈值', '低于此值显示「饥饿」')}
          ${F('satietyExhausted', '饥饿+疲惫阈值', '须小于饥饿阈值')}
          ${F('happinessSad', '难过阈值')}
          ${F('happinessDepressed', '沮丧阈值', '须小于难过阈值')}
          ${F('satietyDecayPerHour', '饱腹 -/小时')}
          ${F('happinessDecayPerHour', '快乐 -/小时')}
          ${F('offlineCapHours', '离线衰减封顶（小时）', '超过此离线时长按此值扣')}
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-head"><h3>互动收益（PRD 4.3）</h3></div>
      <div class="card-body">
        <div class="grid-form">
          <div class="field"><label>单指点击 快乐 +</label>${numCell('interaction.tapHappiness', it.tapHappiness)}</div>
          <div class="field"><label>单指点击 每 N 次 +1 经验</label>${numCell('interaction.tapExpEvery', it.tapExpEvery, 1)}</div>
          <div class="field"><label>抚摸 快乐 +</label>${numCell('interaction.petHappiness', it.petHappiness)}</div>
          <div class="field"><label>抚摸 经验 +</label>${numCell('interaction.petExp', it.petExp)}</div>
          <div class="field"><label>多指挠动 快乐 +</label>${numCell('interaction.scratchHappiness', it.scratchHappiness)}</div>
          <div class="field"><label>多指挠动 经验 +</label>${numCell('interaction.scratchExp', it.scratchExp)}</div>
          <div class="field"><label>快速划动 快乐 +</label>${numCell('interaction.swipeHappiness', it.swipeHappiness)}</div>
          <div class="field"><label>快速划动 经验 +</label>${numCell('interaction.swipeExp', it.swipeExp)}</div>
          <div class="field"><label>背景挠动 快乐 +</label>${numCell('interaction.bgScratchHappiness', it.bgScratchHappiness)}</div>
          <div class="field"><label>背景挠动 经验 +</label>${numCell('interaction.bgScratchExp', it.bgScratchExp)}</div>
          <div class="field"><label>背景划动 快乐 +</label>${numCell('interaction.bgSwipeHappiness', it.bgSwipeHappiness)}</div>
          <div class="field"><label>背景划动 经验 +</label>${numCell('interaction.bgSwipeExp', it.bgSwipeExp)}</div>
          <div class="field"><label>每日互动经验上限</label>${numCell('interaction.dailyExpCap', it.dailyExpCap)}</div>
          <div class="field"><label>每日互动快乐上限</label>${numCell('interaction.dailyHappinessCap', it.dailyHappinessCap)}</div>
        </div>
        <p class="hint" style="margin-top:12px">互动收益由服务端按手势与次数计算并做每日上限，客户端只上报手势，不上报数值。
          「背景」指手指没落在宠物身上的挠/划 —— 收益要比狗身上的低，否则在空白处来回蹭就能顶满每日上限。</p>
      </div>
    </div>
    <p class="hint">新用户赠送在整个「首次领养」里发放：先发初始积分（走流水留痕），再发这批食品。
      赠送清单必须指向食物列表里存在的 id，否则保存草稿会被拦下。</p>`;

  bindEdits(view);

  $$('[data-act="del-gift"]', view).forEach(b => b.onclick = () => {
    state.draft.config.points.initialItems.splice(Number(b.dataset.i), 1);
    markDirty(); render();
  });
  const addGift = $('#addGift', view);
  if (addGift) addGift.onclick = () => {
    const pt = state.draft.config.points;
    if (!pt.initialItems) pt.initialItems = [];
    // 默认给第一个上架商品，数量 1。运营一般只改数量，少一次选择。
    pt.initialItems.push({ itemId: foods.length ? foods[0].id : '', count: 1 });
    markDirty(); render();
  };
};

/* ---------------- 页面：广告位 ---------------- */

RENDER.ad = async (view) => {
  const d = await ensureDraft();
  const list = d.config.ad || [];
  const rows = list.map((a, i) => `
    <tr>
      <td class="mono">${h(a.id)}</td>
      <td>${textCell(`ad.${i}.adUnitId`, a.adUnitId)}</td>
      <td>
        <select data-path="ad.${i}.adType">
          <option value="rewarded" ${a.adType === 'rewarded' ? 'selected' : ''}>rewarded 激励视频</option>
          <option value="interstitial" ${a.adType === 'interstitial' ? 'selected' : ''}>interstitial 插屏</option>
        </select>
      </td>
      <td>${textCell(`ad.${i}.placement`, a.placement)}</td>
      <td class="num">${numCell(`ad.${i}.minIntervalSec`, a.minIntervalSec)}</td>
      <td class="num">${numCell(`ad.${i}.dailyLimit`, a.dailyLimit)}</td>
      <td>${checkCell(`ad.${i}.skipFirstDay`, a.skipFirstDay)}</td>
      <td>${checkCell(`ad.${i}.enabled`, a.enabled)}</td>
    </tr>`).join('');

  view.innerHTML = `
    <div class="card">
      <div class="card-head"><h3>广告位（${list.length} 项）</h3><span class="badge primary">仅超管可改</span></div>
      <div class="card-body tight">
        <div class="table-wrap"><table>
          <thead><tr><th>ID</th><th>广告位 ID</th><th>类型</th><th>投放位置</th><th>最小间隔(秒)</th><th>每日上限</th><th>首日不弹</th><th>启用</th></tr></thead>
          <tbody>${rows || '<tr><td colspan="8" class="empty">还没有广告位</td></tr>'}</tbody>
        </table></div>
      </div>
    </div>
    <p class="hint">插屏只记录曝光、不发积分；激励视频由服务端校验每日上限与最小间隔后才发奖。</p>`;

  bindEdits(view);
};

/* ---------------- 页面：功能开关 ---------------- */

RENDER.flags = async (view) => {
  const d = await ensureDraft();
  const flags = d.config.flags || {};
  const keys = Object.keys(flags).sort();
  const rows = keys.map(k => `
    <tr>
      <td class="mono">${h(k)}</td>
      <td>
        <select data-path="flags.${h(k)}">
          <option value="true" ${flags[k] === 'true' ? 'selected' : ''}>true 开启</option>
          <option value="false" ${flags[k] !== 'true' ? 'selected' : ''}>false 关闭</option>
        </select>
      </td>
      <td>${flagNote(k)}</td>
    </tr>`).join('');

  view.innerHTML = `
    <div class="card">
      <div class="card-head">
        <h3>功能开关（${keys.length} 项）</h3>
        <button class="btn sm primary" id="addFlag">新增开关</button>
      </div>
      <div class="card-body tight">
        <div class="table-wrap"><table>
          <thead><tr><th>key</th><th>值</th><th>说明</th></tr></thead>
          <tbody>${rows || '<tr><td colspan="3" class="empty">没有开关</td></tr>'}</tbody>
        </table></div>
      </div>
    </div>`;

  bindEdits(view);
  $('#addFlag', view).onclick = () => {
    const k = prompt('开关 key（如 enable_new_shop）：', 'enable_');
    if (!k) return;
    state.draft.config.flags[k] = 'false';
    markDirty(); render();
  };
};

function flagNote(k) {
  const map = {
    enable_invite_reward: '邀请奖励。已按合规要求删除，保持 false',
    enable_share_reward: '转发奖励。微信判定为利益诱导分享，保持 false',
    enable_ads: '广告总开关',
    enable_double_pet: '双宠物（猫）。V1.0 只有狗，V1.1 打开',
    enable_voice: '语音互动。V1 不做',
  };
  return map[k] ? h(map[k]) : '<span class="muted">—</span>';
}

/* ---------------- 页面：等级 ---------------- */

// 等级表的经验值是纯配置，页面不做任何推导。
// 新建行给 0 并要求手填：发布时校验「必须大于 0」，0 会被拦下。
function expCell(path, val) {
  const bad = !val || val <= 0;
  return `<input type="number" class="${bad ? 'input-bad' : ''}" value="${h(val)}" data-path="${h(path)}"
    min="1" step="1" placeholder="请填写">`;
}

RENDER.level = async (view) => {
  const d = await ensureDraft();
  const list = d.config.level || [];
  const rows = list.map((l, i) => `
    <tr>
      <td class="mono">L${h(l.level)}</td>
      <td class="num">${expCell(`level.${i}.expRequired`, l.expRequired)}</td>
      <td class="num">${numCell(`level.${i}.rewardPoints`, l.rewardPoints)}</td>
      <td class="num">${numCell(`level.${i}.scale`, l.scale, 0, 0.01)}</td>
      <td>${checkCell(`level.${i}.enabled`, l.enabled)}</td>
      <td>${textCell(`level.${i}.note`, l.note || '')}</td>
      <td class="inline-actions"><button class="btn sm ghost" data-act="del-lv" data-i="${i}">删除</button></td>
    </tr>`).join('');

  const levelMissing = list.some(l => !l.expRequired || l.expRequired <= 0);

  view.innerHTML = `
    <div class="card">
      <div class="card-head">
        <h3>等级表（${list.length} 级）</h3>
        <div class="row">
          <button class="btn sm" id="addLv">新增一级</button>
        </div>
      </div>
      <div class="card-body tight">
        <div class="table-wrap"><table>
          <thead><tr><th>等级</th><th>升到下一级所需经验</th><th>升级奖励积分</th><th>体型缩放</th><th>启用</th><th>备注</th><th></th></tr></thead>
          <tbody>${rows}</tbody>
        </table></div>
      </div>
    </div>
    ${levelMissing ? '<div class="banner warn">有等级的经验值还是空的，保存草稿会被拦下。请把每一级「升到下一级所需经验」都填成大于 0 的整数。</div>' : ''}
    <p class="hint">这张表是唯一的数据来源，服务端不做任何公式推导，只按行扣减经验算等级。
      保存草稿与发布时都会校验：等级必须从 1 连续、经验递增且大于 0、奖励非负。新增行需要手填经验值。</p>`;

  bindEdits(view);
  $$('[data-act="del-lv"]', view).forEach(b => b.onclick = () => {
    state.draft.config.level.splice(Number(b.dataset.i), 1);
    markDirty(); render();
  });
  $('#addLv', view).onclick = () => {
    const arr = state.draft.config.level;
    const last = arr[arr.length - 1];
    arr.push({
      level: (last ? last.level : 0) + 1,
      expRequired: 0,          // 不推导，留空由运营填
      rewardPoints: 100,
      scale: 1,
      enabled: true, note: '',
    });
    markDirty(); render();
    toast('已新增一级，请填写该级所需经验');
  };
};

/* ---------------- 页面：发布与回滚 ---------------- */

RENDER.publish = async (view) => {
  const [diff, rel] = await Promise.all([
    apiQuiet('/config/diff'),
    apiQuiet('/config/releases?limit=20'),
  ]);
  if (!diff || !rel) return;
  state.releases = rel.releases || [];

  const changeRows = diff.changes.slice(0, 200).map(c => `
    <tr><td>${h(c.table)}</td><td class="mono">${h(c.itemId)}</td><td class="mono">${h(c.field)}</td>
    <td class="before">${h(c.before)}</td><td class="after">${h(c.after)}</td></tr>`).join('');

  const relRows = state.releases.map(r => `
    <tr>
      <td class="mono">v${h(r.version)}</td>
      <td>${h(r.note || '—')}</td>
      <td>${h(r.publishedBy)}</td>
      <td class="muted">${h(fmtTime(r.createdAt))}</td>
      <td class="inline-actions">
        <button class="btn sm ghost" data-act="rollback" data-v="${h(r.version)}">回滚到此版本</button>
      </td>
    </tr>`).join('');

  view.innerHTML = `
    <div class="card">
      <div class="card-head">
        <h3>待发布改动（${diff.count} 处）</h3>
        <div class="row">
          ${diff.count ? '<button class="btn sm" id="pubBtn">发布新版本</button>' : ''}
        </div>
      </div>
      <div class="card-body tight">
        ${diff.count ? `<div class="table-wrap"><table>
          <thead><tr><th>表</th><th>条目</th><th>字段</th><th>发布前</th><th>发布后</th></tr></thead>
          <tbody>${changeRows}</tbody></table></div>`
        : '<div class="empty">草稿与线上一致，没有待发布的改动。</div>'}
      </div>
    </div>

    <div class="card">
      <div class="card-head"><h3>历史版本（保留最近 10 版）</h3></div>
      <div class="card-body tight">
        <div class="table-wrap"><table>
          <thead><tr><th>版本</th><th>变更说明</th><th>发布人</th><th>时间</th><th></th></tr></thead>
          <tbody>${relRows || '<tr><td colspan="5" class="empty">还没有发布记录</td></tr>'}</tbody>
        </table></div>
      </div>
    </div>

    <p class="hint">发布后不是立刻全量生效：客户端会在下次启动（或版本号比对）时拉取新配置。</p>`;

  const pb = $('#pubBtn', view);
  if (pb) pb.onclick = doPublish;

  $$('[data-act="rollback"]', view).forEach(b => b.onclick = () => {
    const v = b.dataset.v;
    openModal('回滚到 v' + v,
      `<p>回滚会把线上配置整体换成 v${h(v)} 的内容，并生成一个新版本号（不回退计数）。</p>
       <div class="field" style="margin-top:12px"><label>回滚原因（可选）</label><textarea id="rbNote"></textarea></div>`,
      [
        { text: '取消', cls: 'ghost', fn: closeModal },
        {
          text: '确认回滚', cls: 'primary', fn: async () => {
            const note = $('#rbNote').value;
            const r = await apiQuiet('/config/rollback', { method: 'POST', body: { version: Number(v), note } });
            if (r) { toast('已回滚，当前版本 v' + r.version); closeModal(); render(); }
          }
        },
      ]);
  });
};

function fmtTime(ms) {
  if (!ms) return '';
  const d = new Date(Number(ms));
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/* ---------------- 页面：看板 ---------------- */

RENDER.dashboard = async (view) => {
  const data = await apiQuiet('/dashboard?days=7');
  if (!data) return;
  const d = data.dashboard;
  const u = d.users, ad = d.ads, pt = d.points, bh = d.behavior;

  const stat = (label, value, foot) => `
    <div class="stat"><div class="label">${h(label)}</div>
    <div class="value">${h(value)}</div>
    ${foot ? `<div class="foot">${h(foot)}</div>` : ''}</div>`;

  const maxReason = Math.max(1, ...pt.byReason.map(r => Math.abs(r.total)));
  const reasonBars = (pt.byReason || []).map(r => `
    <div class="bar-row">
      <span>${h(reasonLabel(r.reason))}</span>
      <div class="bar-track"><div class="bar-fill ${r.total < 0 ? 'accent' : ''}" style="width:${Math.min(100, Math.abs(r.total) / maxReason * 100)}%"></div></div>
      <span class="bar-val">${r.total > 0 ? '+' : ''}${h(r.total)}</span>
    </div>`).join('');

  const maxBucket = Math.max(1, ...(pt.buckets || []).map(b => b.count));
  const bucketBars = (pt.buckets || []).map(b => `
    <div class="bar-row">
      <span>${h(b.label)} 分</span>
      <div class="bar-track"><div class="bar-fill" style="width:${b.count / maxBucket * 100}%"></div></div>
      <span class="bar-val">${h(b.count)} 人</span>
    </div>`).join('');

  view.innerHTML = `
    <div class="stats">
      ${stat('DAU', u.dau, '今日有活跃记录')}
      ${stat('新增（今日）', u.newToday, `近 ${d.rangeDays} 天 ${u.newInRange}`)}
      ${stat('次日留存', (u.d1Retention * 100).toFixed(1) + '%', '注册日 D-1 的用户')}
      ${stat('7 日留存', (u.d7Retention * 100).toFixed(1) + '%')}
      ${stat('活跃宠物数', u.activePets, '活跃用户拥有的宠物')}
      ${stat('总用户', u.totalUsers)}
    </div>

    <div class="stats">
      ${stat('积分通胀率', pt.inflationRate >= 0 ? pt.inflationRate.toFixed(2) : '—', pt.inflationRate > 1.2 ? '产出明显大于消耗，需提高消耗' : '产出与消耗大致平衡')}
      ${stat('累计产出', pt.totalEarned)}
      ${stat('累计消耗', pt.totalSpent)}
      ${stat('余额总量', pt.balanceSum)}
      ${stat('广告完播率', (ad.completionRate * 100).toFixed(1) + '%', `请求 ${ad.rewardedRequests} / 完播 ${ad.rewardedCompleted}`)}
      ${stat('人均观看', ad.avgPerUser, `插屏曝光 ${ad.interstitialViews}`)}
    </div>

    <div class="card">
      <div class="card-head"><h3>积分来源占比（近 ${d.rangeDays} 天）</h3></div>
      <div class="card-body">${reasonBars || '<div class="empty">暂无流水</div>'}</div>
    </div>

    <div class="card">
      <div class="card-head"><h3>余额分布</h3></div>
      <div class="card-body">${bucketBars || '<div class="empty">暂无数据</div>'}</div>
    </div>

    <div class="stats">
      ${stat('喂食次数', bh.feedCount)}
      ${stat('购买次数', bh.buyCount)}
      ${stat('场景解锁', bh.unlockCount)}
      ${stat('宠物切换', bh.switchCount)}
      ${stat('互动次数', bh.interactCount)}
      ${stat('广告发奖失败率', (ad.rewardedFailRate * 100).toFixed(1) + '%')}
    </div>

    <div class="card">
      <div class="card-head"><h3>接口健康（进程内统计，重启清零）</h3></div>
      <div class="card-body">
        <div class="row" style="gap:24px;flex-wrap:wrap">
          <span>本次启动累计请求 <b>${h(data.httpRequestsSinceBoot)}</b></span>
          <span>接口错误率 <b>${(data.httpErrorRate * 100).toFixed(2)}%</b></span>
          <span>业务错误率 <b>${(data.httpBusinessErrorRate * 100).toFixed(2)}%</b></span>
        </div>
      </div>
    </div>`;
};

function reasonLabel(r) {
  const map = {
    initial: '初始积分', checkin: '签到', levelup: '升级', ad_reward: '广告',
    buy_food: '购买食物', unlock_scene: '解锁场景', admin_adjust: '后台调整',
    personality_change: '性格修改', invite: '邀请（已关闭）',
  };
  return map[r] || r;
}

/* ---------------- 页面：用户 ---------------- */

RENDER.users = async (view) => {
  view.innerHTML = `
    <div class="card">
      <div class="card-head">
        <h3>用户查询</h3>
        <div class="row">
          <input id="kw" placeholder="openid / 用户 id / 昵称" style="width:260px">
          <button class="btn sm primary" id="searchBtn">查询</button>
        </div>
      </div>
      <div class="card-body tight" id="userTable"><div class="empty">输入关键词后查询，留空列出最近注册</div></div>
    </div>`;

  const search = async () => {
    const kw = encodeURIComponent($('#kw', view).value.trim());
    const r = await apiQuiet('/users?keyword=' + kw + '&size=30');
    if (!r) return;
    const rows = r.list.map(u => `
      <tr>
        <td class="mono">${h(u.id)}</td>
        <td class="mono">${h(u.openid)}</td>
        <td>${h(u.nickname || '—')}</td>
        <td class="num">${h(u.balance)}</td>
        <td class="num">${h(u.petCount)}</td>
        <td class="num" title="有过登录的自然日数">${h(u.activeDays)}</td>
        <td class="num" title="累计登录次数 / 日均">${h(u.loginCount)} / ${h(u.loginPerDay)}</td>
        <td class="num" title="单次最长在线（按登录间隔估算）">${h(u.maxOnlineMinutes)} 分</td>
        <td class="num" title="日均喂养次数">${h(u.feedPerDay)}</td>
        <td class="num" title="日均花费积分">${h(u.pointsSpentPerDay)}</td>
        <td>${u.status === 'active' ? '<span class="badge ok">正常</span>' : '<span class="badge danger">封禁</span>'}</td>
        <td class="muted">${h(fmtTime(u.createdAt))}</td>
        <td class="inline-actions"><button class="btn sm" data-uid="${h(u.id)}">详情</button></td>
      </tr>`).join('');
    $('#userTable', view).innerHTML = `
      <div class="table-wrap"><table>
        <thead><tr><th>用户 id</th><th>openid</th><th>昵称</th><th>积分</th><th>宠物</th>
          <th>活跃天</th><th>登录 总/日均</th><th>最长在线</th><th>日均喂养</th><th>日均花费</th>
          <th>状态</th><th>注册时间</th><th></th></tr></thead>
        <tbody>${rows || '<tr><td colspan="13" class="empty">没有匹配的用户</td></tr>'}</tbody>
      </table></div>
      <p class="hint" style="padding:10px 14px">共 ${h(r.total)} 个用户。「日均」= 累计值 ÷ 活跃天数（有过登录的自然日数）。</p>`;
    $$('[data-uid]', view).forEach(b => b.onclick = () => openUser(b.dataset.uid));
  };

  $('#searchBtn', view).onclick = search;
  $('#kw', view).onkeydown = e => { if (e.key === 'Enter') search(); };
  search();
};

async function openUser(uid) {
  const d = await apiQuiet('/users/' + encodeURIComponent(uid));
  if (!d) return;
  const pets = (d.pets || []).map(p => `
    <tr><td>${h(p.name)}</td><td>${h(p.petType)}</td><td class="num">L${h(p.level)}</td>
    <td class="num">${h(p.exp)}</td><td class="num">${h(p.satiety)}</td><td class="num">${h(p.happiness)}</td>
    <td>${p.isActive ? '<span class="badge ok">使用中</span>' : ''}</td></tr>`).join('');

  const ledger = (d.ledger || []).slice(0, 15).map(l => `
    <tr><td class="muted">${h(fmtTime(l.createdAt))}</td><td>${h(reasonLabel(l.reason))}</td>
    <td class="num ${l.delta > 0 ? 'after' : 'before'}">${l.delta > 0 ? '+' : ''}${h(l.delta)}</td>
    <td class="num">${h(l.balanceAfter)}</td><td>${h(l.note || '')}</td></tr>`).join('');

  const inv = Object.entries(d.assets.inventory || {}).filter(([, v]) => v > 0)
    .map(([k, v]) => `<span class="badge">${h(k)} × ${h(v)}</span>`).join(' ') || '<span class="muted">空</span>';

  const st = d.stats || {};
  const feeds = (d.feedRecords || []).map(f => `
    <tr>
      <td class="muted">${h(fmtTime(f.createdAt))}</td>
      <td>${h(f.foodName || f.foodId)}</td>
      <td class="num">${h(f.foodId)}</td>
      <td class="num">${f.petName ? h(f.petName) : '<span class="muted">—</span>'}</td>
      <td class="num">${h(f.cost)}</td>
    </tr>`).join('');

  openModal('用户详情 · ' + (d.user.nickname || d.user.openid), `
    <div class="stats" style="margin-bottom:14px">
      <div class="stat"><div class="label">余额</div><div class="value">${h(d.assets.points.balance)}</div>
        <div class="foot">累计产出 ${h(d.assets.points.totalEarned)} / 消耗 ${h(d.assets.points.totalSpent)}</div></div>
      <div class="stat"><div class="label">当前场景</div><div class="value" style="font-size:16px">${h(d.assets.activeScene || '—')}</div>
        <div class="foot">已解锁 ${h((d.assets.scenes || []).length)} 个</div></div>
      <div class="stat"><div class="label">库存</div><div class="value" style="font-size:14px">${inv}</div></div>
    </div>

    <h4 style="margin:8px 0 6px">活跃与使用</h4>
    <div class="stats" style="margin-bottom:8px">
      <div class="stat"><div class="label">活跃天数</div><div class="value">${h(st.activeDays)}</div>
        <div class="foot">首次 ${h(fmtTime(st.firstActiveAt) || '—')}</div></div>
      <div class="stat"><div class="label">登录次数</div><div class="value">${h(st.loginCount)}</div>
        <div class="foot">日均 ${h(st.loginPerDay)} 次</div></div>
      <div class="stat"><div class="label">最长单次在线</div><div class="value">${h(st.maxOnlineMinutes)}<span class="unit">分</span></div>
        <div class="foot">累计 ${h(st.totalOnlineMinutes)} 分</div></div>
      <div class="stat"><div class="label">喂养次数</div><div class="value">${h(st.feedCount)}</div>
        <div class="foot">日均 ${h(st.feedPerDay)} 次</div></div>
      <div class="stat"><div class="label">花费积分</div><div class="value">${h(st.pointsSpentTotal)}</div>
        <div class="foot">日均 ${h(st.pointsSpentPerDay)} 分</div></div>
      <div class="stat"><div class="label">互动次数</div><div class="value">${h(st.interactCount)}</div>
        <div class="foot">宠物 ${h(st.petsOwned)} 只</div></div>
    </div>
    ${d.statsBasis ? `<p class="hint" style="margin-bottom:4px">${h(d.statsBasis)}</p>` : ''}
    ${d.statsCaveat ? `<p class="hint" style="color:var(--warn)">${h(d.statsCaveat)}</p>` : ''}

    <h4 style="margin:16px 0 6px">喂养记录${(d.feedRecords || []).length ? `（最近 ${h(d.feedRecords.length)} 条）` : ''}</h4>
    <div class="table-wrap"><table><thead><tr><th>喂养时间</th><th>食物名称</th><th>食物 id</th><th>宠物</th><th>花费积分</th></tr></thead>
    <tbody>${feeds || '<tr><td colspan="5" class="empty">没有喂养记录</td></tr>'}</tbody></table></div>

    <h4 style="margin:16px 0 6px">宠物</h4>
    <div class="table-wrap"><table><thead><tr><th>名字</th><th>类型</th><th>等级</th><th>累计经验</th><th>饱腹</th><th>快乐</th><th></th></tr></thead>
    <tbody>${pets || '<tr><td colspan="7" class="empty">未领养</td></tr>'}</tbody></table></div>
    <h4 style="margin:16px 0 6px">最近积分流水</h4>
    <div class="table-wrap"><table><thead><tr><th>时间</th><th>原因</th><th>变动</th><th>余额</th><th>备注</th></tr></thead>
    <tbody>${ledger || '<tr><td colspan="5" class="empty">无流水</td></tr>'}</tbody></table></div>
    <h4 style="margin:16px 0 6px">手动调整积分</h4>
    <div class="row" style="gap:8px">
      <input id="adjDelta" type="number" placeholder="正数加，负数扣" style="width:170px">
      <input id="adjNote" placeholder="必填：调整原因" style="flex:1">
      <button class="btn primary sm" id="adjBtn">提交</button>
    </div>`,
    [{ text: '关闭', cls: 'ghost', fn: closeModal }]);

  $('#adjBtn').onclick = async () => {
    const delta = Number($('#adjDelta').value);
    const note = $('#adjNote').value.trim();
    if (!delta) return toast('调整值不能为 0', true);
    if (!note) return toast('必须填写调整原因', true);
    const r = await apiQuiet('/points/adjust', { method: 'POST', body: { userId: d.user.id, delta, note } });
    if (r) { toast(`已调整 ${delta > 0 ? '+' : ''}${delta}，现余额 ${r.balance}`); openUser(uid); }
  };
}

/* ---------------- 页面：操作日志 ---------------- */

RENDER.audit = async (view) => {
  const r = await apiQuiet('/audit-logs?limit=200');
  if (!r) return;
  const rows = r.list.map(l => `
    <tr>
      <td class="muted">${h(fmtTime(l.createdAt))}</td>
      <td>${h(l.adminName)}</td>
      <td><span class="badge">${h(l.action)}</span></td>
      <td class="mono">${h(l.target)}</td>
      <td>${l.before ? h(l.before) + ' → ' + h(l.after) : '—'}</td>
      <td>${h(l.note || '')}</td>
      <td class="muted mono">${h(l.ip)}</td>
    </tr>`).join('');
  view.innerHTML = `
    <div class="card">
      <div class="card-head"><h3>操作日志（最近 ${r.list.length} 条）</h3></div>
      <div class="card-body tight">
        <div class="table-wrap"><table>
          <thead><tr><th>时间</th><th>操作人</th><th>动作</th><th>对象</th><th>变更</th><th>说明</th><th>IP</th></tr></thead>
          <tbody>${rows || '<tr><td colspan="7" class="empty">没有记录</td></tr>'}</tbody>
        </table></div>
      </div>
    </div>`;
};

/* ---------------- 页面：接口文档 ---------------- */

// 说明：规格由服务端 GET /api/admin/api-doc 下发，与路由注册同包同源，
// 页面只负责渲染 + 在线调试，不在前端硬编码一份接口清单。
state.doc = null;
state.docSel = '';

function docMethodCls(m) {
  return m === 'GET' ? 'm-get' : (m === 'PUT' ? 'm-put' : (m === 'DELETE' ? 'm-del' : 'm-post'));
}

function docFieldTable(title, fields, emptyText) {
  if (!fields || !fields.length) {
    return `<div class="doc-sec"><h4>${h(title)}</h4><p class="hint">${h(emptyText || '无')}</p></div>`;
  }
  const rows = fields.map(f => `
    <tr>
      <td class="mono">${h(f.name)}${f.required ? '<span class="req">必填</span>' : ''}</td>
      <td class="mono doc-type">${h(f.type)}</td>
      <td>${h(f.desc || '')}</td>
    </tr>`).join('');
  return `<div class="doc-sec">
    <h4>${h(title)}</h4>
    <div class="table-wrap"><table class="doc-table">
      <thead><tr><th>字段</th><th>类型</th><th>说明</th></tr></thead>
      <tbody>${rows}</tbody>
    </table></div>
  </div>`;
}

async function docSend(path, method, bodyText, token) {
  const t0 = performance.now();
  let httpStatus = 0, json = null, raw = '';
  try {
    const res = await fetch(path, {
      method: method,
      headers: Object.assign(
        { 'Content-Type': 'application/json' },
        token ? { Authorization: 'Bearer ' + token } : {}
      ),
      body: method === 'GET' || method === 'DELETE' ? undefined : (bodyText.trim() || undefined),
    });
    httpStatus = res.status;
    raw = await res.text();
    try { json = JSON.parse(raw); } catch (e) { /* 非 JSON，原样显示 */ }
  } catch (e) {
    return { httpStatus: 0, ms: Math.round(performance.now() - t0), error: String(e.message || e) };
  }
  return {
    httpStatus,
    ms: Math.round(performance.now() - t0),
    code: json ? json.code : undefined,
    msg: json ? json.msg : undefined,
    body: json ? JSON.stringify(json.data, null, 2) : raw,
  };
}

RENDER.apidoc = async (view) => {
  if (!state.doc) {
    try {
      state.doc = await api('/api-doc');
    } catch (e) {
      view.innerHTML = `<div class="banner warn">接口文档加载失败：${h(e.message)}</div>`;
      return;
    }
  }
  const doc = state.doc;
  const specs = doc.specs || [];
  if (!state.docSel) state.docSel = specs.length ? specs[0].method + ' ' + specs[0].path : '';
  const cur = specs.find(s => s.method + ' ' + s.path === state.docSel) || specs[0];

  const navItems = specs.map(s => {
    const key = s.method + ' ' + s.path;
    const active = key === (cur ? cur.method + ' ' + cur.path : '') ? ' active' : '';
    return `<a class="doc-nav-item${active}" data-key="${h(key)}">
      <span class="m ${docMethodCls(s.method)}">${h(s.method)}</span>
      <span class="doc-nav-path">${h(s.path.replace('/api/', '/'))}</span>
    </a>`;
  }).join('');

  const clientCount = specs.filter(s => s.group === '客户端').length;

  view.innerHTML = `
    <div class="doc-layout">
      <aside class="doc-side card">
        <div class="card-head"><h3>接口清单</h3><span class="badge">${specs.length} 个</span></div>
        <div class="doc-side-body">${navItems}</div>
      </aside>

      <div class="doc-main">
        ${cur ? renderDocDetail(cur) : '<div class="empty">没有接口</div>'}
      </div>
    </div>

    <div class="card">
      <div class="card-head"><h3>全局约定</h3><span class="badge">${clientCount} 个客户端接口 · ${specs.length - clientCount} 个管理端接口</span></div>
      <div class="card-body">
        <ul class="doc-conv">${(doc.conventions || []).map(c => `<li>${h(c)}</li>`).join('')}</ul>
        <h4 class="doc-h4">响应信封</h4>
        <div class="table-wrap"><table class="doc-table">
          <thead><tr><th>字段</th><th>类型</th><th>说明</th></tr></thead>
          <tbody>${(doc.envelope || []).map(f => `
            <tr><td class="mono">${h(f.name)}</td><td class="mono doc-type">${h(f.type)}</td><td>${h(f.desc || '')}</td></tr>`).join('')}</tbody>
        </table></div>
        <h4 class="doc-h4">业务错误码</h4>
        <div class="table-wrap"><table class="doc-table">
          <thead><tr><th>code</th><th>含义</th><th>说明</th></tr></thead>
          <tbody>${(doc.errorCodes || []).map(f => `
            <tr><td class="mono">${h(f.name)}</td><td>${h(f.type)}</td><td>${h(f.desc || '')}</td></tr>`).join('')}</tbody>
        </table></div>
      </div>
    </div>`;

  $$('.doc-nav-item', view).forEach(a => a.onclick = () => {
    state.docSel = a.dataset.key;
    render();
  });
  if (cur) bindDocTry(cur, view);
};

function renderDocDetail(s) {
  const authText = s.auth === '公开' ? '公开'
    : (s.auth === '用户 token' ? '需用户 token' : '需管理端 token');
  return `
  <div class="card doc-detail">
    <div class="card-head">
      <div>
        <h3><span class="m ${docMethodCls(s.method)}">${h(s.method)}</span> <span class="mono">${h(s.path)}</span> · ${h(s.title)}</h3>
        <div class="doc-tags">
          <span class="badge">${h(s.group)}</span>
          <span class="badge">${h(authText)}</span>
          ${s.roles ? `<span class="badge">${h(s.roles)}</span>` : ''}
          ${s.idem ? '<span class="badge warn">需 requestId 幂等</span>' : ''}
        </div>
      </div>
      <button class="btn sm ghost" data-act="copy-path">复制路径</button>
    </div>
    <div class="card-body">
      ${s.desc ? `<p class="doc-desc">${h(s.desc)}</p>` : ''}
      ${docFieldTable('请求字段', s.req, s.method === 'GET' ? '无请求体，参数走 query string' : '无请求体')}
      ${docFieldTable('响应字段（data 部分）', s.resp, '无（成功时 data 为空）')}
      ${s.errs && s.errs.length ? `<div class="doc-sec">
        <h4>可能返回的错误码</h4>
        <ul class="doc-errs">${s.errs.map(e => `<li>${h(e)}</li>`).join('')}</ul>
      </div>` : ''}
    </div>
  </div>

  <div class="card doc-try">
    <div class="card-head"><h3>在线调试</h3><span class="badge">${h(authText)}</span></div>
    <div class="card-body">
      <p class="hint">
        ${s.auth === '管理端 token' ? '默认使用当前登录的管理员 token。' :
          s.auth === '用户 token' ? '客户端接口要用<strong>用户</strong>的 token（不是管理员 token），请粘贴到下面。' :
          '此接口公开，不需要 token。'}
        调试请求会真的改数据，写操作请用测试账号。
      </p>
      ${s.auth === '用户 token' ? `
        <label class="doc-tok">用户 token
          <input id="docToken" placeholder="粘贴 /api/v1/login 返回的 token" autocomplete="off">
        </label>` : ''}
      <label class="doc-body">${s.method === 'GET' ? 'Query 参数' : '请求体（JSON）'}
        <textarea id="docBody" rows="5" spellcheck="false">${h(s.example || (s.method === 'GET' ? '' : '{}'))}</textarea>
      </label>
      <div class="row">
        <button class="btn primary" id="docGo">发送请求</button>
        <button class="btn ghost sm" id="docFill">恢复示例</button>
      </div>
      <div id="docOut" class="doc-out"></div>
    </div>
  </div>`;
}

function bindDocTry(s, view) {
  const out = $('#docOut', view);
  const bodyEl = $('#docBody', view);
  const tokEl = $('#docToken', view);

  const copyBtn = $('[data-act="copy-path"]', view);
  if (copyBtn) copyBtn.onclick = async () => {
    try {
      await navigator.clipboard.writeText(s.path);
      toast('路径已复制');
    } catch (e) {
      toast('复制失败，请手动选中', true);
    }
  };

  const fillBtn = $('#docFill', view);
  if (fillBtn) fillBtn.onclick = () => { bodyEl.value = s.example || ''; };

  const go = async () => {
    go.disabled = true;
    go.textContent = '发送中…';
    out.innerHTML = '<div class="doc-out-empty">等待响应…</div>';
    const token = tokEl ? tokEl.value.trim() : (s.auth === '管理端 token' ? state.token : '');
    const res = await docSend(s.path, s.method, bodyEl.value, token);
    go.disabled = false;
    go.textContent = '发送请求';

    if (res.error) {
      out.innerHTML = `<div class="doc-out-bad">请求失败：${h(res.error)}</div>`;
      return;
    }
    const okFlag = res.httpStatus < 400 && res.code === 0;
    const badge = okFlag
      ? '<span class="badge ok">成功</span>'
      : `<span class="badge danger">HTTP ${res.httpStatus} · code ${res.code === undefined ? '-' : res.code}</span>`;
    out.innerHTML = `
      <div class="doc-out-head">${badge}<span class="hint">${res.ms} ms</span></div>
      ${res.msg && res.msg !== 'ok' ? `<div class="doc-out-msg">${h(res.msg)}</div>` : ''}
      <pre class="doc-out-body">${h(res.body || '(空)')}</pre>`;
  };
  $('#docGo', view).onclick = go;
}

/* ---------------- 启动 ---------------- */

function boot() {
  const who = state.me || JSON.parse(localStorage.getItem(LS_WHO) || 'null');
  if (who) {
    state.me = who;
    $('#who').innerHTML = `<b>${h(who.username || who.name)}</b>${h(ROLE_CN[who.role] || who.role)}`;
  }
  $('#loginView').classList.add('hidden');
  $('#shell').classList.remove('hidden');
  renderNav();
  const page = (location.hash || '#food').slice(1);
  state.page = PAGES.some(p => p.id === page) ? page : 'food';
  renderNav();
  render();
}

async function init() {
  $('#loginForm').onsubmit = doLogin;
  $('#logoutBtn').onclick = () => logout(false);
  $('#modalClose').onclick = closeModal;
  $('#modal').onclick = e => { if (e.target.id === 'modal') closeModal(); };
  $('#diffBtn').onclick = showDiff;
  $('#publishBtn').onclick = doPublish;

  if (!state.token) return;

  try {
    const data = await api('/me');
    state.me = { username: data.admin.username, role: data.admin.role };
    localStorage.setItem(LS_WHO, JSON.stringify(state.me));
    boot();
  } catch (e) {
    logout(true);
  }
}

init();
