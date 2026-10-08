'use strict';

/* ═══════════════════════════════════════════════════════════════
   PhotoGrader 前端控制器
   —— 与宿主（WPF + WebView2）通过 postMessage 通信，自身不持有业务判断。
   ═══════════════════════════════════════════════════════════════ */

const bridge = window.chrome && window.chrome.webview;

const LABEL_NAME = { 1: '红', 2: '黄', 3: '绿', 4: '蓝', 5: '紫' };
const LABEL_CLASS = { 1: 'red', 2: 'yellow', 3: 'green', 4: 'blue', 5: 'purple' };
const LABEL_VAR = {
  1: 'var(--tag-red)',
  2: 'var(--tag-yellow)',
  3: 'var(--tag-green)',
  4: 'var(--tag-blue)',
  5: 'var(--tag-purple)',
};
const LABEL_KEY = { r: 1, y: 2, g: 3, b: 4, v: 5 };

const GAP = 8;
const PAGE_PADDING = 12;
const FOOT_HEIGHT = 28;

// 屏幕可能是 125% / 150% 缩放，CSS 像素不等于物理像素。
// 缩略图按物理像素请求，否则会被放大而发虚。
const DPR = Math.min(Math.max(window.devicePixelRatio || 1, 1), 3);

const state = {
  root: '',
  items: [],
  view: [],
  selection: new Set(),
  lastClicked: null,
  filter: { minStar: null, label: 0, flag: null, folder: null, search: '', quick: null },
  // 排序：key 为 default 时保持扫描顺序（不做额外排序）
  sort: { key: 'default', desc: false },
  // 视图模式：wall = 自适应流式（按原比例），grid = 紧凑对齐网格
  viewMode: 'wall',
  thumbWidth: 180,
  rows: [],
  layoutWidth: 0,
  lightboxIndex: -1,
  duplicates: [],
  md5Pending: false,
  archiveFromPos: -1,
  md5: { done: 0, total: 0 },
  // 文件夹树的展开状态：已折叠的目录集合（未列出视为展开）
  collapsedFolders: new Set(),
};

const el = {
  rootPath: document.getElementById('rootPath'),
  totalCount: document.getElementById('totalCount'),
  dupPill: document.getElementById('dupPill'),
  dupPillText: document.getElementById('dupPillText'),
  btnCopyRoot: document.getElementById('btnCopyRoot'),
  btnSwitchRoot: document.getElementById('btnSwitchRoot'),
  search: document.getElementById('search'),
  thumbSize: document.getElementById('thumbSize'),
  sizeValue: document.getElementById('sizeValue'),
  btnRefresh: document.getElementById('btnRefresh'),
  btnViewWall: document.getElementById('btnViewWall'),
  btnViewGrid: document.getElementById('btnViewGrid'),
  btnSelectAll: document.getElementById('btnSelectAll'),
  btnPickAll: document.getElementById('btnPickAll'),
  btnHotkeys: document.getElementById('btnHotkeys'),
  libraryEntry: document.getElementById('libraryEntry'),
  foldersBadge: document.getElementById('foldersBadge'),
  quickBadge: document.getElementById('quickBadge'),
  flagsBadge: document.getElementById('flagsBadge'),
  ratingsBadge: document.getElementById('ratingsBadge'),
  labelsBadge: document.getElementById('labelsBadge'),
  quickViews: document.getElementById('quickViews'),
  filterStars: document.getElementById('filterStars'),
  filterLabels: document.getElementById('filterLabels'),
  filterFlags: document.getElementById('filterFlags'),
  filterFolders: document.getElementById('filterFolders'),
  sidebar: document.getElementById('sidebar'),
  sortSelect: document.getElementById('sortSelect'),
  btnSortDir: document.getElementById('btnSortDir'),
  btnClearFilter: document.getElementById('btnClearFilter'),
  wall: document.getElementById('wall'),
  stage: document.getElementById('stage'),
  statusText: document.getElementById('statusText'),
  readyDot: document.getElementById('readyDot'),
  md5Fill: document.getElementById('md5Fill'),
  md5Text: document.getElementById('md5Text'),
  visibleInfo: document.getElementById('visibleInfo'),
  selectionInfo: document.getElementById('selectionInfo'),
  sizeInfo: document.getElementById('sizeInfo'),
  hud: document.getElementById('hud'),
  hudInfo: document.getElementById('hudInfo'),
  emptyHint: document.getElementById('emptyHint'),
  lightbox: document.getElementById('lightbox'),
  lbImage: document.getElementById('lbImage'),
  lbStage: document.getElementById('lbStage'),
  lbStars: document.getElementById('lbStars'),
  lbFlags: document.getElementById('lbFlags'),
  lbLabels: document.getElementById('lbLabels'),
  lbDup: document.getElementById('lbDup'),
  lbName: document.getElementById('lbName'),
  lbPos: document.getElementById('lbPos'),
  lbClose: document.getElementById('lbClose'),
  lbPrev: document.getElementById('lbPrev'),
  lbNext: document.getElementById('lbNext'),
  hotkeySheet: document.getElementById('hotkeySheet'),
  hotkeyClose: document.getElementById('hotkeyClose'),
};

function send(payload) {
  if (bridge) bridge.postMessage(payload);
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"]/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}

function svg(use, size, extraClass) {
  const cls = extraClass ? ` class="${extraClass}"` : '';
  return `<svg width="${size}" height="${size}"${cls}><use href="#${use}"/></svg>`;
}

function formatSize(bytes) {
  if (!bytes) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit += 1; }
  return `${value < 10 && unit > 0 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

/* ---------- 星级渲染（SVG） ---------- */

// size 为图标像素尺寸；返回 5 颗星，已评的实心金黄、未评的描边灰
function starHtml(rating, size) {
  const s = size || 13;
  const parts = [];
  for (let n = 1; n <= 5; n++) {
    const on = n <= rating ? ' on' : '';
    const icon = n <= rating ? 'i-star' : 'i-star-outline';
    parts.push(`<span class="starText${on}" data-star="${n}">${svg(icon, s)}</span>`);
  }
  return parts.join('');
}

/* ═══════════════ 布局：按原始比例排列 ═══════════════ */

function aspectOf(item) {
  return item.w > 0 && item.h > 0 ? item.w / item.h : 16 / 9;
}

function targetRowHeight() {
  return Math.round(state.thumbWidth * 0.62);
}

// 紧凑网格：所有卡片等宽等高（取平均宽高比），行数更整齐
function computeGridLayout(available) {
  const target = Math.max(96, Math.round(state.thumbWidth * 0.72));
  const columns = Math.max(1, Math.floor((available + GAP) / (target + GAP)));
  const cellWidth = (available - GAP * (columns - 1)) / columns;
  const cellHeight = Math.round(cellWidth * 0.72);

  const rows = [];
  for (let i = 0; i < state.view.length; i += columns) {
    const slice = state.view.slice(i, i + columns).map((index) => ({
      index,
      aspect: aspectOf(state.items[index]),
      fixedWidth: cellWidth,
    }));
    rows.push({ height: cellHeight, constrained: true, items: slice, columns });
  }

  let y = 0;
  for (const row of rows) {
    row.y = y;
    y += row.height + FOOT_HEIGHT + GAP;
  }

  state.rows = rows;
  state.layoutWidth = available;
  el.stage.style.width = available + 'px';
  el.stage.style.height = Math.max(0, y - GAP) + 'px';
}

function computeLayout() {
  const available = Math.max(120, el.wall.clientWidth - PAGE_PADDING * 2);

  if (state.viewMode === 'grid') {
    computeGridLayout(available);
    return;
  }

  const wanted = targetRowHeight();

  const rows = [];
  let current = [];
  let aspectSum = 0;

  const flush = (isLast) => {
    if (current.length === 0) return;

    const gaps = GAP * (current.length - 1);
    const natural = (available - gaps) / aspectSum;

    // 最后一行若不足以填满整宽，就按目标高度排布。
    // 关键：此时绝不能再把宽度拉伸到满行，否则卡片宽高比与原图不符，
    // background-size:cover 会裁掉上下边缘。
    const constrained = isLast && natural > wanted;

    rows.push({
      height: constrained ? wanted : natural,
      constrained,
      items: current,
    });

    current = [];
    aspectSum = 0;
  };

  for (const index of state.view) {
    const aspect = aspectOf(state.items[index]);
    current.push({ index, aspect });
    aspectSum += aspect;

    const gaps = GAP * (current.length - 1);
    if ((available - gaps) / aspectSum <= wanted) flush(false);
  }

  flush(true);

  // 一行实际占用的高度 = 缩略图高 + 底部信息栏高，行距必须把信息栏算进去。
  // 只加 GAP 会让下一行压住上一行 FOOT_HEIGHT - GAP 像素，
  // 表现就是「上一行的 foot 看不见」。
  let y = 0;
  for (const row of rows) {
    row.y = y;
    y += row.height + FOOT_HEIGHT + GAP;
  }

  state.rows = rows;
  state.layoutWidth = available;
  el.stage.style.width = available + 'px';
  el.stage.style.height = Math.max(0, y - GAP) + 'px';
}

function renderVisible() {
  const scrollTop = el.wall.scrollTop;
  const viewport = el.wall.clientHeight;
  const margin = 400;
  const parts = [];

  for (const row of state.rows) {
    if (row.y + row.height + FOOT_HEIGHT < scrollTop - margin) continue;
    if (row.y > scrollTop + viewport + margin) break;

    const gaps = GAP * (row.items.length - 1);
    const usable = state.layoutWidth - gaps;
    const aspectSum = row.items.reduce((sum, entry) => sum + entry.aspect, 0);

    let x = 0;
    for (const entry of row.items) {
      // 受限行按原比例定宽；其余行把宽度拉伸填满整行（两者此时等价）
      let width;
      if (entry.fixedWidth) {
        width = entry.fixedWidth;
      } else if (row.constrained) {
        width = entry.aspect * row.height;
      } else {
        width = (entry.aspect / aspectSum) * usable;
      }

      parts.push(cardHtml(state.items[entry.index], entry.index, x, row.y, width, row.height));
      x += width + GAP;
    }
  }

  el.stage.innerHTML = parts.join('');
  el.emptyHint.hidden = state.view.length !== 0;
}

function cardHtml(item, index, x, y, width, thumbHeight) {
  const selected = state.selection.has(index) ? ' sel' : '';
  const rejected = item.f === -1 ? ' rejected' : '';
  const totalHeight = thumbHeight + FOOT_HEIGHT;

  // 左上角旗标徽章：红＝留用（PICK），灰＝排除（叉号）
  let flagHtml = '';
  if (item.f === 1) {
    flagHtml = `<span class="flagBadge picked">${svg('i-flag', 11)}PICK</span>`;
  } else if (item.f === -1) {
    flagHtml = `<span class="flagBadge rejected">${svg('i-ban', 11)}</span>`;
  }

  // 左下角重复徽章
  const dupHtml = item.dc > 1
    ? `<span class="dupBadge" title="有 ${item.dc} 个内容完全相同的文件">`
      + `${svg('i-duplicate', 10)}×${item.dc} 重复</span>`
    : '';

  // 右上角复选框
  const checkHtml = `<span class="cardCheck">${svg('i-check', 11)}</span>`;

  // 底部：5 星 + 彩色标签（hover 展开）
  // 遵循 Lightroom 惯例：只有 5 个颜色，点选中、再点同一个取消。
  // 不额外提供「清除」按钮 —— 那会在卡片右下角留下一个多余的空心圆。
  let labelsHtml = '<span class="footLabels">';
  for (let code = 1; code <= 5; code++) {
    const isCur = item.l === code;
    labelsHtml += `<button type="button" class="labelPick ${LABEL_CLASS[code]}`
      + `${isCur ? ' on cur' : ''}" data-label="${code}" title="${LABEL_NAME[code]}标签"></button>`;
  }
  labelsHtml += '</span>';

  // 按相对路径 + 文件大小寻址：index 会随文件列表增删偏移，
  // 而 URL 带长缓存，按 index 请求会让浏览器返回错位的旧缩略图。
  // size 作为指纹：文件内容被覆盖（大小变化）时 URL 随之失效。
  const requestWidth = Math.min(1024, Math.ceil(width * DPR));
  const thumbUrl = '/thumb/' + encodeURIComponent(item.p) + '?w=' + requestWidth + '&s=' + item.s;

  return `<div class="card${selected}${rejected}" draggable="true" data-index="${index}" `
    + `style="left:${x}px;top:${y}px;width:${width}px;height:${totalHeight}px">`
    + `<div class="thumb" style="background-image:url('${thumbUrl}')">`
    + flagHtml + dupHtml + checkHtml
    + `</div>`
    + `<div class="foot"><span class="stars">${starHtml(item.r, 13)}</span>`
    + labelsHtml
    + `</div>`
    + `</div>`;
}

/* ═══════════════ 筛选 ═══════════════ */

// 快速视图的四个预设（重复组在 applyFilter 里单独处理）
function matchesQuick(item, quick) {
  switch (quick) {
    case 'picks': return item.f === 1;
    case 'rejected': return item.f === -1;
    case 'unculled': return item.f === 0 && item.r === 0;
    case 'duplicates': return item.dc > 1;
    default: return true;
  }
}

function applyFilter() {
  const f = state.filter;
  const needle = f.search.trim().toLowerCase();
  const view = [];

  for (let i = 0; i < state.items.length; i++) {
    const item = state.items[i];

    if (item.archived || item.deleted) continue;
    if (f.quick && !matchesQuick(item, f.quick)) continue;
    if (f.minStar !== null && item.r !== f.minStar) continue;
    if (f.label && item.l !== f.label) continue;
    if (f.flag !== null && item.f !== f.flag) continue;
    if (f.folder !== null && !isUnderFolder(item.d, f.folder)) continue;
    if (needle && item.n.toLowerCase().indexOf(needle) === -1) continue;

    view.push(i);
  }

  state.view = sortView(view);
  state.selection.clear();

  computeLayout();
  renderVisible();
  renderSidebar();
  updateStatus();
}

/// 排序：default 保持扫描顺序；其余按所选字段，desc 决定方向。
function sortView(view) {
  const { key, desc } = state.sort;
  if (key === 'default') return view;

  const dir = desc ? -1 : 1;

  return [...view].sort((a, b) => {
    const x = state.items[a];
    const y = state.items[b];

    let cmp;
    if (key === 'name') {
      cmp = x.n.localeCompare(y.n, 'zh-Hans-CN');
    } else if (key === 'time') {
      cmp = x.t - y.t;
    } else {
      cmp = x.s - y.s;
    }

    // 数值/时间相同时用文件名兜底，保证顺序稳定可预期
    if (cmp === 0) cmp = x.n.localeCompare(y.n, 'zh-Hans-CN');
    return cmp * dir;
  });
}

function clearFilter() {
  state.filter = { minStar: null, label: 0, flag: null, folder: null, search: '', quick: null };
  el.search.value = '';
  applyFilter();
}

/* ═══════════════ 侧栏 ═══════════════ */

// 目录归属判断：选中某目录时，其所有子孙目录里的图片一并纳入。
// 目录名是相对图库根的完整路径（/ 分隔）；根目录用 '.' 编码 ——
// 不能用空串，否则会和「全部文件夹」的取值撞车。
function isUnderFolder(dir, folder) {
  if (folder === '.') return dir === '';
  return dir === folder || dir.startsWith(folder + '/');
}

function folderStats() {
  const map = new Map();
  for (const item of state.items) {
    if (item.archived || item.deleted) continue;
    const key = item.d === '' ? '.' : item.d;
    map.set(key, (map.get(key) || 0) + 1);
  }
  return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0], 'zh-Hans-CN'));
}

/* 把扁平的目录名列表（如 "01-角色设定" / "images/01-xxx"）整理成父子关系。
   返回 Map<parentNameOrNull, childName[]>，键 null 表示顶层。

   关键：即使中间层目录自身没有直接图片（如 images/ 只作为容器），
   也要把它补进树里，否则 "images/a" 和 "images/b" 会各自变成根节点，
   真实目录结构就看不出来了。 */
function buildFolderTree(stats) {
  const names = new Set(stats.map(([name]) => name));
  const childOf = new Map();

  const push = (parent, child) => {
    if (!childOf.has(parent)) childOf.set(parent, []);
    if (!childOf.get(parent).includes(child)) childOf.get(parent).push(child);
  };

  for (const [name] of stats) {
    if (name === '.') continue;

    const parts = name.split('/');

    // 逐级补全所有祖先路径，保证层级完整
    let parent = null;
    for (let i = 1; i < parts.length; i++) {
      const segment = parts.slice(0, i).join('/');
      push(parent, segment);
      parent = segment;
    }
    // 末级（自身）挂到倒数第二级下
    push(parent, name);
  }

  // 根目录 "." 总是排在最前
  if (names.has('.')) {
    childOf.set(null, ['.', ...(childOf.get(null) || [])]);
  }

  const byName = (a, b) => a.localeCompare(b, 'zh-Hans-CN');
  for (const kids of childOf.values()) kids.sort(byName);

  return childOf;
}

function renderSidebar() {
  const f = state.filter;

  // 各 facet 的计数基于「当前目录范围内」的条目，而不是全库；
  // 已归档 / 已删除的条目一并排除
  const alive = state.items.filter((item) => !item.archived && !item.deleted);
  const scoped = alive.filter((item) =>
    f.folder === null || isUnderFolder(item.d, f.folder));

  /* ---------- ① 图库与目录：全部相片总入口 ---------- */
  el.libraryEntry.innerHTML =
    `<div class="row libEntry${f.quick === null && f.folder === null ? ' on' : ''}" `
    + `data-kind="all">`
    + `${svg('i-library', 14, 'ri')}`
    + `<span class="rName">全部相片</span>`
    + `<span class="sNum">${alive.length}</span></div>`;

  /* ---------- ② 快速视图 ---------- */
  const quickDefs = [
    { key: 'picks', label: '留用相片 (Pick)', icon: 'i-flag', cls: 'pick', count: scoped.filter((i) => i.f === 1).length },
    { key: 'rejected', label: '排除相片 (Reject)', icon: 'i-ban', cls: 'reject', count: scoped.filter((i) => i.f === -1).length },
    { key: 'unculled', label: '未审阅 (Unculled)', icon: 'i-pending', count: scoped.filter((i) => i.f === 0 && i.r === 0).length },
    { key: 'duplicates', label: '重复组 (Duplicates)', icon: 'i-duplicate', count: scoped.filter((i) => i.dc > 1).length, hot: true },
  ];

  el.quickViews.innerHTML = quickDefs.map((def) => {
    const on = f.quick === def.key ? ' on' : '';
    const cls = def.cls ? ` ${def.cls}` : '';
    const num = def.hot && def.count > 0 ? ' class="sNum hot"' : ' class="sNum"';
    return `<div class="row${on}" data-kind="quick" data-value="${def.key}">`
      + `${svg(def.icon, 14, 'ri' + cls)}`
      + `<span class="rName">${def.label}</span>`
      + `<span${num}>${def.count}</span></div>`;
  }).join('');

  /* ---------- 星级（精确匹配，含未评分档） ---------- */
  const starCounts = [0, 0, 0, 0, 0, 0];
  for (const item of scoped) starCounts[item.r] += 1;

  const starStrip = (n) => {
    let html = '<span class="starStrip">';
    for (let i = 1; i <= 5; i++) {
      html += i <= n
        ? svg('i-star', 11)
        : svg('i-star-outline', 11, 'empty');
    }
    return html + '</span>';
  };

  const starRow = (value, labelHtml, count) => {
    const on = f.minStar === value ? ' on' : '';
    return `<div class="row${on}" data-kind="star" data-value="${value}">`
      + `${labelHtml}<span class="sNum">${count}</span></div>`;
  };

  el.filterStars.innerHTML =
    starRow(0, `${svg('i-star-outline', 13, 'ri')}<span class="rName">未评分</span>`, starCounts[0])
    + [5, 4, 3, 2, 1]
      .map((n) => starRow(n, starStrip(n), starCounts[n]))
      .join('');

  /* ---------- ⑤ 色彩标签：五色并排 ---------- */
  const labelCounts = {};
  for (const item of scoped) labelCounts[item.l] = (labelCounts[item.l] || 0) + 1;

  let labelHtml = '<div class="labelChips">';
  for (let code = 1; code <= 5; code++) {
    const on = f.label === code ? ' on' : '';
    labelHtml += `<div class="labelChip${on}" data-kind="label" data-value="${code}" `
      + `title="${LABEL_NAME[code]} · ${labelCounts[code] || 0} 张">`
      + `<span class="tagDot ${LABEL_CLASS[code]}"></span>`
      + `<span class="lcNum">${labelCounts[code] || 0}</span></div>`;
  }
  labelHtml += '</div>';

  // 无标签 / 全部标签 的复位行
  labelHtml += `<div class="row labelNone${f.label === 0 ? ' on' : ''}" data-kind="label" data-value="0">`
    + `<span class="tagDot none"></span><span class="rName">不限标签</span>`
    + `<span class="sNum">${scoped.length}</span></div>`;

  el.filterLabels.innerHTML = labelHtml;

  /* ---------- 旗标 ---------- */
  const flagCounts = { '-1': 0, 0: 0, 1: 0 };
  for (const item of scoped) flagCounts[String(item.f)] += 1;

  const flagRow = (icon, cls, text, value, count) => {
    const on = f.flag === value ? ' on' : '';
    return `<div class="row${on}" data-kind="flag" data-value="${value}">`
      + `${svg(icon, 14, 'ri' + (cls ? ' ' + cls : ''))}`
      + `<span class="rName">${text}</span>`
      + `<span class="sNum">${count}</span></div>`;
  };

  el.filterFlags.innerHTML =
    flagRow('i-flag', 'pick', '留用', 1, flagCounts['1'])
    + flagRow('i-ban', 'reject', '排除', -1, flagCounts['-1'])
    + flagRow('i-flag-outline', '', '未标记', 0, flagCounts['0']);

  /* ---------- ① 文件夹目录树（阶梯缩进 + 展开折叠） ---------- */
  const stats = folderStats();                       // [name, count][]  已按名称排序
  const childOf = buildFolderTree(stats);            // parent -> children

  // 某目录自身计数 + 其所有子孙计数
  const selfCount = new Map(stats);
  const subtreeCount = new Map();
  const countSubtree = (name) => {
    if (subtreeCount.has(name)) return subtreeCount.get(name);
    let total = selfCount.get(name) || 0;
    for (const child of childOf.get(name) || []) total += countSubtree(child);
    subtreeCount.set(name, total);
    return total;
  };

  let folderHtml = '';

  const emit = (name, depth, isLast) => {
    const on = f.folder === name ? ' on' : '';
    const kids = childOf.get(name) || [];
    const hasKids = kids.length > 0;
    const collapsed = state.collapsedFolders.has(name);
    const count = hasKids ? countSubtree(name) : (selfCount.get(name) || 0);

    // 有子目录：展开态显示向下箭头，折叠态显示向右箭头；叶子节点留空占位
    const twistIcon = hasKids ? (collapsed ? 'i-chevron-right' : 'i-chevron-down') : null;
    const twistCls = hasKids ? 'twist' : 'twist leaf';
    const twist = `<span class="${twistCls}"${hasKids ? ` data-twist="${escapeHtml(name)}"` : ''}>`
      + (twistIcon ? svg(twistIcon, 9) : '') + '</span>';

    // 标签：只显示末级目录名（保持完整、不被省略号截断）。
    // 层级完全由缩进 + 引导虚线表达；完整相对路径放进 title 悬浮提示。
    let label;
    if (name === '.') {
      label = '<span class="segLeaf">根目录</span>'
        + '<span class="segMeta">（整个图库）</span>';
    } else {
      const leaf = name.split('/').pop();
      label = `<span class="segLeaf">${escapeHtml(leaf)}</span>`;
    }

    // 缩进 20px/级 —— 让每级层级关系一眼可辨
    const indent = 5 + depth * 20;

    // 层级引导虚线：每级祖先一条，与缩进步长严格对齐（20px）
    // 第 d 级虚线的位置 = 该级箭头所在列的左沿，保证折线连贯不断开
    let guides = '';
    for (let d = 0; d < depth; d++) {
      guides += `<span class="guide" style="left:${10 + d * 20}px"></span>`;
    }

    folderHtml += `<div class="row folderRow${on}" data-kind="folder" data-depth="${depth}" `
      + `data-value="${escapeHtml(name)}" style="padding-left:${indent}px">`
      + guides + twist
      + svg(hasKids ? 'i-folder-open' : (name === '.' ? 'i-library' : 'i-folder'), 13, 'ri')
      + `<span class="rName" title="${escapeHtml(name === '.' ? '(根目录)' : name)}">${label}</span>`
      + `<span class="sNum">${count}</span>`
      + '</div>';

    if (!collapsed) {
      kids.forEach((child, i) => emit(child, depth + 1, i === kids.length - 1));
    }
  };

  const roots = childOf.get(null) || [];
  roots.forEach((name, i) => emit(name, 0, i === roots.length - 1));

  if (folderHtml === '') {
    folderHtml = '<div class="treeEmpty">暂无子目录</div>';
  }

  el.filterFolders.innerHTML = folderHtml;

  /* ---------- 顶栏统计胶囊 ---------- */
  el.totalCount.textContent = `${alive.length} ITEMS`;

  /* ---------- 各分组「选中态」高亮 ---------- */
  /* ---------- 各分组的「筛选生效」指示 ----------
     徽章只在对应分组确实参与筛选时才出现，内容是该分组的「已选条件数」。
     平时不显示任何数字 —— 之前把「全部相片数 / 目录数」挂在这里是错的，
     那些数字和分组标题的语义对不上，容易被误读。 */
  const activeMap = {
    folders: { on: f.folder !== null, badge: el.foldersBadge, n: f.folder !== null ? 1 : 0 },
    quick: { on: f.quick !== null, badge: el.quickBadge, n: f.quick !== null ? 1 : 0 },
    flags: { on: f.flag !== null, badge: el.flagsBadge, n: f.flag !== null ? 1 : 0 },
    ratings: { on: f.minStar !== null, badge: el.ratingsBadge, n: f.minStar !== null ? 1 : 0 },
    labels: { on: f.label !== 0, badge: el.labelsBadge, n: f.label !== 0 ? 1 : 0 },
  };

  for (const [key, info] of Object.entries(activeMap)) {
    const node = el.sidebar.querySelector(`.mod[data-mod="${key}"]`);
    if (node) node.classList.toggle('hasActive', info.on);

    if (info.badge) {
      info.badge.hidden = !info.on;
      info.badge.textContent = String(info.n);
    }
  }

  const dupSets = scoped.filter((i, idx) => i.dc > 1).length;
  if (dupSets > 0) {
    // 重复「组数」= 有重复的文件数 / 每组的份数，粗略按 dc 去重组数
    const groups = new Set();
    for (const item of alive) {
      if (item.dc > 1) groups.add(item.m || item.p);
    }
    el.dupPill.hidden = false;
    el.dupPillText.textContent = `${groups.size} DUP SETS`;
  } else {
    el.dupPill.hidden = true;
  }
}

function updateStatus() {
  el.visibleInfo.textContent = String(state.view.length);
  el.selectionInfo.textContent = String(state.selection.size);

  const selectedBytes = [...state.selection]
    .reduce((sum, index) => sum + (state.items[index]?.s || 0), 0);
  el.sizeInfo.textContent = state.selection.size > 0 ? formatSize(selectedBytes) : '—';

  el.hudInfo.textContent = state.selection.size > 0
    ? `${state.selection.size} 项选中${selectedBytes ? ` (${formatSize(selectedBytes)})` : ''}`
    : `共 ${state.view.length} 张`;

  el.thumbSize.value = String(state.thumbWidth);
  el.sizeValue.textContent = String(state.thumbWidth);

  // 排序控件同步
  el.sortSelect.value = state.sort.key;
  const noDir = state.sort.key === 'default';
  el.btnSortDir.textContent = noDir ? '—' : (state.sort.desc ? '↓' : '↑');
  el.btnSortDir.disabled = noDir;
  el.btnSortDir.style.opacity = noDir ? '0.35' : '1';
}

function setStatus(text, kind) {
  el.statusText.textContent = text;
  el.readyDot.className = 'readyDot' + (kind ? ' ' + kind : ' ok');
}

/* ═══════════════ 灯箱 ═══════════════ */

function openLightbox(index) {
  if (!state.items[index]) return;

  state.lightboxIndex = index;
  state.lastClicked = index;

  if (!state.selection.has(index)) {
    state.selection.clear();
    state.selection.add(index);
  }

  applySelectionStyles();
  updateStatus();

  el.lightbox.hidden = false;
  updateLightbox();
  requestMd5(index);
}

/* ---------- MD5 与重复检测 ---------- */

function requestMd5(index) {
  const item = state.items[index];
  if (!item) return;

  state.duplicates = [];
  state.md5Pending = !item.m;

  renderMd5();
  send({ cmd: 'needMd5', index });
}

function renderMd5() {
  const item = state.items[state.lightboxIndex];
  if (!item) return;

  if (state.md5Pending) {
    el.lbDup.hidden = false;
    el.lbDup.className = 'lbDup';
    el.lbDup.innerHTML = '<span>正在计算 MD5 …</span>';
    return;
  }

  if (!item.m) {
    el.lbDup.hidden = false;
    el.lbDup.className = 'lbDup';
    el.lbDup.innerHTML = '<span>MD5 计算失败（文件可能被占用或已移动）</span>';
    return;
  }

  const hash = `<span class="md5Text">MD5 ${item.m.slice(0, 12)}…</span>`;

  if (state.duplicates.length === 0) {
    el.lbDup.hidden = true;
    return;
  }

  el.lbDup.hidden = false;
  el.lbDup.className = 'lbDup warn';
  el.lbDup.innerHTML = hash
    + `<b>发现 ${state.duplicates.length} 个内容完全相同的文件</b>`
    + state.duplicates
      .map((dup) => `<span class="dupItem" data-index="${dup.i}" `
        + `title="${escapeHtml(dup.p)}">${escapeHtml(dup.d)}／${escapeHtml(dup.n)}</span>`)
      .join('')
    + `<span class="lbGroup dupActions">`
    + `<button type="button" class="lbBtn" id="lbArchiveSelf">移除本张</button>`
    + `<button type="button" class="lbBtn" id="lbArchiveDup">`
    + `移除重复 <em>${state.duplicates.length}</em></button>`
    + `</span>`;
}

function closeLightbox() {
  state.lightboxIndex = -1;
  el.lightbox.hidden = true;
}

function stepLightbox(delta) {
  const position = state.view.indexOf(state.lightboxIndex);
  if (position < 0) return;

  const next = position + delta;
  if (next < 0 || next >= state.view.length) return;

  state.lightboxIndex = state.view[next];
  state.selection.clear();
  state.selection.add(state.lightboxIndex);

  applySelectionStyles();
  updateLightbox();
  updateStatus();
  requestMd5(state.lightboxIndex);

  el.lbPrev.disabled = next <= 0;
  el.lbNext.disabled = next >= state.view.length - 1;
}

function updateLightbox() {
  const index = state.lightboxIndex;
  const item = state.items[index];
  if (!item) return;

  // 按相对路径 + 指纹取原图，避免 index 偏移后打开错图
  const fullUrl = '/full/' + encodeURIComponent(item.p) + '?s=' + item.s;
  if (el.lbImage.dataset.url !== fullUrl) {
    el.lbImage.src = fullUrl;
    el.lbImage.dataset.url = fullUrl;
  }

  const position = state.view.indexOf(index);
  el.lbPos.textContent = `${position + 1} / ${state.view.length}`;
  el.lbName.textContent = `${item.n}${item.w && item.h ? `  ${item.w}×${item.h}` : ''}`;
  el.lbName.title = `${item.d ? item.d + '／' : ''}${item.n}`;

  el.lbPrev.disabled = position <= 0;
  el.lbNext.disabled = position >= state.view.length - 1;

  el.lbStars.innerHTML = starHtml(item.r, 19);

  el.lbFlags.querySelectorAll('.lbBtn').forEach((btn) => {
    btn.classList.toggle('on', Number(btn.dataset.flag) === item.f);
  });

  el.lbLabels.innerHTML = [1, 2, 3, 4, 5]
    .map((code) => `<button type="button" class="lbDot ${LABEL_CLASS[code]}`
      + `${item.l === code ? ' on' : ''}" data-label="${code}" title="${LABEL_NAME[code]}"></button>`)
    .join('')
    + `<button type="button" class="lbDot none${item.l === 0 ? ' on' : ''}" `
    + `data-label="0" title="无标签"></button>`;
}

el.lbClose.addEventListener('click', closeLightbox);
el.lbPrev.addEventListener('click', () => stepLightbox(-1));
el.lbNext.addEventListener('click', () => stepLightbox(1));
el.lbStage.addEventListener('click', closeLightbox);

// 放大视图里滚轮切换上/下一张（带节流，防止惯性滚轮连翻好几张）
el.lightbox.addEventListener('wheel', (event) => {
  if (el.lightbox.hidden || state.lightboxIndex < 0) return;

  event.preventDefault();

  if (event.deltaY === 0) return;
  if (Date.now() - (state.lastWheelNav || 0) < 220) return;
  state.lastWheelNav = Date.now();

  stepLightbox(event.deltaY > 0 ? 1 : -1);
}, { passive: false });

el.lightbox.addEventListener('click', (event) => {
  const index = state.lightboxIndex;
  const item = state.items[index];
  if (!item) return;

  const star = event.target.closest('.starText');
  if (star) {
    const value = Number(star.dataset.star);
    send({ cmd: 'setGrade', index, rating: item.r === value ? 0 : value });
    return;
  }

  const flagBtn = event.target.closest('button[data-flag]');
  if (flagBtn) {
    const value = Number(flagBtn.dataset.flag);
    send({ cmd: 'setGrade', index, flag: item.f === value ? 0 : value });
    return;
  }

  const labelBtn = event.target.closest('button[data-label]');
  if (labelBtn) {
    const value = Number(labelBtn.dataset.label);
    send({ cmd: 'setGrade', index, label: item.l === value ? 0 : value });
    return;
  }

  if (event.target.closest('#lbArchiveSelf')) {
    // 记住当前在视图中的位置，归档后据此跳到「下一张」
    state.archiveFromPos = state.view.indexOf(state.lightboxIndex);
    send({ cmd: 'archive', index: state.lightboxIndex, scope: 'self' });
    return;
  }

  if (event.target.closest('#lbArchiveDup')) {
    state.archiveFromPos = state.view.indexOf(state.lightboxIndex);
    send({ cmd: 'archive', index: state.lightboxIndex, scope: 'duplicates' });
    return;
  }

  const dup = event.target.closest('.dupItem');
  if (dup) {
    openLightbox(Number(dup.dataset.index));
  }
});

/* ═══════════════ 图库交互 ═══════════════ */

el.wall.addEventListener('scroll', () => {
  closeCtxMenu();   // 菜单是 fixed 定位，滚动后不再对应原卡片
  if (state._frame) return;
  state._frame = requestAnimationFrame(() => {
    state._frame = 0;
    renderVisible();
  });
});

el.wall.addEventListener('dblclick', (event) => {
  const card = event.target.closest('.card');
  if (!card) return;
  openLightbox(Number(card.dataset.index));
});

// 右键卡片 → 上下文菜单
el.wall.addEventListener('contextmenu', (event) => {
  const card = event.target.closest('.card');
  if (!card) return;

  event.preventDefault();
  openCtxMenu(Number(card.dataset.index), event.clientX, event.clientY);
});

// 点击菜单之外的地方时收起菜单（菜单自身的点击由其专属处理器处理）
el.wall.addEventListener('click', (event) => {
  closeCtxMenu();

  const card = event.target.closest('.card');
  if (!card) return;

  const index = Number(card.dataset.index);
  const star = event.target.closest('.starText');

  if (star) {
    const value = Number(star.dataset.star);
    const current = state.items[index].r;
    send({ cmd: 'setGrade', index, rating: current === value ? 0 : value });
    return;
  }

  // 彩色标签：直接点圆点即设置（再点同一个取消）
  const tag = event.target.closest('.labelPick');
  if (tag) {
    const value = Number(tag.dataset.label);
    const current = state.items[index].l;
    send({ cmd: 'setGrade', index, label: current === value ? 0 : value });
    return;
  }

  // 右上角复选框：点击即切换该张的选中状态（不影响其他已选项）
  const check = event.target.closest('.cardCheck');
  if (check) {
    if (state.selection.has(index)) state.selection.delete(index);
    else state.selection.add(index);
    state.lastClicked = index;
    applySelectionStyles();
    updateStatus();
    return;
  }

  if (event.ctrlKey || event.metaKey) {
    if (state.selection.has(index)) state.selection.delete(index);
    else state.selection.add(index);
  } else if (event.shiftKey && state.lastClicked !== null) {
    const from = state.view.indexOf(state.lastClicked);
    const to = state.view.indexOf(index);
    if (from >= 0 && to >= 0) {
      const [lo, hi] = from < to ? [from, to] : [to, from];
      for (let i = lo; i <= hi; i++) state.selection.add(state.view[i]);
    }
  } else if (state.selection.size === 1 && state.selection.has(index)) {
    // 再次点击唯一的已选中项 → 取消选中
    state.selection.clear();
  } else {
    state.selection.clear();
    state.selection.add(index);
  }

  state.lastClicked = index;

  // 只更新选中样式，绝不重建 DOM。
  // 重建会让两次点击落在不同元素上，浏览器就不会派发 dblclick，双击放大随之失效。
  applySelectionStyles();
  updateStatus();
});

function applySelectionStyles() {
  el.stage.querySelectorAll('.card').forEach((card) => {
    card.classList.toggle('sel', state.selection.has(Number(card.dataset.index)));
  });
}

/* ═══════════════ 右键上下文菜单 ═══════════════ */

function openCtxMenu(index, mouseX, mouseY) {
  closeCtxMenu();

  const item = state.items[index];
  if (!item) return;

  const menu = document.createElement('div');
  menu.className = 'ctxMenu';
  menu.dataset.index = String(index);

  const flagIcon = (kind) => kind === 'pick' ? 'i-flag' : (kind === 'reject' ? 'i-ban' : 'i-clear');
  const sizeText = formatSize(item.s);
  const dimText = item.w && item.h ? `${item.w}×${item.h}` : '尺寸未知';
  const starText = item.r > 0 ? `${item.r} 星` : '未评分';
  const flagText = item.f === 1 ? ' · 已留用' : (item.f === -1 ? ' · 已排除' : '');

  let html = `<div class="ctxHead">`
    + `<div class="ctxName" title="${escapeHtml(item.n)}">${escapeHtml(item.n)}</div>`
    + `<div class="ctxMeta">${dimText} · ${sizeText} · ${starText}${flagText}</div>`
    + `</div>`;

  html += `<button type="button" class="ctxItem" data-act="pick">`
    + `${svg('i-flag', 15, 'ci')}<span class="ctxText">标记为留用</span><em>P</em></button>`
    + `<button type="button" class="ctxItem" data-act="reject">`
    + `${svg('i-ban', 15, 'ci')}<span class="ctxText">标记为排除</span><em>X</em></button>`
    + `<button type="button" class="ctxItem" data-act="clear">`
    + `${svg('i-clear', 15, 'ci')}<span class="ctxText">清除所有标记</span><em>U</em></button>`;

  html += `<div class="ctxSep"></div>`;

  // 色彩标签横排
  html += `<div class="ctxTags"><span class="ctxTagLabel">`
    + `${svg('i-palette', 15, 'ci')}<span>色彩标签</span></span>`;
  for (let code = 1; code <= 5; code++) {
    html += `<button type="button" class="ctxTag ${LABEL_CLASS[code]}`
      + `${item.l === code ? ' on' : ''}" data-tag="${code}" title="${LABEL_NAME[code]}"></button>`;
  }
  html += `<button type="button" class="ctxTag none${item.l === 0 ? ' on' : ''}" `
    + `data-tag="0" title="无标签"></button></div>`;

  // 重复副本入口
  if (item.dc > 1) {
    html += `<button type="button" class="ctxItem" data-act="duplicates">`
      + `${svg('i-duplicate', 15, 'ci')}`
      + `<span class="ctxText">查看 ${item.dc} 张重复副本</span></button>`;
  }

  html += `<div class="ctxSep"></div>`;

  html += `<button type="button" class="ctxItem" data-act="copyPath">`
    + `${svg('i-copy', 15, 'ci')}<span class="ctxText">复制完整路径</span><em>Ctrl+C</em></button>`
    + `<button type="button" class="ctxItem" data-act="reveal">`
    + `${svg('i-reveal', 15, 'ci')}<span class="ctxText">在资源管理器中定位</span></button>`
    + `<button type="button" class="ctxItem danger" data-act="recycle">`
    + `${svg('i-trash', 15, 'ci')}<span class="ctxText">移入回收站</span><em>Delete</em></button>`;

  menu.innerHTML = html;

  // 菜单挂在 body 下（为了 fixed 定位），点击不会冒泡到 wall，
  // 所以必须自己处理点击 —— 之前挂掉正是因为这一点。
  menu.addEventListener('click', (event) => {
    const button = event.target.closest('button');
    if (!button) return;

    event.stopPropagation();

    // 色彩标签：不关闭菜单，允许连续调整
    const tag = button.dataset.tag;
    if (tag !== undefined) {
      const value = Number(tag);
      send({ cmd: 'setGrade', index, label: item.l === value ? 0 : value });
      return;
    }

    const action = button.dataset.act;
    closeCtxMenu();

    if (action === 'recycle') send({ cmd: 'delete', index });
    else if (action === 'copyPath') send({ cmd: 'copyPath', index });
    else if (action === 'reveal') send({ cmd: 'revealInExplorer', index });
    else if (action === 'pick') send({ cmd: 'setGrade', index, flag: 1 });
    else if (action === 'reject') send({ cmd: 'setGrade', index, flag: -1 });
    else if (action === 'clear') send({ cmd: 'setGrade', index, flag: 0, rating: 0, label: 0 });
    else if (action === 'duplicates') openLightbox(index);
  });

  menu.style.left = mouseX + 'px';
  menu.style.top = mouseY + 'px';
  document.body.appendChild(menu);

  // 防止菜单超出窗口右/下边缘
  const rect = menu.getBoundingClientRect();
  if (rect.right > window.innerWidth) {
    menu.style.left = Math.max(4, window.innerWidth - rect.width - 6) + 'px';
  }
  if (rect.bottom > window.innerHeight) {
    menu.style.top = Math.max(4, window.innerHeight - rect.height - 6) + 'px';
  }
}

// 点击菜单以外的任何地方时收起菜单
document.addEventListener('click', (event) => {
  if (!event.target.closest('.ctxMenu')) closeCtxMenu();
});

document.addEventListener('contextmenu', (event) => {
  if (!event.target.closest('.card')) closeCtxMenu();
});

function closeCtxMenu() {
  // 菜单挂在 body 下（fixed 定位），不能只在 stage 里找 —— 否则永远关不掉
  document.querySelectorAll('.ctxMenu').forEach((node) => node.remove());
}

/* ═══════════════ 侧栏与顶栏交互 ═══════════════ */

document.getElementById('sidebar').addEventListener('click', (event) => {
  /* ---------- 分组折叠 ---------- */
  const head = event.target.closest('.modHead');
  if (head) {
    head.closest('.mod').classList.toggle('collapsed');
    const expanded = !head.closest('.mod').classList.contains('collapsed');
    head.setAttribute('aria-expanded', String(expanded));
    return;
  }

  /* ---------- 文件夹树：点箭头只展开/折叠，不切换筛选 ---------- */
  const twist = event.target.closest('.twist');
  if (twist && twist.dataset.twist) {
    const name = twist.dataset.twist;
    if (state.collapsedFolders.has(name)) state.collapsedFolders.delete(name);
    else state.collapsedFolders.add(name);
    renderSidebar();
    return;
  }

  // 可点击的筛选行：既支持普通 .row，也支持色彩标签的 .labelChip
  const row = event.target.closest('.row, .labelChip');
  if (!row) return;

  const kind = row.dataset.kind;
  const raw = row.dataset.value;
  const f = state.filter;

  if (kind === 'all') {
    f.quick = null;
    f.folder = null;
    applyFilter();
    return;
  }

  if (kind === 'quick') {
    f.quick = f.quick === raw ? null : raw;
    applyFilter();
    return;
  }

  if (kind === 'star') {
    const value = Number(raw);
    f.minStar = f.minStar === value ? null : value;
  } else if (kind === 'label') {
    const value = Number(raw);
    f.label = f.label === value ? 0 : value;
  } else if (kind === 'flag') {
    const value = Number(raw);
    f.flag = f.flag === value ? null : value;
  } else if (kind === 'folder') {
    f.folder = f.folder === raw ? null : raw;
  }

  applyFilter();
});

el.search.addEventListener('input', () => {
  state.filter.search = el.search.value;
  applyFilter();
});

// Ctrl+F 聚焦搜索框
document.addEventListener('keydown', (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') {
    event.preventDefault();
    el.search.focus();
    el.search.select();
  }
});

el.thumbSize.addEventListener('input', () => {
  state.thumbWidth = Number(el.thumbSize.value);
  el.sizeValue.textContent = String(state.thumbWidth);
  computeLayout();
  renderVisible();
});

/* ---------- 视图模式切换 ---------- */

function setViewMode(mode) {
  state.viewMode = mode;
  el.btnViewWall.classList.toggle('on', mode === 'wall');
  el.btnViewGrid.classList.toggle('on', mode === 'grid');
  computeLayout();
  renderVisible();
}

el.btnViewWall.addEventListener('click', () => setViewMode('wall'));
el.btnViewGrid.addEventListener('click', () => setViewMode('grid'));

/* ---------- 全选 / 批量留用 ---------- */

el.btnSelectAll.addEventListener('click', () => {
  state.selection = new Set(state.view);
  state.lastClicked = state.view.length ? state.view[state.view.length - 1] : null;
  applySelectionStyles();
  updateStatus();
});

el.btnPickAll.addEventListener('click', () => {
  const targets = state.selection.size > 0 ? [...state.selection] : state.view;
  for (const index of targets) send({ cmd: 'setGrade', index, flag: 1 });
  setStatus(`已对 ${targets.length} 张设为留用`);
});

/* ---------- 排序控件 ---------- */

el.sortSelect.addEventListener('change', () => {
  const key = el.sortSelect.value;
  state.sort.key = key;
  // 生成时间默认看最新的，其余默认升序
  const opt = el.sortSelect.selectedOptions[0];
  state.sort.desc = key !== 'default' && opt && opt.dataset.desc === '1';
  applyFilter();
});

el.btnSortDir.addEventListener('click', () => {
  if (state.sort.key === 'default') return;
  state.sort.desc = !state.sort.desc;
  applyFilter();
  el.btnSortDir.textContent = state.sort.desc ? '↓' : '↑';
});

/* ---------- 顶部胶囊按钮 ---------- */

el.btnCopyRoot.addEventListener('click', () => {
  if (!state.root) return;
  navigator.clipboard.writeText(state.root).then(
    () => setStatus('图库路径已复制到剪贴板'),
    () => setStatus('复制失败'),
  );
});

el.btnSwitchRoot.addEventListener('click', () => {
  send({ cmd: 'switchRoot' });
});

el.btnRefresh.addEventListener('click', () => {
  state.selection.clear();
  send({ cmd: 'refresh' });
});

el.btnClearFilter.addEventListener('click', clearFilter);

/* ---------- 快捷键速查 ---------- */

function toggleHotkeys(show) {
  el.hotkeySheet.hidden = show === undefined ? !el.hotkeySheet.hidden : !show;
}
el.btnHotkeys.addEventListener('click', () => toggleHotkeys(true));
el.hotkeyClose.addEventListener('click', () => toggleHotkeys(false));
el.hotkeySheet.addEventListener('click', (event) => {
  if (event.target === el.hotkeySheet) toggleHotkeys(false);
});

window.addEventListener('resize', () => {
  closeCtxMenu();
  computeLayout();
  renderVisible();
});

// 垂直滚动条出现或消失会改变墙体的可用宽度，但**不会**触发 window.resize。
// 布局时若还没有滚动条，算出的宽度会比实际可用宽度大一个滚动条的宽度（约 9px），
// 结果最右一列卡片溢出被裁，表现为「错位」。
// 用 ResizeObserver 盯住墙体自身尺寸，任何变化都重新布局。
if (typeof ResizeObserver !== 'undefined') {
  let pending = 0;

  const observer = new ResizeObserver(() => {
    if (pending) return;
    pending = requestAnimationFrame(() => {
      pending = 0;
      computeLayout();
      renderVisible();
    });
  });

  observer.observe(el.wall);
}

/* ═══════════════ 键盘 ═══════════════ */

function applyGradeToSelection(payload) {
  for (const index of state.selection) {
    send(Object.assign({ cmd: 'setGrade', index }, payload));
  }
}

document.addEventListener('keydown', (event) => {
  if (event.target.tagName === 'INPUT' || event.target.tagName === 'SELECT') return;

  const key = event.key;
  const lower = key.toLowerCase();

  // 速查面板打开时，Esc 关闭
  if (!el.hotkeySheet.hidden && key === 'Escape') {
    toggleHotkeys(false);
    event.preventDefault();
    return;
  }

  // 放大查看模式
  if (state.lightboxIndex >= 0) {
    const index = state.lightboxIndex;

    if (key === 'Escape') {
      closeLightbox();
      event.preventDefault();
    } else if (key === 'ArrowLeft') {
      stepLightbox(-1);
      event.preventDefault();
    } else if (key === 'ArrowRight') {
      stepLightbox(1);
      event.preventDefault();
    } else if (key >= '0' && key <= '5') {
      send({ cmd: 'setGrade', index, rating: Number(key) });
      event.preventDefault();
    } else if (LABEL_KEY[lower]) {
      send({ cmd: 'setGrade', index, label: LABEL_KEY[lower] });
      event.preventDefault();
    } else if (lower === 'p') {
      send({ cmd: 'setGrade', index, flag: 1 });
      event.preventDefault();
    } else if (lower === 'x') {
      send({ cmd: 'setGrade', index, flag: -1 });
      event.preventDefault();
    } else if (lower === 'u') {
      send({ cmd: 'setGrade', index, rating: 0, flag: 0, label: 0 });
      event.preventDefault();
    }

    return;
  }

  // 全选
  if ((event.ctrlKey || event.metaKey) && lower === 'a') {
    event.preventDefault();
    state.selection = new Set(state.view);
    applySelectionStyles();
    updateStatus();
    return;
  }

  // 筛选（Shift 前缀）
  if (event.shiftKey) {
    const f = state.filter;
    let handled = true;

    if (lower === 'p') f.flag = f.flag === 1 ? null : 1;
    else if (lower === 'x') f.flag = f.flag === -1 ? null : -1;
    else if (lower === 'u') f.flag = f.flag === 0 ? null : 0;
    else if (lower === 'a') {
      clearFilter();
      event.preventDefault();
      return;
    } else if ('12345'.indexOf(key) >= 0) {
      const value = Number(key);
      f.minStar = f.minStar === value ? null : value;
    } else if (LABEL_KEY[lower]) {
      const value = LABEL_KEY[lower];
      f.label = f.label === value ? 0 : value;
    } else {
      handled = false;
    }

    if (handled) {
      event.preventDefault();
      applyFilter();
    }

    return;
  }

  // 图库：对选中项打标
  if (state.selection.size === 0) return;

  if (key >= '0' && key <= '5') {
    applyGradeToSelection({ rating: Number(key) });
    event.preventDefault();
  } else if (LABEL_KEY[lower]) {
    applyGradeToSelection({ label: LABEL_KEY[lower] });
    event.preventDefault();
  } else if (lower === 'p') {
    applyGradeToSelection({ flag: 1 });
    event.preventDefault();
  } else if (lower === 'x') {
    applyGradeToSelection({ flag: -1 });
    event.preventDefault();
  } else if (lower === 'u') {
    applyGradeToSelection({ rating: 0, flag: 0, label: 0 });
    event.preventDefault();
  } else if (key === 'Enter') {
    const first = [...state.selection][0];
    if (first !== undefined) openLightbox(first);
    event.preventDefault();
  }
});

/* ═══════════════ 拖拽归类 ═══════════════ */

el.stage.addEventListener('dragstart', (event) => {
  const card = event.target.closest('.card');
  if (!card) return;

  const index = Number(card.dataset.index);
  if (!state.selection.has(index)) {
    state.selection.clear();
    state.selection.add(index);
    renderVisible();
    updateStatus();
  }

  event.dataTransfer.effectAllowed = 'move';
  event.dataTransfer.setData('text/plain', [...state.selection].join(','));
});

el.filterFolders.addEventListener('dragover', (event) => {
  const row = event.target.closest('.row');
  if (!row || row.dataset.kind !== 'folder' || !row.dataset.value) return;

  event.preventDefault();
  event.dataTransfer.dropEffect = 'move';

  el.filterFolders.querySelectorAll('.row.dropOn').forEach((node) => {
    if (node !== row) node.classList.remove('dropOn');
  });
  row.classList.add('dropOn');
});

el.filterFolders.addEventListener('dragleave', (event) => {
  const row = event.target.closest('.row');
  if (row) row.classList.remove('dropOn');
});

el.filterFolders.addEventListener('drop', (event) => {
  const row = event.target.closest('.row');
  if (!row || row.dataset.kind !== 'folder') return;

  const folder = row.dataset.value;
  if (!folder) return;

  event.preventDefault();
  row.classList.remove('dropOn');

  const indices = [...state.selection];
  if (indices.length === 0) return;

  send({ cmd: 'moveFiles', indices, folder });
});

/* ---- 拖到侧栏色块 = 给选中图片批量打该标签 ---- */

el.filterLabels.addEventListener('dragover', (event) => {
  const chip = event.target.closest('.labelChip');
  if (!chip) return;

  event.preventDefault();
  event.dataTransfer.dropEffect = 'copy';
  chip.classList.add('dropOn');
});

el.filterLabels.addEventListener('dragleave', (event) => {
  const chip = event.target.closest('.labelChip');
  if (chip) chip.classList.remove('dropOn');
});

el.filterLabels.addEventListener('drop', (event) => {
  const chip = event.target.closest('.labelChip');
  if (!chip) return;

  event.preventDefault();
  chip.classList.remove('dropOn');

  const label = Number(chip.dataset.value);
  const indices = [...state.selection];
  if (indices.length === 0) return;

  for (const index of indices) send({ cmd: 'setGrade', index, label });
});

el.stage.addEventListener('dragend', () => {
  el.filterFolders
    .querySelectorAll('.row.dropOn')
    .forEach((node) => node.classList.remove('dropOn'));
  el.filterLabels
    .querySelectorAll('.labelChip.dropOn')
    .forEach((node) => node.classList.remove('dropOn'));
});

/* ═══════════════ 与宿主通信 ═══════════════ */

function patchCard(index) {
  const card = el.stage.querySelector(`.card[data-index="${index}"]`);
  if (!card) return;

  const item = state.items[index];

  // 星级
  card.querySelectorAll('.starText').forEach((node) => {
    const value = Number(node.dataset.star);
    const on = value <= item.r;
    node.classList.toggle('on', on);
    const use = node.querySelector('use');
    if (use) use.setAttribute('href', on ? '#i-star' : '#i-star-outline');
  });

  // 左上角旗标徽章
  const thumb = card.querySelector('.thumb');
  let badge = thumb.querySelector('.flagBadge');
  const wantFlag = item.f === 1 ? 'picked' : (item.f === -1 ? 'rejected' : '');

  if (wantFlag) {
    const inner = item.f === 1
      ? `${svg('i-flag', 11)}PICK`
      : svg('i-ban', 11);
    if (badge) {
      badge.className = `flagBadge ${wantFlag}`;
      badge.innerHTML = inner;
    } else {
      badge = document.createElement('span');
      badge.className = `flagBadge ${wantFlag}`;
      badge.innerHTML = inner;
      thumb.insertBefore(badge, thumb.firstChild);
    }
  } else if (badge) {
    badge.remove();
  }

  // 底部彩色标签
  card.querySelectorAll('.labelPick').forEach((node) => {
    const code = Number(node.dataset.label);
    node.classList.toggle('on', item.l === code);
    node.classList.toggle('cur', item.l === code);
  });

  // 旗标会同时影响整张卡片的灰度，需要即时同步
  card.classList.toggle('rejected', item.f === -1);
}

if (bridge) {
  bridge.addEventListener('message', (event) => {
    const message = event.data;
    if (!message || typeof message !== 'object') return;

    if (message.type === 'library') {
      state.root = message.root;
      state.items = message.items;
      el.rootPath.textContent = message.root;
      el.rootPath.title = message.root;

      closeLightbox();
      applyFilter();

      const report = message.report || {};
      setStatus(
        `已加载 ${report.total} 张 · 索引命中 ${report.fromIndex} · 实读 ${report.readFromDisk}`
        + ` · 已评分 ${report.graded} · 用时 ${Math.round(report.elapsedMs)} ms`,
        'ok',
      );
      return;
    }

    if (message.type === 'gradeUpdated') {
      const item = state.items[message.index];
      if (!item) return;

      item.r = message.rating;
      item.f = message.flag;
      item.l = message.label;

      patchCard(message.index);
      updateStatus();
      renderSidebar();

      if (state.lightboxIndex === message.index) updateLightbox();
      return;
    }

    if (message.type === 'md5') {
      const item = state.items[message.index];
      if (item) item.m = message.md5;

      if (state.lightboxIndex === message.index) {
        state.md5Pending = false;
        state.duplicates = message.duplicates || [];
        renderMd5();
      }
      return;
    }

    if (message.type === 'archived') {
      const fromPos = state.archiveFromPos;
      state.archiveFromPos = -1;

      for (const i of message.indices) {
        const item = state.items[i];
        if (item) item.archived = true;
      }

      // 已归档的文件已经剪切走了，从当前视图与选区中剔除
      state.view = state.view.filter((i) => !state.items[i].archived);
      state.selection = new Set(
        [...state.selection].filter((i) => !state.items[i].archived));

      computeLayout();
      renderVisible();
      updateStatus();
      renderSidebar();

      // 宿主已指明「接下来该看哪一张」：-1 表示没有后继了。
      // 用宿主给的 index 精确定位，不再靠位置推算，避免视图重建后错位。
      if (typeof message.nextIndex === 'number' && message.nextIndex >= 0
        && state.items[message.nextIndex] && !state.items[message.nextIndex].archived) {
        openLightbox(message.nextIndex);
        return;
      }

      if (message.self || message.sourceGone) {
        if (fromPos >= 0 && fromPos < state.view.length) {
          openLightbox(state.view[fromPos]);
          return;
        }
        if (state.view.length === 0) {
          closeLightbox();
          return;
        }
        openLightbox(state.view[state.view.length - 1]);
        return;
      }

      // 移走的是重复件、当前这张还在原位：留在原图，只重取一次详情，
      // 让提示条按最新的重复组刷新（可能还有重复，也可能已经清空）。
      if (!message.self) {
        requestMd5(state.lightboxIndex);
      }

      return;
    }

    if (message.type === 'deleted') {
      const item = state.items[message.index];
      if (item) item.deleted = true;

      // 已删除的条目从视图与选区中移除
      state.view = state.view.filter((i) => !state.items[i].deleted);
      state.selection = new Set(
        [...state.selection].filter((i) => !state.items[i].deleted));

      if (state.lightboxIndex === message.index) {
        closeLightbox();
      }

      computeLayout();
      renderVisible();
      updateStatus();
      renderSidebar();
      return;
    }

    if (message.type === 'duplicates') {
      for (const item of state.items) item.dc = 0;

      for (const flagged of message.items) {
        const item = state.items[flagged.i];
        if (item) item.dc = flagged.c;
      }

      renderVisible();
      renderSidebar();
      return;
    }

    if (message.type === 'md5Progress') {
      state.md5.done = message.done;
      state.md5.total = message.total;
      const pct = message.total ? Math.round((message.done / message.total) * 100) : 0;
      el.md5Fill.style.width = pct + '%';
      el.md5Text.textContent = `${message.done} / ${message.total}`;

      if (message.done >= message.total && message.total > 0) {
        setStatus(`MD5 计算完成：${message.done} / ${message.total}`, 'ok');
      } else {
        setStatus(`正在计算 MD5：${message.done} / ${message.total}`, 'busy');
      }
      return;
    }

    if (message.type === 'status') {
      setStatus(message.text);
    }
  });
}

// 初始：进入自适应流式模式
setViewMode('wall');
updateStatus();

send({ cmd: 'ready' });

// 供宿主在调试时调用（配合 --lightbox / --eval 开关做界面验证）
window.__openLightbox = openLightbox;
window.__state = state;
window.__setViewMode = setViewMode;
window.__toggleHotkeys = toggleHotkeys;
