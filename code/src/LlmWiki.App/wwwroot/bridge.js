/**
 * LLM Wiki Bridge JS
 * 替代 fetch('/api/...')，通过 WebView2 postMessage 与 C# BridgeDispatcher 通信
 *
 * 协议（与 C# Bridge/BridgeProtocol.cs 对齐）：
 *   请求：{ cmd, args, requestId }
 *   响应：{ requestId, result }  —— result 直接是结果对象，不是字符串
 */

// 超时策略：默认 20s；0 表示不超时（等用户操作）
// 交互类命令要等人点对话框/选文件，不能超时；上传和大目录扫描给足时间。
const DEFAULT_TIMEOUT = 20000;
const TIMEOUTS = {
  'file-pick': 0,
  'folder-pick': 0,
  'dialog-confirm': 0,
  'dialog-input': 0,
  'upload': 300000,      // 5 分钟：office 转换可能较慢
  'prompt': 60000,       // 大库扫描
  'net-pc': 60000,       // SMB 枚举在断网共享上会慢
  'net-shares': 60000,
};
window.api = (async (path, options = {}) => {
  // 1. 将 /api/path 转换为命令名
  const routeMap = {
    '/api/settings': options.method === 'POST' ? 'settings-set' : 'settings-get',
    '/api/kbs': options.method === 'POST' ? 'kbs-create' : 'kbs-list',
    '/api/kbs/delete': 'kbs-delete',
    '/api/tree': 'tree',
    '/api/file': options.method === 'POST' ? 'file-write' : 'file-read',
    '/api/delete': 'delete',
    '/api/sources': 'sources',
    '/api/changes': 'changes',
    '/api/changes/clear': 'changes-clear',
    '/api/upload': 'upload',
    '/api/pick-files': 'file-pick',
    '/api/kb-watch': 'kb-watch',
    '/api/ui-state': options.method === 'POST' ? 'ui-state-set' : 'ui-state-get',
    '/api/drives': 'drives',
    '/api/browse': 'browse',
    '/api/places': 'places',
    '/api/pick-folder': 'folder-pick',
    '/api/net-pc': 'net-pc',
    '/api/net-shares': 'net-shares',
    '/api/prompt': 'prompt',
    '/api/share': options.method === 'POST' ? 'share-start' : 'share-status',
    '/api/share/stop': 'share-stop',
    '/api/reveal': 'reveal',
    '/api/open-file': 'open-file',
    '/api/open-kb-dir': 'open-kb-dir',
    '/api/dialog/confirm': 'dialog-confirm',
    '/api/dialog/input': 'dialog-input',
    '/api/clipboard': 'clipboard-set',
    '/api/file-assoc': options.method === 'POST' ? 'file-assoc-register' : 'file-assoc-get',
    '/api/file-assoc/unregister': 'file-assoc-unregister',
    '/api/file-reveal': 'file-reveal',
    '/api/file-open': 'file-open',
    '/api/file-copy-path': 'file-copy-path',
  };

  // 处理带 Query 的路径 (e.g., /api/sources?kb=...)
  const cleanPath = path.split('?')[0];
  const cmd = routeMap[cleanPath];
  if (!cmd) return { ok: false, json: async () => ({ error: 'Unknown API route: ' + cleanPath }) };

  // 2. 准备参数（Query 与 Body 合并成一份 JSON）
  const args = {};
  if (path.includes('?')) {
    const search = path.split('?')[1];
    new URLSearchParams(search).forEach((v, k) => { args[k] = v; });
  }
  if (options.body) {
    try {
      Object.assign(args, JSON.parse(options.body));
    } catch (e) {
      // 非 JSON body 忽略（当前协议所有 body 都是 JSON）
    }
  }

  // 3. 发请求并按 requestId 关联响应
  const requestId = Math.random().toString(36).substring(2, 15);
  const timeoutMs = options.timeout ?? TIMEOUTS[cmd] ?? DEFAULT_TIMEOUT;
  return new Promise((resolve) => {
    let timer = null;
    const finish = (payload) => {
      window.chrome.webview.removeEventListener('message', handler);
      if (timer) clearTimeout(timer);
      resolve(payload);
    };
    const handler = (event) => {
      let wrapper;
      try { wrapper = JSON.parse(event.data); } catch { return; }
      if (wrapper.requestId !== requestId) return;
      const payload = wrapper.result || {};
      // 模拟 fetch 的 .json() 形状
      finish({
        ok: !payload.error,
        json: async () => payload
      });
    };
    // 注意：宿主应答只投递到 chrome.webview 的事件上，window 的 message 事件收不到
    window.chrome.webview.addEventListener('message', handler);
    // 超时兜底：桥断时不能让调用方永远挂着（曾经表现为「点什么都没反应」）
    if (timeoutMs > 0) {
      timer = setTimeout(() => {
        console.warn('[bridge] 请求超时:', cmd, requestId);
        finish({
          ok: false,
          json: async () => ({
            error: '通讯超时（' + cmd + '）：界面与后台连接异常。请重试；持续失败请重启应用。'
          })
        });
      }, timeoutMs);
    }
    window.chrome.webview.postMessage({ cmd: cmd, args: JSON.stringify(args), requestId: requestId });
  });
});

/**
 * 宿主推送通道（无 requestId）：文件变更通知 { type:'fs-change', kb }。
 * 由 WPF FileWatcher 在 Agent 改完文件后发出，前端据此自动刷新列表。
 */
window.chrome?.webview?.addEventListener('message', (event) => {
  const msg = event.data;
  if (msg && msg.type === 'fs-change' && typeof window.onFsChange === 'function') {
    window.onFsChange(msg);
  }
});
