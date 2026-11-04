export const HOST = 'com.flow.downloader';
const actions = new Set(['ping', 'inspect', 'enqueue', 'queue', 'pause', 'resume', 'retry', 'cancel', 'show']);
export function validRequest(request) {
  if (!request || request.v !== 1 || !actions.has(request.action)) return false;
  if (request.action === 'inspect' && (typeof request.url !== 'string' || request.url.length > 8192)) return false;
  return JSON.stringify(request).length < 32000;
}
export function connectionError(message = '') {
  if (/not found|not registered|does not exist/i.test(message)) return 'HOST_MISSING';
  if (/forbidden|access.*denied/i.test(message)) return 'HOST_FORBIDDEN';
  return 'HOST_DISCONNECTED';
}
export function callNative(api, request, timeoutMs = 200000) {
  if (!validRequest(request)) return Promise.resolve({v: 1, ok: false, error: {code: 'INVALID_REQUEST'}});
  return new Promise(resolve => {
    let port, timer, settled = false;
    const finish = result => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try { port?.disconnect(); } catch {}
      resolve(result);
    };
    const fail = code => finish({v: 1, ok: false, error: {code}});
    try {
      port = api.runtime.connectNative(HOST);
      port.onMessage.addListener(message => {
        if (message?.v !== 1 || typeof message.ok !== 'boolean') fail('PROTOCOL_VERSION');
        else finish(message);
      });
      port.onDisconnect.addListener(() => fail(connectionError(api.runtime.lastError?.message)));
      timer = setTimeout(() => fail('TIMEOUT'), timeoutMs);
      port.postMessage(request);
    } catch (error) { fail(connectionError(error.message)); }
  });
}
