// API 基础 URL
const API_BASE_URL = '/api/dns';

// 状态管理
let allRecords = [];

// DOM 元素
const elements = {
    addRecordForm: document.getElementById('addRecordForm'),
    recordsBody: document.getElementById('recordsBody'),
    searchInput: document.getElementById('searchInput'),
    refreshBtn: document.getElementById('refreshBtn'),
    clearAllBtn: document.getElementById('clearAllBtn'),
    serverStatus: document.getElementById('serverStatus'),
    recordCount: document.getElementById('recordCount'),
    toast: document.getElementById('toast')
};

// 初始化
document.addEventListener('DOMContentLoaded', () => {
    initializeEventListeners();
    checkServerHealth();
    loadRecords();

    // 自动刷新（每30秒）
    setInterval(() => {
        loadRecords(true);
    }, 30000);
});

// 初始化事件监听器
function initializeEventListeners() {
    elements.addRecordForm.addEventListener('submit', handleAddRecord);
    elements.searchInput.addEventListener('input', handleSearch);
    elements.refreshBtn.addEventListener('click', () => loadRecords());
    elements.clearAllBtn.addEventListener('click', handleClearAll);

    // 记录类型改变时，更新值的占位符
    document.getElementById('type').addEventListener('change', updateValuePlaceholder);
}

// 更新值输入框的占位符
function updateValuePlaceholder(e) {
    const type = e.target.value;
    const valueInput = document.getElementById('value');
    const domainInput = document.getElementById('domain');

    const placeholders = {
        'A': '192.168.1.100',
        'AAAA': '2001:db8::1',
        'CNAME': 'target.example.com',
        'TXT': 'v=spf1 include:_spf.google.com ~all',
        'NS': 'ns1.example.com',
        'MX': 'mail.example.com',
        'PTR': 'host.example.com'
    };

    valueInput.placeholder = placeholders[type] || '';

    // 更新域名提示
    const domainExamples = {
        'A': 'example.local 或 *.dev.local',
        'AAAA': 'example.local 或 *.dev.local',
        'CNAME': 'www.example.local 或 *.api.local',
        'default': 'example.local 或 *.example.local'
    };

    domainInput.placeholder = domainExamples[type] || domainExamples['default'];
}

// 检查服务器健康状态
async function checkServerHealth() {
    try {
        const response = await fetch('/health');
        const data = await response.json();

        if (response.ok && data.status === 'Healthy') {
            elements.serverStatus.textContent = '✅ 正常运行';
            elements.serverStatus.className = 'status-value healthy';
        } else {
            throw new Error('Server unhealthy');
        }
    } catch (error) {
        elements.serverStatus.textContent = '❌ 离线';
        elements.serverStatus.className = 'status-value error';
        showToast('无法连接到服务器', 'error');
    }
}

// 加载所有记录
async function loadRecords(silent = false) {
    if (!silent) {
        elements.recordsBody.innerHTML = '<tr class="loading-row"><td colspan="5">⏳ 加载中...</td></tr>';
    }

    try {
        const response = await fetch(`${API_BASE_URL}/records`);

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const records = await response.json();
        allRecords = Array.isArray(records) ? records : [];

        elements.recordCount.textContent = allRecords.length;
        renderRecords(allRecords);

        if (!silent) {
            showToast(`成功加载 ${allRecords.length} 条记录`, 'success');
        }
    } catch (error) {
        console.error('加载记录失败:', error);
        elements.recordsBody.innerHTML = '<tr class="empty-row"><td colspan="5">❌ 加载失败: ' + error.message + '</td></tr>';
        showToast('加载记录失败', 'error');
    }
}

// 渲染记录列表
function renderRecords(records) {
    if (records.length === 0) {
        elements.recordsBody.innerHTML = '<tr class="empty-row"><td colspan="5">📭 暂无记录</td></tr>';
        return;
    }

    const html = records.map(record => `
        <tr>
            <td><span class="record-domain">${escapeHtml(record.domain)}</span></td>
            <td><span class="record-type type-${record.type}">${record.type}</span></td>
            <td><span class="record-value">${escapeHtml(record.value)}</span></td>
            <td><span class="record-ttl">${record.ttl}s</span></td>
            <td class="record-actions">
                <button class="btn btn-danger btn-sm" onclick="handleDeleteRecord('${escapeHtml(record.domain)}', '${record.type}')">
                    🗑️ 删除
                </button>
            </td>
        </tr>
    `).join('');

    elements.recordsBody.innerHTML = html;
}

// 添加记录
async function handleAddRecord(e) {
    e.preventDefault();

    const formData = new FormData(e.target);
    const record = {
        domain: formData.get('domain').trim(),
        type: formData.get('type'),
        value: formData.get('value').trim(),
        ttl: parseInt(formData.get('ttl'))
    };

    // 验证
    if (!record.domain || !record.value) {
        showToast('请填写所有必填字段', 'error');
        return;
    }

    try {
        const response = await fetch(`${API_BASE_URL}/records`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(record)
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || `HTTP ${response.status}`);
        }

        showToast(`✅ 成功添加记录: ${record.domain}`, 'success');
        e.target.reset();
        document.getElementById('ttl').value = '3600'; // 重置 TTL
        await loadRecords();
    } catch (error) {
        console.error('添加记录失败:', error);
        showToast('添加记录失败: ' + error.message, 'error');
    }
}

// 删除记录
async function handleDeleteRecord(domain, type) {
    if (!confirm(`确定要删除记录 ${domain} (${type}) 吗？`)) {
        return;
    }

    try {
        const response = await fetch(`${API_BASE_URL}/records/${encodeURIComponent(domain)}/${type}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        showToast(`✅ 成功删除记录: ${domain}`, 'success');
        await loadRecords();
    } catch (error) {
        console.error('删除记录失败:', error);
        showToast('删除记录失败: ' + error.message, 'error');
    }
}

// 清空所有记录
async function handleClearAll() {
    if (!confirm('⚠️ 确定要清空所有 DNS 记录吗？此操作不可撤销！')) {
        return;
    }

    if (!confirm('⚠️ 再次确认：真的要删除所有记录吗？')) {
        return;
    }

    try {
        const response = await fetch(`${API_BASE_URL}/records`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        showToast('✅ 已清空所有记录', 'success');
        await loadRecords();
    } catch (error) {
        console.error('清空记录失败:', error);
        showToast('清空记录失败: ' + error.message, 'error');
    }
}

// 搜索记录
function handleSearch(e) {
    const searchTerm = e.target.value.toLowerCase().trim();

    if (!searchTerm) {
        renderRecords(allRecords);
        return;
    }

    const filtered = allRecords.filter(record =>
        record.domain.toLowerCase().includes(searchTerm) ||
        record.value.toLowerCase().includes(searchTerm) ||
        record.type.toLowerCase().includes(searchTerm)
    );

    renderRecords(filtered);
}

// 显示提示消息
function showToast(message, type = 'info') {
    elements.toast.textContent = message;
    elements.toast.className = `toast ${type} show`;

    setTimeout(() => {
        elements.toast.classList.remove('show');
    }, 3000);
}

// HTML 转义
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// 导出函数供全局使用
window.handleDeleteRecord = handleDeleteRecord;
