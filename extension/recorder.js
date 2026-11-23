// ── 顶层兜底：offscreen 文档是独立上下文，这里崩了同样会让 Edge 停用扩展 ──
self.addEventListener("error", (e) => {
  const msg = (e && e.message || "unknown") + " @ " + (e && e.filename || "?") + ":" + (e && e.lineno || 0);
  try { asrNoteTo("offscreen 异常：" + msg); } catch { }
  try { console.error("[一起看] offscreen error:", msg); } catch { }
});
self.addEventListener("unhandledrejection", (e) => {
  const r = e && e.reason;
  const msg = String(r && (r.stack || r.message) || r);
  try { asrNoteTo("offscreen 未处理 rejection：" + msg); } catch { }
  try { console.error("[一起看] offscreen rejection:", msg); } catch { }
});

/**
 * offscreen.js — 用 tabCapture 抓当前标签页的画面，按需返回 JPEG。
 *
 * 为什么必须有这一层：
 *   B 站 / YouTube 的视频流在跨域 CDN 上，页面里 canvas.drawImage(video) 会把画布
 *   标记为「被污染」，toDataURL 直接抛 SecurityError —— 页面脚本根本读不到像素。
 *   tabCapture 抓的是「标签页合成后的画面」，不受 CORS 限制，所以它才是可靠通道。
 *
 * MV3 的 service worker 没有 DOM / getUserMedia，所以必须借离屏文档（本页）。
 *
 * 画面变化检测：每次抓帧先缩成 96×54 灰度算平均差异，没超过阈值就不上传，
 * 省带宽也省模型上下文；PPT 翻页 / 切镜会明显超过阈值，必触发。
 */

/* global chrome */

let stream = null;
let aStream = null;   // 纯录音流（ASR 与抓帧解耦时的独立音源）
let video = null;
let canvas = null;
let ctx = null;
let small = null;
let smallCtx = null;
let lastSignature = null;

let settings = { threshold: 6, maxWidth: 1280, quality: 72 };

// ── ASR：录标签页声音，按片发给插件转写 ──
let recorder = null;
let asrOn = false;
let asrSeconds = 10;

// 诊断：为什么不录音 / 录了多少片（popup 与状态可见）
let asrNote = '';      // 最近一次失败/跳过原因
let asrChunks = 0;     // 已发给插件的切片数
let asrLastAt = 0;     // 最近一片时间

function asrNoteTo(msg) {
  asrNote = String(msg || '');
  try { chrome.runtime.sendMessage({ type: 'audio-note', note: asrNote }).catch(() => {}); } catch { }
}

function startAsr() {
  if (recorder) return;

  if (!stream && !aStream) {
    asrNoteTo('没有可用的捕获流：点一下扩展图标授权（会按插件意愿开画面/录音）');
    return;
  }

  const tracks = stream && stream.getAudioTracks().length > 0
    ? stream.getAudioTracks()
    : (aStream ? aStream.getAudioTracks() : []);

  if (tracks.length === 0) {
    asrNoteTo('此标签页没有可录的音频轨（视频可能被静音）');
    return;
  }

  try {
    trace("off.startAsr tracks=" + tracks.length);

    let mime = 'audio/webm;codecs=opus';
    if (typeof MediaRecorder.isTypeSupported === 'function' && !MediaRecorder.isTypeSupported(mime)) {
      mime = 'audio/webm';
    }
    trace("off.startAsr mime=" + mime);

    recorder = new MediaRecorder(new MediaStream(tracks), { mimeType: mime });
    asrNote = '';
    trace("off.startAsr recorder 创建完成");

    recorder.ondataavailable = (event) => {
      if (!event.data || event.data.size < 2048) return;

      const reader = new FileReader();
      reader.onloadend = () => {
        const base64 = String(reader.result || '').split(',')[1] || '';
        if (!base64) return;
        asrChunks += 1;
        asrLastAt = Date.now();
        chrome.runtime.sendMessage({ type: 'audio-chunk', data: base64 }).catch(() => {});
      };
      reader.readAsDataURL(event.data);
    };

    recorder.onerror = (e) => {
      asrNoteTo('录音出错：' + (e && e.error ? e.error : 'unknown'));
      try { recorder?.stop(); } catch { }
      recorder = null;
    };

    // 延迟启动：getUserMedia 之后立刻 new/start MediaRecorder 会让 Edge 渲染进程原生崩溃
    // （连 JS catch 都不经过）。拉开 800ms 窗口。
    setTimeout(() => {
      try {
        trace("off.startAsr recorder.start 调用");
        recorder.start(asrSeconds * 1000);
        trace("off.startAsr recorder.start 完成");
      } catch (e) {
        recorder = null;
        asrNoteTo("录音启动失败(延迟)：" + ((e && e.message) || e));
        asrNote = "录音启动失败(延迟)：" + ((e && e.message) || e);
      }
    }, 800);
  } catch (e) {
    recorder = null;
    asrNoteTo('录音启动失败：' + (e && e.message ? e.message : e));
  }
}

function stopAsr() {
  try { recorder?.stop(); } catch { /* 忽略 */ }
  recorder = null;
}

function stop() {
  stopAsr();

  try {
    if (stream) stream.getTracks().forEach((track) => track.stop());
  } catch { /* 忽略 */ }
  try {
    if (aStream) aStream.getTracks().forEach((track) => track.stop());
  } catch { /* 忽略 */ }

  stream = null;
  aStream = null;
  video = null;
  canvas = null;
  ctx = null;
  small = null;
  smallCtx = null;
  lastSignature = null;
}

async function start(msg) {
  try {
    stop();

    settings = {
      threshold: Number(msg.threshold) || 6,
      maxWidth: Number(msg.maxWidth) || 1280,
      quality: Math.min(90, Math.max(55, Number(msg.quality) || 72))
    };

    // 声音一并抓：插件开「语音转写」时用它切片（音频轨不影响播放，也不是额外录音）
    stream = await navigator.mediaDevices.getUserMedia({
      audio: true,
      video: {
        mandatory: {
          chromeMediaSource: 'tab',
          chromeMediaSourceId: msg.streamId
        }
      }
    });
    trace("off.getUserMedia 成功");

    video = document.createElement('video');
    video.muted = true;
    video.playsInline = true;
    video.srcObject = stream;
    await video.play();

    // 等一帧真正到位，否则第一次抓出来是黑图
    await new Promise((resolve) => {
      if (video.videoWidth > 0) return resolve();
      const timer = setTimeout(resolve, 1500);
      video.addEventListener('loadedmetadata', () => {
        clearTimeout(timer);
        resolve();
      }, { once: true });
    });

    canvas = document.createElement('canvas');
    ctx = canvas.getContext('2d');
    small = document.createElement('canvas');
    small.width = 96;
    small.height = 54;
    smallCtx = small.getContext('2d', { willReadFrequently: true });

    if (asrOn) startAsr();

    return {
      ok: true,
      size: `${video.videoWidth || 0}x${video.videoHeight || 0}`,
      audio: stream.getAudioTracks().length
    };
  } catch (e) {
    stop();
    return { ok: false, error: String((e && e.message) || e) };
  }
}

/** 抓一帧：没变化返回 skip，变了返回 dataUrl + changed。 */
function grab(msg) {
  try {
    if (!video || !ctx || video.videoWidth === 0) {
      return { ok: false, error: '画面还没就绪' };
    }

    // ① 96×54 灰度签名，判断「画面变了没有」
    smallCtx.drawImage(video, 0, 0, small.width, small.height);
    const data = smallCtx.getImageData(0, 0, small.width, small.height).data;
    const signature = new Uint8Array(small.width * small.height);

    let sum = 0;
    for (let i = 0, p = 0; i < data.length; i += 4, p++) {
      const gray = (data[i] * 0.299 + data[i + 1] * 0.587 + data[i + 2] * 0.114) | 0;
      signature[p] = gray;
      sum += gray;
    }

    let diff = 0;
    if (lastSignature && lastSignature.length === signature.length) {
      let total = 0;
      for (let i = 0; i < signature.length; i++) {
        total += Math.abs(signature[i] - lastSignature[i]);
      }
      diff = total / signature.length;
    } else {
      diff = 255;   // 第一帧一定上传
    }

    const force = Boolean(msg && msg.force);
    const changed = diff >= settings.threshold;

    if (!changed && !force) {
      return { skip: true, diff };
    }

    lastSignature = signature;

    // ② 按最大宽度缩放后导出 JPEG
    const vw = video.videoWidth;
    const vh = video.videoHeight;
    const scale = vw > settings.maxWidth ? settings.maxWidth / vw : 1;
    const w = Math.max(2, Math.round(vw * scale));
    const h = Math.max(2, Math.round(vh * scale));

    canvas.width = w;
    canvas.height = h;
    ctx.drawImage(video, 0, 0, w, h);

    const dataUrl = canvas.toDataURL('image/jpeg', settings.quality / 100);

    return { ok: true, dataUrl, changed: true, diff: Math.round(diff), size: `${w}x${h}`, avg: Math.round(sum / signature.length) };
  } catch (e) {
    return { ok: false, error: String((e && e.message) || e) };
  }
}

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

chrome.runtime.onMessage.addListener((msg, _sender, respond) => {
  if (!msg || msg.target !== 'offscreen') return false;

  if (msg.type === 'offscreen-start') {
    trace("off.start 收到");
    start(msg).then((r) => { trace("off.start 完成 ok=" + (r && r.ok)); respond(r); })
      .catch((e) => { trace("off.start 抛错: " + ((e && e.message) || e)); respond({ ok: false, error: String((e && e.message) || e) }); });
    return true;
  }

  if (msg.type === 'offscreen-grab') {
    respond(grab(msg));
    return false;
  }

  if (msg.type === 'offscreen-asr') {
    asrOn = Boolean(msg.on);
    if (msg.seconds) asrSeconds = Math.max(10, Math.min(60, Number(msg.seconds) || 20));

    if (asrOn) startAsr(); else stopAsr();
    respond({
      ok: true, on: asrOn, seconds: asrSeconds, recording: recorder !== null,
      chunks: asrChunks, lastAt: asrLastAt, note: asrNote
    });
    return false;
  }

  if (msg.type === 'audio-stats') {
    respond({
      recording: recorder !== null,
      chunks: asrChunks,
      lastAt: asrLastAt,
      note: asrNote || (asrOn && recorder === null && stream ? '尚未开始录音' : '')
    });
    return false;
  }

  if (msg.type === 'offscreen-audio-start') {
    // 插件按配置传切片长度（10-60 秒）
    if (msg.seconds) asrSeconds = Math.max(10, Math.min(60, Number(msg.seconds) || 10));
    // ASR 与抓帧解耦：只开一条音频 tabCapture 流，不建画面管线
    (async () => {
      try {
        if (stream) {                      // 抓帧流已在：它的音轨直接用
          asrOn = true;
          startAsr();
          respond({ ok: true, reused: true, chunks: asrChunks, note: asrNote });
          return;
        }

        const media = await navigator.mediaDevices.getUserMedia({
          audio: {
            mandatory: { chromeMediaSource: 'tab', chromeMediaSourceId: msg.streamId }
          }
        });
        aStream = media;
        asrOn = true;
        asrNote = '';
        startAsr();
        respond({ ok: true, audio: media.getAudioTracks().length, chunks: asrChunks, note: asrNote });
      } catch (e) {
        asrNoteTo('纯录音流启动失败：' + (e && e.message ? e.message : e));
        respond({ ok: false, error: String(e && e.message || e) });
      }
    })();
    return true;
  }

  if (msg.type === 'offscreen-stop') {
    stop();
    respond({ ok: true });
    return false;
  }

  return false;
});
