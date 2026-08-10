// DNS Core Server — 管理控制台脚本

const API_BASE = '/api/dns';
const CACHE_API = '/api/cache';
const API_KEY_STORAGE = 'dnscore.apiKey';
const THEME_STORAGE = 'dnscore.theme';

let allRecords = [];

// DOM 元素
const $ = {
    serverStatus: document.getElementById('serverStatus'),
    recordCount: document.getElementById('recordCount'),
    cacheEntries: document.getElementById('cacheEntries'),
    cacheHitRate: document.getElementById('cacheHitRate'),
    apiKeyBadge: document.getElementById('apiKeyBadge'),
    apiKeyBtn: document.getElementById('apiKeyBtn'),
    themeBtn: document.getElementById('themeBtn'),
    refreshBtn: document.getElementById('refreshBtn'),
    searchInput: document.getElementById('searchInput'),
    searchClear: document.getElementById('searchClear'),
    filterCount: document.getElementById('filterCount'),
    addRecordForm: document.getElementById('addRecordForm'),
    clearAllBtn: document.getElementById('clearAllBtn'),
    recordsTable: document.getElementById('recordsTable'),
    recordsBody: document.getElementById('recordsBody'),
    typeSelect: document.getElementById('type'),
    domainInput: document.getElementById('domain'),
    valueInput: document.getElementById('value'),
    valueHint: document.getElementById('valueHint'),
    toastHost: document.getElementById('toastHost'),

    // 上游配置
    upstreamForm: document.getElementById('upstreamForm'),
    upstreamState: document.getElementById('upstreamState'),
    enableUpstream: document.getElementById('enableUpstream'),
    upstreamFields: document.getElementById('upstreamFields'),
    serverChips: document.getElementById('serverChips'),
    serverInput: document.getElementById('serverInput'),
    addServerBtn: document.getElementById('addServerBtn'),
    raceMode: document.getElementById('raceMode'),
    raceHint: document.getElementById('raceHint'),
    upstreamTimeout: document.getElementById('upstreamTimeout'),
    upstreamWarn: document.getElementById('upstreamWarn'),
    resetUpstreamBtn: document.getElementById('resetUpstreamBtn'),
    saveUpstreamBtn: document.getElementById('saveUpstreamBtn'),
    effectiveBox: document.getElementById('effectiveBox'),
    effectiveList: document.getElementById('effectiveList'),
    orderLive: document.getElementById('orderLive'),

    // 标签页
    tabs: [...document.querySelectorAll('[role="tab"]')],
    tabRecordBadge: document.getElementById('tabRecordBadge'),
    tabUpstreamDot: document.getElementById('tabUpstreamDot')
};

const TAB_STORAGE = 'dnscore.activeTab';

// 上游配置的本地编辑状态；savedUpstream 用于"放弃修改"与脏检查
let upstreamServers = [];
let savedUpstream = null;

// --- 初始化 ---------------------------------------------------------------
document.addEventListener('DOMContentLoaded', init);

function init() {
    restoreTheme();
    restoreTab();
    updateKeyBadge();
    bindEvents();
    checkServerHealth();
    loadRecords();
    loadCacheStats();
    loadUpstreamSettings();
    startAutoRefresh();
}

function bindEvents() {
    // 标签页：点击切换 + 方向键导航（WAI-ARIA tabs 模式）
    $.tabs.forEach(tab => {
        tab.addEventListener('click', () => activateTab(tab.id));
        tab.addEventListener('keydown', handleTabKeydown);
    });

    $.apiKeyBtn.addEventListener('click', showApiKeyDialog);
    $.themeBtn.addEventListener('click', toggleTheme);
    $.refreshBtn.addEventListener('click', handleRefresh);
    $.searchInput.addEventListener('input', handleSearch);
    $.searchClear.addEventListener('click', clearSearch);
    $.addRecordForm.addEventListener('submit', handleAddRecord);
    $.clearAllBtn.addEventListener('click', handleClearAll);
    $.typeSelect.addEventListener('change', updateFormHints);
    updateFormHints();

    // 上游配置
    $.upstreamForm.addEventListener('submit', handleSaveUpstream);
    $.resetUpstreamBtn.addEventListener('click', () => applyUpstreamToForm(savedUpstream));
    $.enableUpstream.addEventListener('change', onUpstreamToggle);
    $.addServerBtn.addEventListener('click', () => addServerFromInput());
    $.raceMode.addEventListener('change', onRaceModeChange);

    // 回车添加服务器，而不是提交整个表单
    $.serverInput.addEventListener('keydown', e => {
        if (e.key === 'Enter') {
            e.preventDefault();
            addServerFromInput();
        }
    });

    document.querySelectorAll('.chip--add').forEach(btn => {
        btn.addEventListener('click', () => addServer(btn.dataset.ip));
    });

    // 任何改动都触发脏检查，用于显示"未保存"提示
    ['change', 'input'].forEach(evt => {
        $.upstreamForm.addEventListener(evt, () => {
            if (savedUpstream) updateUpstreamDirtyState();
        });
    });
}

// --- 标签页 ---------------------------------------------------------------
function restoreTab() {
    const saved = localStorage.getItem(TAB_STORAGE);
    // 只接受已知的 tab id，避免陈旧/伪造值导致两个面板都被隐藏
    if (saved && $.tabs.some(t => t.id === saved)) {
        activateTab(saved, { focus: false });
    }
}

function activateTab(tabId, options = {}) {
    const { focus = false } = options;

    $.tabs.forEach(tab => {
        const selected = tab.id === tabId;
        const panel = document.getElementById(tab.getAttribute('aria-controls'));

        tab.classList.toggle('is-active', selected);
        tab.setAttribute('aria-selected', String(selected));
        // 标签组内只保留一个 Tab 落点，方向键负责组内移动
        tab.tabIndex = selected ? 0 : -1;

        if (panel) panel.hidden = !selected;
    });

    if (focus) {
        document.getElementById(tabId)?.focus();
    }

    localStorage.setItem(TAB_STORAGE, tabId);
}

function handleTabKeydown(e) {
    const index = $.tabs.findIndex(t => t.id === e.currentTarget.id);
    let next = null;

    switch (e.key) {
        case 'ArrowRight':
        case 'ArrowDown':
            next = (index + 1) % $.tabs.length;
            break;
        case 'ArrowLeft':
        case 'ArrowUp':
            next = (index - 1 + $.tabs.length) % $.tabs.length;
            break;
        case 'Home':
            next = 0;
            break;
        case 'End':
            next = $.tabs.length - 1;
            break;
        default:
            return;
    }

    e.preventDefault();
    activateTab($.tabs[next].id, { focus: true });
}

/// 把记录数与未保存状态同步到标签上，便于在另一个标签页时也能看到
function updateTabIndicators() {
    $.tabRecordBadge.textContent = allRecords.length.toLocaleString();
    $.tabUpstreamDot.hidden = $.upstreamWarn.hidden;
}

// --- 主题 -----------------------------------------------------------------
function restoreTheme() {
    const saved = localStorage.getItem(THEME_STORAGE);
    if (saved === 'dark' || saved === 'light') {
        document.documentElement.setAttribute('data-theme', saved);
    }
}

function toggleTheme() {
    const root = document.documentElement;
    const current = root.getAttribute('data-theme');
    const next = current === 'dark' ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    localStorage.setItem(THEME_STORAGE, next);
}

// --- API Key 管理 ---------------------------------------------------------
function getApiKey() {
    return sessionStorage.getItem(API_KEY_STORAGE) || '';
}

function setApiKey(key) {
    if (key) {
        sessionStorage.setItem(API_KEY_STORAGE, key);
    } else {
        sessionStorage.removeItem(API_KEY_STORAGE);
    }
    updateKeyBadge();
}

function updateKeyBadge() {
    const hasKey = !!getApiKey();
    $.apiKeyBadge.hidden = !hasKey;
}

function showApiKeyDialog() {
    const current = getApiKey();
    const msg = current
        ? '当前已配置 API Key。输入新的密钥可覆盖，留空可清除：'
        : '服务端已启用管理 API 鉴权。\n请输入配置的 DNSCORE_API_KEY 环境变量值：';

    const input = prompt(msg, current ? '●'.repeat(12) : '');
    if (input === null) return;

    const trimmed = input.trim();
    if (trimmed && trimmed !== '●'.repeat(12)) {
        setApiKey(trimmed);
        showToast('API Key 已更新', 'success');
    } else if (!trimmed && current) {
        setApiKey('');
        showToast('API Key 已清除', 'info');
    }
}

// --- 统一请求封装 ---------------------------------------------------------
// 401 去重：首屏会并发发出多个请求（记录 / 缓存 / 上游），
// 若每个都单独提示，用户会连续收到多个弹窗和多条错误 toast
let authPromptPending = false;

async function apiFetch(url, options = {}) {
    const headers = { ...options.headers };
    const apiKey = getApiKey();

    if (apiKey) {
        headers['X-Api-Key'] = apiKey;
    }

    const response = await fetch(url, { ...options, headers });

    if (response.status === 401 || response.status === 403) {
        if (!authPromptPending) {
            authPromptPending = true;
            showToast('管理 API 需要有效的 API Key', 'error');

            setTimeout(() => {
                showApiKeyDialog();
                authPromptPending = false;

                // 填入密钥后重新加载全部数据
                if (getApiKey()) refreshAll();
            }, 600);
        }
        throw new Error('Unauthorized');
    }

    return response;
}

/// 重新加载全部数据（用于填入密钥后的恢复）
function refreshAll() {
    checkServerHealth();
    loadRecords(true);
    loadCacheStats();
    loadUpstreamSettings();
}

// --- 表单提示更新 ---------------------------------------------------------
function updateFormHints() {
    const type = $.typeSelect.value;

    const hints = {
        'A':     { domain: 'example.local 或 *.dev.local', value: '192.168.1.100', hint: 'IPv4 地址' },
        'AAAA':  { domain: 'example.local 或 *.dev.local', value: '2001:db8::1', hint: 'IPv6 地址' },
        'CNAME': { domain: 'www.example.local', value: 'target.example.local', hint: '目标域名（不含末尾点）' },
        'TXT':   { domain: 'example.local', value: 'v=spf1 include:_spf.google.com ~all', hint: '任意文本，自动分片' },
        'MX':    { domain: 'example.local', value: '10 mx1.example.local', hint: '优先级 + 空格 + 邮件服务器' },
        'NS':    { domain: 'example.local', value: 'ns1.example.local', hint: '名称服务器域名' },
        'SRV':   { domain: '_http._tcp.example.local', value: '10 60 80 web.example.local', hint: '优先级 权重 端口 目标' },
        'PTR':   { domain: '100.1.168.192.in-addr.arpa', value: 'example.local', hint: '反向解析目标域名' },
        'CAA':   { domain: 'example.local', value: '0 issue letsencrypt.org', hint: '标志 标签 值' }
    };

    const h = hints[type] || hints['A'];

    $.domainInput.placeholder = h.domain;
    $.valueInput.placeholder = h.value;
    $.valueHint.textContent = h.hint;
}

// --- 健康检查 & 缓存统计 --------------------------------------------------
async function checkServerHealth() {
    try {
        const response = await fetch('/health');
        const data = await response.json();

        if (response.ok && data.status === 'Healthy') {
            $.serverStatus.textContent = '正常运行';
            $.serverStatus.className = 'stat__value is-healthy';
        } else {
            throw new Error('Server unhealthy');
        }
    } catch (error) {
        $.serverStatus.textContent = '离线';
        $.serverStatus.className = 'stat__value is-error';
        showToast('无法连接到服务器', 'error');
    }
}

async function loadCacheStats() {
    try {
        const response = await apiFetch(`${CACHE_API}/stats`);
        if (!response.ok) throw new Error('Cache API error');

        const stats = await response.json();

        $.cacheEntries.textContent = stats.activeEntries?.toLocaleString() ?? '—';
        const hitRate = stats.hitRate != null && stats.hitRate >= 0
            ? `${(stats.hitRate * 100).toFixed(1)}%`
            : '—';
        $.cacheHitRate.textContent = hitRate;
    } catch (error) {
        console.warn('加载缓存统计失败:', error);
        $.cacheEntries.textContent = '—';
        $.cacheHitRate.textContent = '—';
    }
}

// --- 上游 DNS 配置 --------------------------------------------------------
async function loadUpstreamSettings() {
    try {
        const response = await apiFetch('/api/upstream');
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        const status = await response.json();
        savedUpstream = status;
        applyUpstreamToForm(status);
    } catch (error) {
        if (error.message !== 'Unauthorized') {
            console.error('加载上游配置失败:', error);
            $.upstreamState.textContent = '加载失败';
        }
    }
}

function applyUpstreamToForm(status) {
    if (!status) return;

    $.enableUpstream.checked = status.enableUpstreamDnsQuery;
    $.raceMode.value = String(status.raceUpstreams);
    $.upstreamTimeout.value = status.timeoutMilliseconds;
    upstreamServers = [...(status.upstreamDnsServers || [])];

    renderServerChips();
    onUpstreamToggle();
    onRaceModeChange();
    renderEffective(status);
    updateUpstreamDirtyState();
}

function renderEffective(status) {
    const list = status.effectiveServers || [];

    if (list.length === 0) {
        $.effectiveBox.hidden = true;
        return;
    }

    $.effectiveBox.hidden = false;
    // 未显式配置时说明这些地址来自系统探测，需要讲清来源
    const suffix = status.usingSystemDns ? '（自动探测的系统 DNS）' : '';
    $.effectiveList.textContent = list.join(' · ') + suffix;
}

function onUpstreamToggle() {
    $.upstreamFields.disabled = !$.enableUpstream.checked;
    // 禁用态会影响拖拽可用性，需重绘标签
    renderServerChips();
}

function onRaceModeChange() {
    const isRace = $.raceMode.value === 'true';
    $.raceHint.textContent = isRace
        ? '并行模式下列表顺序不影响结果，但每次查询都会打所有上游'
        : '顺序模式下列表次序即优先级，可拖拽标签调整（或 Alt + 方向键）';
    // 顺序模式才显示优先级序号
    renderServerChips();
}

function renderServerChips() {
    const isSequential = $.raceMode.value === 'false';

    // 顺序模式下可拖拽调整优先级；并行模式下顺序不影响结果，故不提供拖拽
    const draggable = isSequential && !$.upstreamFields.disabled && upstreamServers.length > 1;

    $.serverChips.innerHTML = upstreamServers.map((ip, i) => {
        const safeIp = escapeHtml(ip);
        const pos = `第 ${i + 1} 项，共 ${upstreamServers.length} 项`;

        return `
        <span class="chip${draggable ? ' chip--draggable' : ''}" role="listitem"
              data-ip="${safeIp}" data-index="${i}"
              ${draggable ? `tabindex="0" aria-label="${safeIp}，${pos}。按 Alt 加方向键可调整顺序"` : ''}>
            ${draggable ? '<span class="chip__grip" aria-hidden="true"><svg><use href="#i-grip"/></svg></span>' : ''}
            ${isSequential ? `<span class="chip__order" title="优先级 ${i + 1}">${i + 1}</span>` : ''}
            <span class="chip__text">${safeIp}</span>
            <button type="button" class="chip__del" data-ip="${safeIp}"
                    aria-label="移除 ${safeIp}">
                <svg aria-hidden="true"><use href="#i-x"/></svg>
            </button>
        </span>`;
    }).join('');

    $.serverChips.querySelectorAll('.chip__del').forEach(btn => {
        btn.addEventListener('click', () => removeServer(btn.dataset.ip));
    });

    if (draggable) {
        bindChipReorder();
    }

    syncQuickAddButtons();
}

/// 同步快捷添加按钮的可用状态。
/// 必须独立于 renderServerChips 的空列表分支：此前空列表会提前 return，
/// 导致删掉最后一个服务器后快捷项一直是禁用状态、无法再添加。
function syncQuickAddButtons() {
    const enabled = $.enableUpstream.checked;

    document.querySelectorAll('.chip--add').forEach(b => {
        b.disabled = !enabled || upstreamServers.includes(b.dataset.ip);
    });
}

function addServerFromInput() {
    const value = $.serverInput.value.trim();
    if (!value) return;

    if (addServer(value)) {
        $.serverInput.value = '';
        $.serverInput.setAttribute('aria-invalid', 'false');
    }
}

function addServer(ip) {
    const value = (ip || '').trim();

    if (!isValidIp(value)) {
        showToast(`无效的 IP 地址: ${value}`, 'error');
        $.serverInput.setAttribute('aria-invalid', 'true');
        return false;
    }

    if (upstreamServers.includes(value)) {
        showToast(`${value} 已在列表中`, 'info');
        return false;
    }

    // 前端先挡掉指向本机的地址，服务端也会再校验一次
    if (isLoopback(value)) {
        showToast('不能将上游指向本机地址，会形成查询环路', 'error');
        return false;
    }

    if (upstreamServers.length >= 16) {
        showToast('上游服务器最多 16 个', 'error');
        return false;
    }

    upstreamServers.push(value);
    renderServerChips();
    updateUpstreamDirtyState();
    return true;
}

function removeServer(ip) {
    upstreamServers = upstreamServers.filter(s => s !== ip);
    renderServerChips();
    updateUpstreamDirtyState();
}

// --- 上游顺序拖拽排序 ------------------------------------------------------
// 顺序模式下列表次序即查询优先级，因此需要能调整顺序。
// 使用 Pointer Events 而非 HTML5 drag-and-drop：后者在移动端基本不可用，
// 前者同一套代码即可覆盖鼠标与触摸。

/// 把 from 位置的元素移到 to 位置
function moveServer(from, to) {
    if (from === to || from < 0 || to < 0 ||
        from >= upstreamServers.length || to >= upstreamServers.length) {
        return false;
    }

    const next = [...upstreamServers];
    const [item] = next.splice(from, 1);
    next.splice(to, 0, item);
    upstreamServers = next;
    return true;
}

/// 按指针位置找出应插入的目标下标。
/// 标签会换行排布，因此先按行（y）筛选，再在行内按 x 判断插入点。
function findDropIndex(clientX, clientY, draggedIndex) {
    const chips = [...$.serverChips.querySelectorAll('.chip')];
    if (chips.length === 0) return draggedIndex;

    let best = null;
    let bestDist = Infinity;

    chips.forEach((chip, i) => {
        const r = chip.getBoundingClientRect();
        // 垂直方向到该标签所在行的距离（行内为 0）
        const dy = clientY < r.top ? r.top - clientY
                 : clientY > r.bottom ? clientY - r.bottom : 0;
        const cx = r.left + r.width / 2;
        const dx = Math.abs(clientX - cx);
        // 行优先：dy 权重远大于 dx
        const dist = dy * 1000 + dx;

        if (dist < bestDist) {
            bestDist = dist;
            const after = clientX > cx && dy === 0;
            best = after ? i + 1 : i;
        }
    });

    if (best === null) return draggedIndex;

    // 目标下标要换算成"移除被拖项后"的插入位置
    let target = best;
    if (best > draggedIndex) target = best - 1;
    return Math.max(0, Math.min(upstreamServers.length - 1, target));
}

function bindChipReorder() {
    $.serverChips.querySelectorAll('.chip--draggable').forEach(chip => {
        chip.addEventListener('pointerdown', onChipPointerDown);
        chip.addEventListener('keydown', onChipKeydown);
    });
}

function onChipPointerDown(e) {
    // 点删除按钮时不启动拖拽
    if (e.target.closest('.chip__del')) return;
    // 只响应主键/单指
    if (e.button !== undefined && e.button !== 0) return;

    const chip = e.currentTarget;
    const fromIndex = Number(chip.dataset.index);
    const startX = e.clientX;
    const startY = e.clientY;

    let dragging = false;
    let targetIndex = fromIndex;

    // 捕获指针，确保移出元素后仍能收到事件
    try { chip.setPointerCapture(e.pointerId); } catch { /* 忽略 */ }

    const onMove = ev => {
        // 超过阈值才认定为拖拽，避免与普通点击/聚焦冲突
        if (!dragging) {
            if (Math.hypot(ev.clientX - startX, ev.clientY - startY) < 5) return;
            dragging = true;
            chip.classList.add('is-dragging');
            $.serverChips.classList.add('is-reordering');
        }

        targetIndex = findDropIndex(ev.clientX, ev.clientY, fromIndex);
        paintDropTarget(fromIndex, targetIndex);
    };

    const onUp = () => {
        chip.removeEventListener('pointermove', onMove);
        chip.removeEventListener('pointerup', onUp);
        chip.removeEventListener('pointercancel', onUp);
        try { chip.releasePointerCapture(e.pointerId); } catch { /* 忽略 */ }

        clearDropTarget();
        $.serverChips.classList.remove('is-reordering');
        chip.classList.remove('is-dragging');

        if (!dragging) return;

        if (moveServer(fromIndex, targetIndex)) {
            renderServerChips();
            updateUpstreamDirtyState();
            announceOrder(upstreamServers[targetIndex], targetIndex);
            // 重绘后把焦点还给被移动的标签，便于连续调整
            focusChipAt(targetIndex);
        }
    };

    chip.addEventListener('pointermove', onMove);
    chip.addEventListener('pointerup', onUp);
    chip.addEventListener('pointercancel', onUp);
}

/// 键盘调序：Alt + 方向键。
/// 不占用裸方向键，避免与页面滚动、以及标签页的方向键导航冲突。
function onChipKeydown(e) {
    const from = Number(e.currentTarget.dataset.index);
    let to = null;

    if (e.altKey && (e.key === 'ArrowLeft' || e.key === 'ArrowUp')) {
        to = from - 1;
    } else if (e.altKey && (e.key === 'ArrowRight' || e.key === 'ArrowDown')) {
        to = from + 1;
    } else if (e.key === 'Delete' || e.key === 'Backspace') {
        e.preventDefault();
        removeServer(e.currentTarget.dataset.ip);
        return;
    } else {
        return;
    }

    e.preventDefault();

    if (moveServer(from, to)) {
        const ip = upstreamServers[to];
        renderServerChips();
        updateUpstreamDirtyState();
        announceOrder(ip, to);
        focusChipAt(to);
    }
}

function focusChipAt(index) {
    const chip = $.serverChips.querySelector(`.chip[data-index="${index}"]`);
    if (chip && typeof chip.focus === 'function') chip.focus();
}

/// 在目标位置显示插入指示条
function paintDropTarget(fromIndex, targetIndex) {
    clearDropTarget();

    if (fromIndex === targetIndex) return;

    const chip = $.serverChips.querySelector(`.chip[data-index="${targetIndex}"]`);
    if (!chip) return;

    chip.classList.add(targetIndex > fromIndex ? 'is-drop-after' : 'is-drop-before');
}

function clearDropTarget() {
    $.serverChips.querySelectorAll('.is-drop-before, .is-drop-after').forEach(el => {
        el.classList.remove('is-drop-before', 'is-drop-after');
    });
}

/// 顺序变化对读屏用户不可见，需显式播报
function announceOrder(ip, index) {
    if (!$.orderLive) return;
    $.orderLive.textContent = `${ip} 已移到第 ${index + 1} 位，共 ${upstreamServers.length} 项`;
}

function isValidIp(value) {
    if (!value) return false;

    // IPv4：必须四段完整写法。
    // 不能只靠"能解析"判断——inet_aton 简写会把 223.5.5 当成 223.5.0.5，
    // 少打一位就静默指向另一台服务器。
    if (value.indexOf(':') === -1) {
        const parts = value.split('.');
        if (parts.length !== 4) return false;

        return parts.every(p => {
            if (!/^\d{1,3}$/.test(p)) return false;
            return Number(p) <= 255;
        });
    }

    // IPv6：交给浏览器的 URL 解析器判断，避免自己写易错的正则。
    // 仅凭字符集正则会放过 "::::" 这类非法串。
    try {
        return new URL(`http://[${value}]/`).hostname.startsWith('[');
    } catch {
        return false;
    }
}

function isLoopback(value) {
    return value.startsWith('127.') || value === '::1' || value === '0.0.0.0' || value === '::';
}

function collectUpstreamForm() {
    return {
        enableUpstreamDnsQuery: $.enableUpstream.checked,
        upstreamDnsServers: [...upstreamServers],
        raceUpstreams: $.raceMode.value === 'true',
        timeoutMilliseconds: parseInt($.upstreamTimeout.value, 10) || 3000
    };
}

function updateUpstreamDirtyState() {
    if (!savedUpstream) return;

    const current = collectUpstreamForm();
    const dirty =
        current.enableUpstreamDnsQuery !== savedUpstream.enableUpstreamDnsQuery ||
        current.raceUpstreams !== savedUpstream.raceUpstreams ||
        current.timeoutMilliseconds !== savedUpstream.timeoutMilliseconds ||
        current.upstreamDnsServers.join(',') !== (savedUpstream.upstreamDnsServers || []).join(',');

    $.upstreamState.textContent = dirty ? '有未保存的修改' : '';
    $.upstreamWarn.hidden = !dirty;
    $.resetUpstreamBtn.disabled = !dirty;

    // 在 DNS 记录标签页时也能看到上游有未保存改动
    updateTabIndicators();
}

async function handleSaveUpstream(e) {
    e.preventDefault();

    const settings = collectUpstreamForm();

    if (settings.timeoutMilliseconds < 200 || settings.timeoutMilliseconds > 30000) {
        showToast('超时时间必须在 200–30000 毫秒之间', 'error');
        return;
    }

    $.saveUpstreamBtn.disabled = true;

    try {
        const response = await apiFetch('/api/upstream', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(settings)
        });

        if (!response.ok) {
            const err = await response.json().catch(() => ({}));
            throw new Error(err.error || `HTTP ${response.status}`);
        }

        const status = await response.json();
        savedUpstream = status;
        applyUpstreamToForm(status);

        const mode = settings.raceUpstreams ? '并行竞速' : '顺序尝试';
        showToast(`上游配置已保存并生效（${mode}）`, 'success');

        // 上游变更会清空缓存，刷新统计以反映
        loadCacheStats();
    } catch (error) {
        if (error.message !== 'Unauthorized') {
            console.error('保存上游配置失败:', error);
            showToast(`保存失败: ${error.message}`, 'error');
        }
    } finally {
        $.saveUpstreamBtn.disabled = false;
    }
}

// --- 记录管理 -------------------------------------------------------------
async function loadRecords(silent = false) {
    if (!silent) {
        $.recordsBody.innerHTML = '<tr class="state-row"><td colspan="5"><span class="spinner"></span> 加载中…</td></tr>';
    }

    try {
        const response = await apiFetch(`${API_BASE}/records`);

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const records = await response.json();
        allRecords = Array.isArray(records) ? records : [];

        $.recordCount.textContent = allRecords.length.toLocaleString();
        renderRecords(allRecords);
        updateTabIndicators();

        if (!silent) {
            showToast(`成功加载 ${allRecords.length} 条记录`, 'success');
        }
    } catch (error) {
        if (error.message !== 'Unauthorized') {
            console.error('加载记录失败:', error);
            $.recordsBody.innerHTML = `<tr class="state-row state-row--error"><td colspan="5">加载失败: ${escapeHtml(error.message)}</td></tr>`;
            showToast('加载记录失败', 'error');
        }
    }
}

function renderRecords(records) {
    if (records.length === 0) {
        $.recordsBody.innerHTML = `
            <tr class="state-row">
                <td colspan="5">
                    <div class="empty-state">
                        <svg><use href="#i-inbox"/></svg>
                        <strong>暂无记录</strong>
                        <span>从上方表单添加您的第一条 DNS 记录</span>
                    </div>
                </td>
            </tr>`;
        return;
    }

    const html = records.map(rec => {
        // 泛域名前缀加高亮 span，便于在长列表中快速辨认
        const domainDisplay = rec.domain.startsWith('*.')
            ? `<span class="wild">*.</span>${escapeHtml(rec.domain.slice(2))}`
            : escapeHtml(rec.domain);

        return `
            <tr data-domain="${escapeHtml(rec.domain)}" data-type="${escapeHtml(rec.type)}">
                <td data-label="域名" class="rec-domain">${domainDisplay}</td>
                <td data-label="类型"><span class="tag tag--${escapeHtml(rec.type)}">${escapeHtml(rec.type)}</span></td>
                <td data-label="记录值" class="rec-value"><div class="rec-value__text">${escapeHtml(rec.value)}</div></td>
                <td data-label="TTL" class="rec-ttl num">${rec.ttl.toLocaleString()}s</td>
                <td class="col-actions">
                    <button type="button" class="row-del" title="删除记录"
                            aria-label="删除 ${escapeHtml(rec.domain)} ${escapeHtml(rec.type)}">
                        <svg aria-hidden="true"><use href="#i-trash"/></svg>
                    </button>
                </td>
            </tr>`;
    }).join('');

    $.recordsBody.innerHTML = html;

    // 用事件委托替代 inline onclick，避免 JS 字符串转义问题
    $.recordsBody.querySelectorAll('.row-del').forEach(btn => {
        btn.addEventListener('click', handleDeleteRecord);
    });
}

async function handleAddRecord(e) {
    e.preventDefault();

    const formData = new FormData(e.target);
    const record = {
        domain: formData.get('domain').trim(),
        type: formData.get('type'),
        value: formData.get('value').trim(),
        ttl: parseInt(formData.get('ttl'), 10)
    };

    if (!record.domain || !record.value) {
        showToast('请填写所有必填字段', 'error');
        return;
    }

    if (isNaN(record.ttl) || record.ttl < 1) {
        showToast('TTL 必须为正整数', 'error');
        return;
    }

    try {
        const response = await apiFetch(`${API_BASE}/records`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(record)
        });

        if (!response.ok) {
            const error = await response.json().catch(() => ({}));
            throw new Error(error.error || `HTTP ${response.status}`);
        }

        showToast(`已添加记录: ${record.domain}`, 'success');
        e.target.reset();
        $.typeSelect.value = 'A';
        document.getElementById('ttl').value = '3600';
        updateFormHints();
        await loadRecords();
    } catch (error) {
        if (error.message !== 'Unauthorized') {
            console.error('添加记录失败:', error);
            showToast(`添加失败: ${error.message}`, 'error');
        }
    }
}

async function handleDeleteRecord(event) {
    const btn = event.currentTarget;
    const row = btn.closest('tr');
    const domain = row.dataset.domain;
    const type = row.dataset.type;

    if (!confirm(`确定要删除 ${domain} (${type}) 吗？`)) {
        return;
    }

    try {
        const response = await apiFetch(`${API_BASE}/records/${encodeURIComponent(domain)}/${type}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        showToast(`已删除记录: ${domain}`, 'success');
        await loadRecords();
    } catch (error) {
        if (error.message !== 'Unauthorized') {
            console.error('删除记录失败:', error);
            showToast('删除失败', 'error');
        }
    }
}

async function handleClearAll() {
    if (!confirm(`确定要清空所有 ${allRecords.length} 条记录吗？此操作不可恢复！`)) {
        return;
    }

    try {
        const response = await apiFetch(`${API_BASE}/records`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        showToast('已清空全部记录', 'success');
        await loadRecords();
    } catch (error) {
        if (error.message !== 'Unauthorized') {
            console.error('清空记录失败:', error);
            showToast('清空失败', 'error');
        }
    }
}

// --- 搜索 -----------------------------------------------------------------
function handleSearch(e) {
    const query = e.target.value.trim().toLowerCase();

    $.searchClear.hidden = !query;

    if (!query) {
        renderRecords(allRecords);
        $.filterCount.textContent = '';
        return;
    }

    const filtered = allRecords.filter(rec =>
        rec.domain.toLowerCase().includes(query) ||
        rec.type.toLowerCase().includes(query) ||
        rec.value.toLowerCase().includes(query)
    );

    renderRecords(filtered);
    $.filterCount.textContent = `找到 ${filtered.length} / ${allRecords.length} 条`;
}

function clearSearch() {
    $.searchInput.value = '';
    $.searchClear.hidden = true;
    renderRecords(allRecords);
    $.filterCount.textContent = '';
}

// --- 刷新 -----------------------------------------------------------------
async function handleRefresh() {
    $.refreshBtn.classList.add('is-busy');
    $.refreshBtn.disabled = true;

    await Promise.all([
        checkServerHealth(),
        loadRecords(true),
        loadCacheStats(),
        // 只在没有未保存改动时重载上游配置，否则会覆盖用户正在编辑的内容
        $.upstreamWarn.hidden ? loadUpstreamSettings() : Promise.resolve()
    ]).finally(() => {
        $.refreshBtn.classList.remove('is-busy');
        $.refreshBtn.disabled = false;
    });

    showToast('已刷新', 'info');
}

function startAutoRefresh() {
    setInterval(() => {
        loadRecords(true);
        loadCacheStats();
    }, 30000);
}

// --- Toast 提示 -----------------------------------------------------------
// 改为创建/移除元素，而非复用：连续操作时提示可堆叠显示
function showToast(message, type = 'info') {
    const toast = document.createElement('div');
    toast.className = `toast toast--${type}`;

    const iconMap = {
        success: 'check',
        error: 'alert',
        info: 'info'
    };

    toast.innerHTML = `
        <svg aria-hidden="true"><use href="#i-${iconMap[type] || 'info'}"/></svg>
        <span>${escapeHtml(message)}</span>
    `;

    $.toastHost.appendChild(toast);

    // 强制 reflow 触发进入动画
    toast.offsetHeight;

    setTimeout(() => {
        toast.classList.add('is-leaving');
        setTimeout(() => toast.remove(), 200);
    }, 3500);
}

// --- 工具 -----------------------------------------------------------------
/// HTML 转义。同时用于元素内容与属性值，因此必须转义引号：
/// 基于 textContent 的写法不会转义 " 和 '，属性值里含双引号的域名
/// 可以闭合属性并注入 onfocus/autofocus 等，构成可利用的 XSS。
function escapeHtml(str) {
    return String(str ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

