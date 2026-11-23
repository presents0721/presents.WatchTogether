// ── 弹窗自身兜底：任何异常直接显示在红条里（否则 Edge 只会弹「出现故障」然后停用）──
function showPopupError(kind, detail) {
  try {
    const b = document.getElementById('errBanner');
    if (b) {
      b.style.display = 'block';
      b.textContent = kind + '：' + detail;
    }
  } catch { }
  try { chrome.runtime.sendMessage({ type: 'audio-note', note: 'popup ' + kind + '：' + detail }).catch(() => { }); } catch { }
}
window.addEventListener("error", (e) => showPopupError("弹窗异常", (e && e.message || "unknown") + " @ " + (e && e.filename || "?") + ":" + (e && e.lineno || 0)));
window.addEventListener("unhandledrejection", (e) => {
  const r = e && e.reason;
  showPopupError("弹窗 rejection", String(r && (r.stack || r.message) || r));
});

/* global chrome */

const stateEl = document.getElementById('state');
const detailEl = document.getElementById('detail');
const lastEl = document.getElementById('last');
const portEl = document.getElementById('port');
const savedEl = document.getElementById('saved');
const captionEl = document.getElementById('caption');

function mmss(value) {
  const n = Number(value);
  if (!Number.isFinite(n) || n < 0) return '--:--';
  const total = Math.floor(n);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (x) => String(x).padStart(2, '0');
  return h > 0 ? `${pad(h)}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}

function ago(at) {
  if (!at) return '还没';
  const seconds = Math.max(0, (Date.now() - at) / 1000);
  if (seconds < 60) return `${seconds.toFixed(0)} 秒前`;
  if (seconds < 3600) return `${(seconds / 60).toFixed(0)} 分钟前`;
  return `${(seconds / 3600).toFixed(1)} 小时前`;
}

function render(status) {
  if (!status) {
    stateEl.textContent = '扩展后台没有响应';
    stateEl.className = 'state bad';
    return;
  }

  if (!status.at) {
    stateEl.textContent = '还没有上报过';
    stateEl.className = 'state warn';
  } else if (status.ok) {
    stateEl.textContent = `已连上 127.0.0.1:${status.port || 9377}`;
    stateEl.className = 'state ok';
  } else {
    stateEl.textContent = '没连上陪看插件';
    stateEl.className = 'state bad';
  }

  detailEl.textContent = status.error
    ? status.error
    : `最近上报 ${ago(status.at)}`;
  lastEl.textContent = status.at
    ? `位置：${mmss(status.pos)}\n标题：${status.title || '（无标题）'}`
    : '还没有收到上报：打开一个有视频的页面并播放，再回来看这里。';
}

async function refresh() {
  try {
    const status = await chrome.runtime.sendMessage({ type: 'status' });
    render(status);
  } catch {
    stateEl.textContent = '扩展后台没有响应';
    stateEl.className = 'state bad';
  }
}

async function loadPort() {
  try {
    const { port } = await chrome.storage.local.get({ port: 9377 });
    portEl.value = String(port);
  } catch {
    portEl.value = '9377';
  }
}

async function loadOptions() {
  try {
    const { caption } = await chrome.storage.local.get({ caption: false });
    captionEl.checked = Boolean(caption);
  } catch {
    captionEl.checked = false;
  }
}

captionEl.addEventListener('change', () => {
  try {
    chrome.storage.local.set({ caption: captionEl.checked });
  } catch { /* 忽略 */ }
});

// ── 画面抓取 ───────────────────────────────────────────────
const captureBtn = document.getElementById('capture');   // 1.4.9 起按钮已移除：页面内通道无需授权
const captureStateEl = document.getElementById('captureState');

async function activeTabId() {
  try {
    const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
    return tabs[0]?.id ?? null;
  } catch {
    return null;
  }
}

async function refreshCapture() {
  try {
    const state = await chrome.runtime.sendMessage({ type: 'captureStatus' });

    const banner = document.getElementById('errBanner');
    if (banner) {
      if (state && state.swError) {
        banner.style.display = 'block';
        banner.textContent = '后台错误：' + state.swError;
      } else {
        banner.style.display = 'none';
      }
    }

    if (state && state.active) {
      captureBtn.textContent = '停止抓画面';
      captureStateEl.textContent = `抓取中 · 已上传 ${state.frames || 0} 帧`;
    } else {
      captureBtn.textContent = '开始抓画面';
      captureStateEl.textContent = (state && state.note) || '未开启';
    }
  } catch {
    captureStateEl.textContent = '扩展后台没有响应';
  }
}

if (captureBtn) captureBtn.addEventListener('click', legacyCaptureClick);

async function legacyCaptureClick() {
  captureStateEl.textContent = '处理中…';

  try {
    const status = await chrome.runtime.sendMessage({ type: 'captureStatus' });

    if (status && status.active) {
      await chrome.runtime.sendMessage({ type: 'stopCapture' });
    } else {
      const tabId = await activeTabId();
      const result = await chrome.runtime.sendMessage({ type: 'startCapture', tabId });
      if (!result || !result.ok) {
        captureStateEl.textContent = (result && result.error) || '启动失败';
        return;
      }
    }
  } catch (e) {
    captureStateEl.textContent = String((e && e.message) || e);
    return;
  }

  await refreshCapture();
}

document.getElementById('save').addEventListener('click', async () => {
  const port = Number(portEl.value);
  if (!Number.isFinite(port) || port <= 0 || port > 65535) {
    savedEl.textContent = '端口不合法';
    return;
  }
  savedEl.textContent = '正在保存并重连…';
  try {
    const result = await chrome.runtime.sendMessage({ type: 'setPort', port });
    savedEl.textContent = result?.ok
      ? `已保存端口 ${port}，连接正常`
      : `已保存端口 ${port}，但连不上（插件没在运行，或端口不一致）`;
    await refresh();
  } catch {
    savedEl.textContent = '保存失败：扩展后台没有响应';
  }
});

/**
 * 打开弹窗就自动开始抓画面。
 *
 * 为什么：tabCapture 必须由「用户对扩展的手势」触发——点扩展图标打开弹窗就是那个手势。
 * 之前要用户先找到「开始抓画面」按钮，很多人根本不知道有这一步（也确实藏得深）。
 * 现在只要在视频标签页上点一下扩展图标，就等于同意了抓画面。
 */
async function autoStartCapture() {
  try {
    const status = await chrome.runtime.sendMessage({ type: 'captureStatus' });
    if (status && status.active) return;

    const tabId = await activeTabId();
    if (tabId == null) return;

    const probe = await chrome.tabs.sendMessage(tabId, { type: 'probe' }).catch(() => null);

    // probe 为 null：页面是扩展更新前加载的，内容脚本还没注入。
    // 这种情况仍然尝试启动（tabCapture 不依赖内容脚本），只是抓不到 canvas 通道。
    if (probe && !probe.video) {
      captureStateEl.textContent = '这个标签页没有视频；请在视频页面点扩展图标';
      return;
    }

    if (!probe) {
      captureStateEl.textContent = '页面需要刷新一次（扩展刚更新过），正在尝试直接启动…';
    }

    // 按插件意愿选模式：要画面开抓帧流；只要声音（画面关）开纯录音流
    let result;
    let label;
    if (status.wantFrames === false && status.asrOn) {
      result = await chrome.runtime.sendMessage({ type: 'startAudio', tabId });
      label = '已自动开始（仅录音，画面抓取已关）';
    } else {
      result = await chrome.runtime.sendMessage({ type: 'startCapture', tabId });
      label = '已自动开始抓画面';
    }

    if (result && result.ok) {
      captureStateEl.textContent = label;
    } else {
      captureStateEl.textContent = (result && result.error) || '自动开始失败，请点下面的按钮';
    }
  } catch {
    // 老版本扩展没有这些消息时保持静默
  }
}

// 版本号直接显示在标题旁：方便确认浏览器加载的是不是最新代码（1.3.0 起才有自动抓画面）
try {
  const extVersionEl = document.getElementById('extVersion');
  if (extVersionEl && chrome.runtime?.getManifest) {
    extVersionEl.textContent = 'v' + chrome.runtime.getManifest().version;
  }
} catch { /* 忽略 */ }

(async () => {
  await loadPort();
  await loadOptions();
  try { await chrome.runtime.sendMessage({ type: 'hello' }); } catch { /* 忽略 */ }
  await refresh();
  await refreshCapture();
  await refreshDiag();
  // 1.4.7：画面/声音都走页面内通道（canvas + captureStream），无需授权页，不再自动创建
  await refreshCapture();

  async function refreshDiag() {
    const d = document.getElementById('diag');
    try {
      const { port = 9377 } = await chrome.storage.local.get({ port: 9377 });
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 3000);
      const r = await fetch(`http://127.0.0.1:${port}/api/state`, { signal: ctl.signal });
      clearTimeout(timer);
      const st = await r.json();
      d.textContent = `桥：正常 · 插件 ${st.build || '?'} · 上报 ${st.positionCount ?? 0} 条 · 音频片 ${st.audioCount ?? 0}`;
    } catch {
      d.textContent = '桥：连不上 —— Alife 的「一起看」插件没在运行？';
    }
  }

  setInterval(() => {
    refresh();
    refreshCapture();
    refreshDiag();
  }, 1000);
})();
