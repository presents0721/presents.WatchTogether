/**
 * content.js — 在你正在看的那个页面里读播放进度，交给后台去上报。
 *
 * 为什么不在页面里直接 fetch：页面是 https 公网站点，目标是 http://127.0.0.1，
 * 跨域 + 私有网络访问有两道闸门。扩展后台声明了 host_permissions，不受这两道限制。
 * 所以这里只负责「读」，发出去交给 background.js。
 *
 * 读什么：<video> 的 currentTime / duration / paused / ended。
 * 这是页面里的真值 —— 播放器自己就是靠它在走。
 *
 * 移植自 SAOS-3624/dsh-watch-together 的 extension/content.js（Apache-2.0）。
 */

/* global chrome */

(() => {
  // 有些站点在一个页面里放多个 video（广告、预览、小窗）。
  // 挑「最像正在看的那个」：优先在播的，其次时长长的。
  function pickVideo() {
    const all = [...document.querySelectorAll('video')];
    if (all.length === 0) return null;
    if (all.length === 1) return all[0];
    const playing = all.filter((v) => !v.paused && !v.ended);
    const pool = playing.length > 0 ? playing : all;
    return pool.reduce((best, v) => {
      const score = (v) => (Number.isFinite(v.duration) ? v.duration : 0);
      return score(v) > score(best) ? v : best;
    }, pool[0]);
  }

  // ── 页面字幕（实验，默认关） ─────────────────────────────
  // 只在用户于 popup 里开启后，才读视频字幕元素；不读正文、评论或私信。
  const CAPTION_SELECTORS = [
    '.ytp-caption-segment',
    '.bpx-player-subtitle-panel-text',
    '.bpx-player-subtitle',
    '.captions-text',
    '[class*="caption"]'
  ];

  let captionEnabled = false;
  try {
    chrome.storage.local.get({ caption: false }, (value) => {
      captionEnabled = Boolean(value && value.caption);
    });
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area === 'local' && changes.caption) {
        captionEnabled = Boolean(changes.caption.newValue);
      }
    });
  } catch {
    // 扩展上下文失效时保持关闭
  }

  // 插件按需下发：陪看模块开着「自动读取页面字幕」时，会对任意站点要求开字幕
  // （用户 popup 里的开关仍然管用——插件要求优先）。
  let captionWanted = null;      // null = 听 popup；true/false = 插件要求
  let customSelector = "";

  function captionOn() {
    return captionWanted === null ? captionEnabled : captionWanted;
  }

  function readCaption() {
    if (!captionOn()) return null;
    try {
      // 自定义选择器优先（某些站点类名很怪，自动认不出来）
      const selectors = customSelector ? [customSelector, ...CAPTION_SELECTORS] : CAPTION_SELECTORS;

      for (const selector of selectors) {
        const nodes = document.querySelectorAll(selector);
        for (const node of nodes) {
          if (!(node instanceof HTMLElement)) continue;
          const style = window.getComputedStyle(node);
          if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) continue;
          const text = (node.innerText || node.textContent || '').replace(/\s+/g, ' ').trim();
          if (text.length > 0 && text.length <= 300) return text;
        }
      }
    } catch {
      // 页面结构变化不影响位置上报
    }
    return null;
  }

  /**
   * 只在这个标签页可见的时候上报。
   * 为什么：每个标签页都会跑这份脚本。两个带视频的标签页同时上报，服务端收到的位置会来回跳，
   * 陪看会以为用户在疯狂拖进度条。当前可见的那个才是「正在看的」。
   */
  let lastReportAt = 0;

  function report(force) {
    if (!chrome?.runtime?.id) return;      // 扩展被重新加载/停用了

    // 后台标签页也要报：用户经常「把视频页放后台听」。
    // Chrome 会把后台标签页的 setInterval 节流到 1 次/分钟——所以光靠定时器会断档，
    // 靠 timeupdate（媒体事件，不受节流）兜住节奏。
    if (!force && Date.now() - lastReportAt < 800) return;

    const v = pickVideo();
    if (!v || !Number.isFinite(v.currentTime)) return;

    const payload = {
      pos: v.currentTime,
      dur: Number.isFinite(v.duration) ? v.duration : null,
      title: document.title || '',
      url: location.href,
      paused: Boolean(v.paused),
      ended: Boolean(v.ended),
      caption: readCaption(),
    };

    try {
      // 不 await：上报失败不该在页面控制台里刷红字
      lastReportAt = Date.now();
      chrome.runtime.sendMessage({ type: 'position', payload }).catch(() => {});
    } catch { /* 扩展上下文失效，下次再说 */ }
  }


  // ── 主世界 ASR 中继（1.4.8）：captureStream 必须在主世界跑（隔离世界会 KILLED_BAD_MESSAGE）──
  // 后台用 chrome.scripting.executeScript(world:'MAIN') 注入录音器；录音数据经 postMessage 回来，
  // 这里只做转发：主世界 → chrome.runtime → 插件桥。
  window.addEventListener("message", (ev) => {
    const d = ev.data;
    if (!d || d.source !== "wt-asr-page") return;

    if (d.kind === "chunk" && d.data) {
      try { chrome.runtime.sendMessage({ type: "audio-chunk", data: d.data }).catch(() => { }); } catch { }
    } else if (d.kind === "note" && d.data) {
      try { chrome.runtime.sendMessage({ type: "audio-note", note: d.data }).catch(() => { }); } catch { }
    }
  });

  // 1) 每秒一次心跳（前台；后台会被节流成 1 次/分钟，靠下面的 timeupdate 补节奏）
  setInterval(report, 1000);

  // 2) 媒体事件驱动：播放中约 4 次/秒，且在后台标签页照常触发（不被节流）
  //    —— 这是后台「听视频」场景的主要上报通道
  document.addEventListener('timeupdate', () => report(false), true);

  // 3) 关键动作立刻报一次：暂停/拖动/切集之后位置会跳，等下一拍就晚了
  for (const ev of ['play', 'pause', 'seeked', 'ratechange', 'ended', 'loadedmetadata']) {
    document.addEventListener(ev, () => report(true), true);
  }

  // 4) 单页应用切视频不刷新页面，视频元素可能后来才出现
  document.addEventListener('DOMContentLoaded', () => report(true), true);

  // 4) 切回这个标签页的瞬间立刻报一次：换片检测靠的就是这一拍
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') report();
  });

  // 6) 页面内 canvas 抓帧：能读就用（画质按视频原生分辨率，最准）。
  //    B 站 / YouTube 的视频流跨域，canvas 会被「污染」，toDataURL 抛 SecurityError ——
  //    这时返回 null，后台会自动切到 tabCapture 通道（offscreen.js）。
  // 8) 探一下「这个页面有没有正在播的视频」——popup 用它决定要不要自动开抓画面
  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type !== 'probe') return false;

    try {
      const v = pickVideo();
      sendResponse({
        video: Boolean(v),
        playing: Boolean(v && !v.paused && v.readyState >= 2),
        duration: v && Number.isFinite(v.duration) ? v.duration : 0
      });
    } catch {
      sendResponse({ video: false, playing: false, duration: 0 });
    }

    return false;
  });

  // 7) 插件按需开关字幕读取（任意站点通用；selector 空 = 用内置常见选择器）
  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type !== 'caption') return false;

    captionWanted = Boolean(msg.enabled);
    customSelector = String(msg.selector || '').trim();
    sendResponse({ ok: true, on: captionWanted, selector: customSelector });
    return false;
  });

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type !== 'grab-canvas') return false;

    try {
      const v = pickVideo();
      if (!v || !v.videoWidth) {
        sendResponse({ dataUrl: null, error: 'no-video' });
        return false;
      }

      const maxWidth = Math.min(1600, Number(msg.maxWidth) || 1280);
      const scale = v.videoWidth > maxWidth ? maxWidth / v.videoWidth : 1;
      const w = Math.max(2, Math.round(v.videoWidth * scale));
      const h = Math.max(2, Math.round(v.videoHeight * scale));

      const c = document.createElement('canvas');
      c.width = w;
      c.height = h;
      const g = c.getContext('2d');
      g.drawImage(v, 0, 0, w, h);

      const dataUrl = c.toDataURL('image/jpeg', 0.72);
      sendResponse({ dataUrl, size: `${w}x${h}` });
    } catch (e) {
      // 画布被污染（跨域视频）——这是预期内的，交给 tabCapture
      sendResponse({ dataUrl: null, error: String(e && e.name || e) });
    }

    return false;
  });

  // 5) 插件请求的播放器命令：暂停 / 继续 / 回退 / 跳到。
  //    只控制这个页面里正在播的那个 <video>，不碰页面其他内容。
  chrome.runtime.onMessage.addListener((msg) => {
    if (msg?.type !== 'command' || !msg.command) return;
    const v = pickVideo();
    if (!v) return;

    const command = msg.command;
    try {
      if (command.action === 'pause') {
        v.pause();
      } else if (command.action === 'play') {
        const playing = v.play();
        if (playing && playing.catch) playing.catch(() => {});
      } else if (command.action === 'seekBack' || command.action === 'seekTo') {
        const raw = command.action === 'seekBack'
          ? v.currentTime - Number(command.value || 10)
          : Number(command.value || 0);
        const duration = Number.isFinite(v.duration) ? v.duration : raw;
        v.currentTime = Math.min(Math.max(0, raw), duration);
      }
    } catch {
      // 播放器不允许操作时忽略，下次上报照旧
    }
    report();
  });

  report();
})();
