/**
 * LLM Wiki Bridge JS
 * 替代 fetch('/api/...')，通过 WebView2 postMessage 与 C# BridgeDispatcher 通信
 *
 * 协议（与 C# Bridge/BridgeProtocol.cs 对齐）：
 *   请求：{ cmd, args, requestId }
 *   响应：{ requestId, result }  —— result 直接是结果对象，不是字符串
 */
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
    '/api/drives': 'drives',
    '/api/browse': 'browse',
    '/api/places': 'places',
    '/api/net-pc': 'net-pc',
    '/api/net-shares': 'net-shares',
    '/api/prompt': 'prompt',
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
  return new Promise((resolve) => {
    const handler = (event) => {
      let wrapper;
      try { wrapper = JSON.parse(event.data); } catch { return; }
      if (wrapper.requestId !== requestId) return;
      window.removeEventListener('message', handler);
      const payload = wrapper.result || {};
      // 模拟 fetch 的 .json() 形状
      resolve({
        ok: !payload.error,
        json: async () => payload
      });
    };
    window.addEventListener('message', handler);
    window.chrome.webview.postMessage({ cmd: cmd, args: JSON.stringify(args), requestId: requestId });
  });
});
