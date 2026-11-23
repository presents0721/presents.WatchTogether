// ── 顶层兜底（必须最先注册）──────────────────────────────
// SW 里任何未捕获异常都会让 Edge 弹「出现故障」并自动停用扩展。
// 全部接住：记录到 storage，弹窗顶部直接显示，不再需要 DevTools。
let lastSwError = "";

function recordSwError(kind, detail) {
  lastSwError = kind + ": " + detail;
  try { chrome.storage.local.set({ lastSwError, lastSwErrorAt: Date.now() }); } catch { }
  try { console.error("[一起看] SW", lastSwError); } catch { }
}

self.addEventListener("error", (e) => {
  recordSwError("error", (e && e.message || "unknown") + " @ " + (e && e.filename || "?") + ":" + (e && e.lineno || 0));
});
self.addEventListener("unhandledrejection", (e) => {
  const r = e && e.reason;
  recordSwError("rejection", String(r && (r.stack || r.message) || r));
});

/**
 * background.js — 把页面报上来的位置 POST 给本机的 Alife「一起看」插件。
 *
 * 为什么由这里发而不是 content.js 发：页面是 https 公网站点，目标是 127.0.0.1，
 * 跨域 + 私有网络访问有两道闸门；扩展后台声明了 host_permissions，不受限制。
 *
 * 除了位置，启动时还会打一声招呼（/api/hello）：光看「有没有收到位置」，
 * 分不清「扩展没装」和「装了但这个页面没视频」。
 *
 * 端口默认 9377，可在 popup 里改；改完会和插件配置里的端口一致。
 *
 * 移植自 SAOS-3624/dsh-watch-together 的 extension/background.js（Apache-2.0）。
 */

/* global chrome */

const DEFAULT_PORT = 9377;
const DEFAULT_BASE = `http://127.0.0.1:${DEFAULT_PORT}`;

/** 最近一次上报的结果，popup 会来问 */
let lastResult = {
  ok: false,
  at: 0,
  error: '还没有上报过',
  pos: null,
  title: null,
  port: DEFAULT_PORT
};

/** service worker 每次被唤醒都会重新加载这个模块 —— 正好当「我还活着」的心跳 */
let helloSent = false;

async function getPort() {
  try {
    const { port } = await chrome.storage.local.get({ port: DEFAULT_PORT });
    const value = Number(port);
    return Number.isFinite(value) && value > 0 && value < 65536 ? value : DEFAULT_PORT;
  } catch {
    return DEFAULT_PORT;
  }
}

function baseFor(port) {
  return `http://127.0.0.1:${port}`;
}

// ─────────────────────────────────────────────────────────────
// 画面抓取（tabCapture + offscreen 文档）
// ─────────────────────────────────────────────────────────────

const OFFSCREEN_PATH = 'offscreen.html';

/** 图标红点：插件要画面但抓帧没开时提示用户点一下。 */
function setAttention(on, title) {
  try {
    if (!chrome.action) return;
    chrome.action.setBadgeText({ text: on ? '!' : '' });
    if (on) chrome.action.setBadgeBackgroundColor({ color: '#f97316' });
    chrome.action.setTitle({ title: title || '一起看位置桥 (Alife)' });
  } catch { /* 忽略 */ }
}

let captureTabId = null;
let captureNote = '';
let lastGrabAt = 0;
let frameCount = 0;
let lastFrameAt = 0;

// 多标签择优：最近一个「正在播」的标签页
let lastPlayingTabId = null;
let lastPlayingAt = 0;

// 音频切片计数（录音诊断）
let audioChunks = 0;
let audioLastAt = 0;
let audioOnlyActive = false;   // 纯录音流（ASR 与抓帧解耦）

async function hasOffscreen() {
  try {
    if (!chrome.runtime.getContexts) return false;
    const contexts = await chrome.runtime.getContexts({ contextTypes: ['OFFSCREEN_DOCUMENT'] });
    return contexts.length > 0;
  } catch {
    return false;
  }
}

// ── 录制器标签页：替代 offscreen 文档 ──────────────────────
// Edge 的 offscreen 文档在 tabCapture getUserMedia 时会原生崩溃（渲染进程直接死，
// JS 兜底全部失效，trace 已实证）。普通扩展标签页走的是正常渲染进程，不触发该 bug。
let recorderTabId = null;

async function ensureRecorder() {
  if (recorderTabId != null) {
    try {
      await chrome.tabs.get(recorderTabId);
      return true;
    } catch {
      recorderTabId = null;   // 页面被用户关了，下次重建
    }
  }

  try {
    const tab = await chrome.tabs.create({
      url: chrome.runtime.getURL("recorder.html"),
      active: false
    });
    recorderTabId = tab.id;
    await new Promise((r) => setTimeout(r, 700));   // 等页面脚本就绪
    return true;
  } catch (e) {
    captureNote = "录制器页面创建失败：" + ((e && e.message) || e);
    return false;
  }
}

async function sendToRecorder(payload) {
  if (!(await ensureRecorder())) return { ok: false, error: captureNote };
  try {
    return await chrome.tabs.sendMessage(recorderTabId, payload);
  } catch (e) {
    recorderTabId = null;   // 发送失败视为页面失效
    return { ok: false, error: String((e && e.message) || e) };
  }
}

chrome.tabs.onRemoved.addListener((tabId) => {
  if (tabId === recorderTabId) recorderTabId = null;
});

async function ensureOffscreen() {
  if (await hasOffscreen()) return true;

  try {
    await chrome.offscreen.createDocument({
      url: OFFSCREEN_PATH,
      reasons: ['USER_MEDIA'],
      justification: '截取标签页当前画面，供本机「一起看」陪看使用'
    });
    return true;
  } catch (e) {
    captureNote = `离屏文档创建失败：${(e && e.message) || e}`;
    return false;
  }
}

/** 开始抓画面：必须在目标视频标签页处于激活状态时调用（tabCapture 的限制）。 */
async function startCapture(tabId) {
  trace("bg.startCapture 进入 tabId=" + tabId);
  if (tabId == null) {
    captureNote = '没有可用的标签页';
    return { ok: false, error: captureNote };
  }

  if (!(await ensureOffscreen())) return { ok: false, error: captureNote };

  try {
    const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tabId });
    trace("bg.streamId 拿到");
    const opts = await chrome.storage.local.get({
      frameThreshold: 6,
      frameMaxWidth: 1280,
      frameQuality: 72
    });

    const result = await sendToRecorder({
      target: 'offscreen',
      type: 'offscreen-start',
      streamId,
      threshold: Number(opts.frameThreshold) || 6,
      maxWidth: Number(opts.frameMaxWidth) || 1280,
      quality: Number(opts.frameQuality) || 72
    });

    trace("bg.offscreen-start 回包: " + JSON.stringify(result && { ok: result.ok, error: result.error }));

    if (!result || !result.ok) {
      captureNote = (result && result.error) || '离屏文档没接上';
      return { ok: false, error: captureNote };
    }

    captureTabId = tabId;
    captureNote = '';
    setAttention(false);
    return { ok: true, size: result.size || '' };
  } catch (e) {
    trace("bg.startCapture 异常: " + ((e && e.message) || e));
    captureNote = `抓画面启动失败：${(e && e.message) || e}（要在视频标签页上点）`;
    return { ok: false, error: captureNote };
  }
}

async function startAudioOnly(tabId) {
  if (tabId == null) {
    captureNote = '没有可用的标签页';
    return { ok: false, error: captureNote };
  }

  if (!(await ensureOffscreen())) return { ok: false, error: captureNote };

  try {
    const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tabId });
    const result = await sendToRecorder({
      target: 'offscreen',
      type: 'offscreen-audio-start',
      streamId,
      seconds: lastResult?.asrSeconds || 10
    });

    if (!result || !result.ok) {
      captureNote = (result && result.error) || '纯录音流没接上';
      return { ok: false, error: captureNote };
    }

    audioOnlyActive = true;
    captureNote = '';
    setAttention(false);
    return { ok: true, reused: Boolean(result.reused) };
  } catch (e) {
    captureNote = `纯录音启动失败：${(e && e.message) || e}（要在视频标签页上点）`;
    return { ok: false, error: captureNote };
  }
}

async function stopCapture() {
  try {
    await sendToRecorder({ target: 'offscreen', type: 'offscreen-stop' });
  } catch { /* 离屏可能已经没了 */ }

  captureTabId = null;
  audioOnlyActive = false;
  captureNote = '';
  setAttention(false);
  return { ok: true };
}

/**
 * 抓一帧并上传：
 *   ① 先让页面 canvas 抓（原生分辨率、最准；跨域视频会失败）
 *   ② 失败则用 tabCapture 的离屏通道（一定能抓）
 */
async function grabAndSend(tabId, pos, force) {
  const now = Date.now();
  if (now - lastGrabAt < 8000) return;      // 硬间隔，避免模型连发把带宽打满
  lastGrabAt = now;

  let dataUrl = null;
  let changed = true;

  try {
    const result = await chrome.tabs.sendMessage(tabId, { type: 'grab-canvas', maxWidth: 1280 });
    if (result && result.dataUrl) dataUrl = result.dataUrl;
  } catch { /* 页面没注入或已关闭 */ }

  if (!dataUrl && captureTabId === tabId) {
    try {
      const result = await sendToRecorder({
        target: 'offscreen',
        type: 'offscreen-grab',
        pos,
        force: Boolean(force)
      });

      if (result && result.skip) return;      // 画面没变，不上传
      if (result && result.dataUrl) {
        dataUrl = result.dataUrl;
        changed = result.changed !== false;
      }
    } catch { /* 离屏没起来 */ }
  }

  if (!dataUrl) {
    captureNote = captureNote || '没有可用的抓帧通道（点扩展图标开启「开始抓画面」）';
    return;
  }

  const port = await getPort();
  const sample = lastResult || {};

  try {
    const response = await fetch(`${baseFor(port)}/api/frame`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        image: dataUrl,
        pos: Number(pos) || 0,
        url: sample.url || '',
        title: sample.title || '',
        changed
      })
    });

    if (response.ok) {
      frameCount += 1;
      lastFrameAt = Date.now();
      captureNote = '';
    } else {
      captureNote = `帧被拒：HTTP ${response.status}`;
    }
  } catch {
    captureNote = '上传帧失败（插件没在运行？）';
  }
}

async function sayHello(force = false) {
  if (helloSent && !force) return false;
  helloSent = true;
  const port = await getPort();
  try {
    const r = await fetch(`${baseFor(port)}/api/hello`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ version: chrome.runtime.getManifest().version })
    });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    return true;
  } catch {
    helloSent = false;
    lastResult = {
      ...lastResult,
      ok: false,
      at: Date.now(),
      error: `连不上 127.0.0.1:${port} —— Alife 的「一起看」插件没在运行`,
      port
    };
    return false;
  }
}

void sayHello();

chrome.runtime.onInstalled.addListener(() => {
  void sayHello(true);
});

chrome.runtime.onStartup?.addListener(() => {
  void sayHello(true);
});

// service worker 活着的时候每 5 分钟补一次心跳；被回收也无妨，下次唤醒会再打
setInterval(() => {
  void sayHello(true);
}, 5 * 60 * 1000);

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.port) {
    helloSent = false;
    void sayHello(true);
  }
});

/** 把一段录音切片交给插件转写（位置取最近一次上报，作为切片起点）。 */
async function postAudio(base64) {
  const port = await getPort();

  try {
    await fetch(`${baseFor(port)}/api/audio`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        audio: base64,
        pos: Number(lastResult?.pos) || 0,
        url: lastResult?.url || ''
      })
    });
  } catch {
    // 插件没在跑就算了，下一片再试
  }
}

async function postPosition(payload, tabId) {
  const port = await getPort();
  try {
    const r = await fetch(`${baseFor(port)}/api/position`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload)
    });
    const j = await r.json().catch(() => ({}));

    // 插件可能把播放器命令挂在响应里；交给发这条上报的那个标签页执行。
    if (j?.command && tabId != null) {
      try {
        chrome.tabs.sendMessage(tabId, { type: 'command', command: j.command }).catch(() => {});
      } catch { /* 标签页可能已经关了 */ }
    }

    // 插件请求抓一帧就拍（PPT / 图表讲解类全指望这条通道）
    if (j?.capture && tabId != null) {
      if (captureTabId == null) {
        // 还没开始抓：给图标挂红点，提醒用户点一下（点开弹窗会自动开始）
        captureNote = '插件要画面了，但抓画面还没开始——点扩展图标即可自动开始';
        setAttention(true, '一起看：点这里开始抓画面');
      }

      grabAndSend(tabId, payload?.pos, false).catch(() => {});
    }

    // 插件要求读页面字幕（任意站点通用）→ 通知内容脚本开/关
    if (j && j.caption !== undefined && tabId != null) {
      try {
        chrome.tabs.sendMessage(tabId, {
          type: 'caption',
          enabled: Boolean(j.caption),
          selector: j.captionSelector || ''
        }).catch(() => {});
      } catch { /* 页面可能已经关了 */ }
    }

    // 插件要求语音转写 → 注入主世界录音器（1.4.8）：
    // captureStream 在隔离世界会 KILLED_BAD_MESSAGE，只有 MAIN world 合法。
    // 录音数据由主世界 postMessage → content script 中继 → 这里 → 桥。
    if (j && j.asr !== undefined) {
      lastResult.asrOn = Boolean(j.asr);
      const asrSeconds = Math.max(10, Math.min(60, Number(j.asrSeconds) || 10));
      lastResult.asrSeconds = asrSeconds;

      try {
        chrome.scripting.executeScript({
          target: { tabId },
          world: "MAIN",
          args: [lastResult.asrOn, asrSeconds],
          func: (on, seconds) => {
            if (!window.__wtAsr) {
              window.__wtAsr = { setup: true, rec: null, el: null };
              window.__wtAsr.send = (kind, data) => window.postMessage({ source: "wt-asr-page", kind, data }, "*");
              window.__wtAsr.start = () => {
                try {
                  const v = document.querySelector("video");
                  if (!v) { window.__wtAsr.send("note", "页面上没有视频元素"); return; }
                  if (window.__wtAsr.rec && window.__wtAsr.rec.state === "recording" && window.__wtAsr.el === v) return;
                  if (window.__wtAsr.rec) { try { window.__wtAsr.rec.stop(); } catch (e) { } }

                  const stream = v.captureStream ? v.captureStream() : null;
                  const tracks = stream ? stream.getAudioTracks() : [];
                  if (!tracks.length) { window.__wtAsr.send("note", "captureStream 暂无音轨（视频还没出声？稍后自动重试）"); return; }

                  window.__wtAsr.el = v;
                  let mime = "audio/webm;codecs=opus";
                  if (window.MediaRecorder && MediaRecorder.isTypeSupported && !MediaRecorder.isTypeSupported(mime)) mime = "audio/webm";

                  window.__wtAsr.spawn = () => {
                    const rec = new MediaRecorder(new MediaStream(tracks), { mimeType: mime });
                    rec.ondataavailable = (ev) => {
                      if (!ev.data || ev.data.size < 2048) return;
                      const rd = new FileReader();
                      rd.onloadend = () => {
                        const b64 = String(rd.result || "").split(",")[1] || "";
                        if (b64) window.__wtAsr.send("chunk", b64);
                      };
                      rd.readAsDataURL(ev.data);
                    };
                    rec.onerror = () => { window.__wtAsr.send("note", "页面录音出错"); };
                    rec.onstop = () => {
                      if (window.__wtAsr.wanted && window.__wtAsr.el === v) window.__wtAsr.spawn();
                    };
                    rec.start();
                    window.__wtAsr.rec = rec;
                  };
                  window.__wtAsr.wanted = true;
                  window.__wtAsr.spawn();
                  try { if (window.__wtAsr.cycle) clearInterval(window.__wtAsr.cycle); } catch (e) { }
                  window.__wtAsr.cycle = setInterval(() => {
                    try {
                      const r = window.__wtAsr.rec;
                      if (r && r.state === "recording") r.stop();
                    } catch (e) { }
                  }, (window.__wtAsr.seconds || 10) * 1000);
                } catch (e) {
                  window.__wtAsr.rec = null;
                  window.__wtAsr.send("note", "页面录音启动失败：" + (e && e.message || e));
                }
              };
              window.__wtAsr.stop = () => {
                window.__wtAsr.wanted = false;
                try { if (window.__wtAsr.cycle) clearInterval(window.__wtAsr.cycle); } catch (e) { }
                try { if (window.__wtAsr.rec) window.__wtAsr.rec.stop(); } catch (e) { }
                window.__wtAsr.rec = null;
              };
              window.addEventListener("message", (ev) => {
                const d = ev.data;
                if (d && d.source === "wt-asr-sw") {
                  if (d.cmd === "start") window.__wtAsr.start();
                  if (d.cmd === "stop") window.__wtAsr.stop();
                }
              });
            }
            // 插件可以随时改切片长度（10-60 秒），下次启动生效
            window.__wtAsr.seconds = Math.max(10, Math.min(60, Number(seconds) || 10));
            if (on) window.__wtAsr.start(); else window.__wtAsr.stop();
          }
        }).catch(() => { });
      } catch { /* 标签页可能关了 */ }
    }

    lastResult = {
      ok: Boolean(r.ok && j?.ok),
      at: Date.now(),
      error: r.ok ? (j?.ok ? null : '服务端拒收（位置不是数字？）') : `HTTP ${r.status}`,
      pos: payload?.pos ?? null,
      title: payload?.title ?? null,
      url: payload?.url ?? null,
      port,
      framesWanted: Boolean(j?.frames),
      // 关键：asrOn 必须带进新对象——上面 if 里设的会被这里整个覆盖掉，
      // audio-chunk 处理器靠它判断要不要上传，丢了就是「已收 0 片」
      asrOn: Boolean(j?.asr),
      // 关键：asrSeconds 也要带进新对象，audio 分段长度靠它
      asrSeconds: Math.max(10, Math.min(60, Number(j?.asrSeconds) || 10))
    };
  } catch {
    lastResult = {
      ok: false,
      at: Date.now(),
      error: `连不上 127.0.0.1:${port} —— Alife 的「一起看」插件没在运行`,
      pos: payload?.pos ?? null,
      title: payload?.title ?? null,
      port
    };
  }
  return lastResult;
}

// 消息处理器统一包裹：单个消息出错不影响扩展存活
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  try {
    return handleMessage(msg, sender, sendResponse);
  } catch (e) {
    try { console.error("[一起看] onMessage 异常:", msg && msg.type, e && (e.stack || e.message || e)); } catch { }
    try { sendResponse && sendResponse({ ok: false, error: String((e && e.message) || e) }); } catch { }
    return false;
  }
});

// 面包屑：keepalive fetch 让请求不随上下文死亡，崩在一步一目了然
function trace(step) {
  try {
    fetch("http://127.0.0.1:9377/api/trace", {
      method: "POST",
      body: JSON.stringify({ step }),
      headers: { "Content-Type": "application/json" },
      keepalive: true
    }).catch(() => { });
  } catch { }
}

function handleMessage(msg, _sender, sendResponse) {
  // 发给离屏文档的消息不归这里管（SW 自己发的消息不会回到自己，但保险）
  if (msg?.target === 'offscreen') return false;

  if (msg?.type === 'audio-chunk') {
    audioChunks += 1;
    audioLastAt = Date.now();
    if (lastResult?.asrOn) postAudio(msg.data).catch(() => {});
    return false;
  }

  if (msg?.type === 'audio-note') {
    // 录音层的问题（无音轨/启动失败/offscreen 异常）透传到抓帧状态与弹窗红条
    if (msg.note) {
      captureNote = '录音：' + msg.note;
      recordSwError('offscreen', msg.note);
    }
    return false;
  }

  if (msg?.type === 'startAudio') {
    startAudioOnly(msg.tabId).then(sendResponse);
    return true;
  }

  if (msg?.type === 'startCapture') {
    startCapture(msg.tabId).then(sendResponse);
    return true;
  }
  if (msg?.type === 'stopCapture') {
    stopCapture().then(sendResponse);
    return true;
  }
  if (msg?.type === 'captureStatus') {
    sendResponse({
      active: captureTabId != null || audioOnlyActive,
      audioOnly: audioOnlyActive,
      wantFrames: lastResult ? lastResult.framesWanted !== false : true,
      asrOn: Boolean(lastResult && lastResult.asrOn),
      swError: lastSwError || "",
      tabId: captureTabId,
      note: captureNote,
      frames: frameCount,
      lastAt: lastFrameAt,
      audio: { chunks: audioChunks, lastAt: audioLastAt }
    });
    return false;
  }

  if (msg?.type === 'position') {
    // ── 多标签页择优 ──────────────────────────────────────────
    // 每个开了视频的标签页都会每秒上报；插件只记「最后到的」。
    // 首页/挂着的预览视频（暂停、pos=0）会把真正在看的那个标签页顶掉——
    // 表现就是「标题是首页默认标题、永远 00:00 暂停」。
    // 规则：暂停的上报只有在「10 秒内没有任何标签页在播」时才放行。
    const tabId = _sender?.tab?.id;
    const payload = msg.payload || {};
    const now = Date.now();

    if (payload.paused === true && lastPlayingTabId != null && tabId !== lastPlayingTabId
        && now - lastPlayingAt < 10000) {
      // 有别的标签页正在播：这份暂停上报直接丢掉
      sendResponse({ ok: true, dropped: true });
      return false;
    }

    if (payload.paused !== true) {
      lastPlayingTabId = tabId;
      lastPlayingAt = now;
    }

    postPosition(payload, tabId).then(sendResponse);
    return true;                    // 异步回包，必须返回 true 让通道留着
  }
  if (msg?.type === 'status') {
    sendResponse(lastResult);
    return false;
  }
  if (msg?.type === 'hello') {
    sayHello(true).then((ok) => sendResponse({ ok }));
    return true;
  }
  if (msg?.type === 'setPort') {
    const port = Number(msg.port);
    if (!Number.isFinite(port) || port <= 0 || port > 65535) {
      sendResponse({ ok: false, error: '端口不合法' });
      return false;
    }
    chrome.storage.local.set({ port }, () => {
      helloSent = false;
      sayHello(true).then((ok) => sendResponse({ ok, port }));
    });
    return true;
  }
  return false;
}
