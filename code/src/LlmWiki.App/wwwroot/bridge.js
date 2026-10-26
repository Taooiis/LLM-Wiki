/**
 * LLM Wiki Bridge JS
 * 替代 fetch('/api/...')，通过 WebView2 postMessage 与 C# BridgeDispatcher 通信
 */
window.api = (async (path, options = {}) => {
  // 1. 将 /api/path 转换为命令名 (e.g., /api/kbs -> kbs-list, /api/settings -> settings-get)
  const routeMap = {
    '/api/settings': options.method === 'POST' ? 'settings-set' : 'settings-get',
    '/api/kbs': options.method === 'POST' ? 'kbs-create' : 'kbs-list',
    '/api/kbs/delete': 'kbs-delete',
    '/api/tree': 'tree',
    '/api/file': options.method === 'POST' ? 'file-write' : 'file-read',
    '/api/file/delete': 'delete',
    '/api/sources': 'sources',
    '/api/changes': 'changes',
    '/api/changes/clear': 'changes-clear',
    '/api/upload': 'upload',
    '/api/drives': 'drives',
    '/api/browse': 'browse',
    '/api/places': 'places',
    '/api/net-pc': 'net-pc',
    '/api/net-shares': 'net-shares',
  };

  // 处理带 Query 的路径 (e.g., /api/sources?kb=...)
  let cleanPath = path.split('?')[0];
  let cmd = routeMap[cleanPath];
  if (!cmd) return { error: 'Unknown API route: ' + cleanPath };

  // 2. 准备参数 (将 Body 和 Query 统一为 JSON)
  const args = {};
  // 提取 Query Params
  if (path.includes('?')) {
    const search = path.split('?')[1];
    new URLSearchParams(search).forEach((v, k) => { args[k] = v; });
  }
  // 提取 Body
  if (options.body) {
    try {
      const body = JSON.parse(options.body);
      Object.assign(args, body);
    } catch (e) {
      // 如果不是 JSON，尝试作为字符串处理（但 spec 定义绝大多数是 JSON）
    }
  }

  // 3. 通过 WebView2 发送请求
  const requestId = Math.random().toString(36).substring(2, 15);
  return new Promise((resolve) => {
    const handler = (event) => {
      const resp = JSON.parse(event.data);
      if (resp.requestId === requestId) {
        window.removeEventListener('message', handler);
        // 模拟 fetch 的 .json() 形状
        resolve({
          ok: !resp.error,
          json: async () => resp
        });
      }
    };
    window.addEventListener('message', handler);
    window.chrome.webview.postMessage({ cmd: cmd, args: JSON.stringify(args), requestId });
  });
});
