(() => {
  if (window.__autonameContentLoaded) {
    if (typeof window.__autonameSyncNow === "function") {
      window.__autonameSyncNow(true);
    }
    return;
  }
  window.__autonameContentLoaded = true;

  const ENDPOINT = "http://127.0.0.1:48123/context";
  let lastUrl = location.href;
  let lastSentKey = "";

  function cleanText(raw) {
    return (raw || "").replace(/\s+/g, " ").trim();
  }

  function stripCodePrefix(title, code) {
    let t = cleanText(title);
    t = t.replace(/^\[[^\]]+\]\s*/, "");
    if (!code) return t;
    const shortCode = code.includes("_") ? code.split("_").pop() : code;
    for (const prefix of [code, shortCode]) {
      if (!prefix) continue;
      const re = new RegExp(`^${prefix.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s*[-:.–—]?\\s*`, "i");
      t = t.replace(re, "");
    }
    t = t.replace(/^[A-Z]\d?\.\s+/, "");
    return cleanText(t);
  }

  function firstText(selectors) {
    for (const sel of selectors) {
      const el = document.querySelector(sel);
      if (el) {
        const txt = cleanText(el.textContent);
        if (txt) return txt;
      }
    }
    return "";
  }

  function extractContext() {
    const host = location.hostname.toLowerCase();
    const path = location.pathname;
    const url = location.href;

    if (host.endsWith("code.ptit.edu.vn")) {
      const m = path.match(/\/(?:student\/)?questions?\/([A-Za-z0-9_-]+)/i);
      if (!m) return null;
      const code = m[1].toUpperCase();
      let title = firstText([
        ".submit__nav p span a",
        ".submit__nav p a",
        ".submit__nav",
        "h3",
        ".title",
        ".card-title",
        "h2",
        "h1"
      ]);
      title = stripCodePrefix(title, code);
      return { problem_code: code, problem_title: title, platform: "PTIT", url };
    }

    if (host.endsWith("codeforces.com")) {
      let m = path.match(/\/problemset\/problem\/(\d+)\/([A-Za-z0-9]+)/i);
      if (!m) m = path.match(/\/(?:contest|gym)\/(\d+)\/problem\/([A-Za-z0-9]+)/i);
      let code = m ? `CF_${m[1]}${m[2].toUpperCase()}` : "";
      let shortIndex = m ? m[2].toUpperCase() : "";

      if (!code && /\/(?:contest|gym)\/(\d+)\/submit/i.test(path)) {
        const cm = path.match(/\/(?:contest|gym)\/(\d+)\/submit/i);
        const sel = document.querySelector('select[name="submittedProblemIndex"]');
        const idx = sel && sel.value ? sel.value.toUpperCase() : "";
        if (cm && idx) {
          code = `CF_${cm[1]}${idx}`;
          shortIndex = idx;
        }
      }
      if (!code) return null;

      let title = firstText([".problem-statement .title", ".problem-statement .header .title", ".title"]);
      title = stripCodePrefix(title, shortIndex);
      return { problem_code: code, problem_title: title, platform: "Codeforces", url };
    }

    if (host.endsWith("oj.vnoi.info")) {
      const m = path.match(/\/problem\/([A-Za-z0-9_-]+)/i);
      if (!m) return null;
      const code = `VNOJ_${m[1]}`;
      let title = firstText([".problem-title", "h2", "h1"]);
      title = stripCodePrefix(title, m[1]);
      return { problem_code: code, problem_title: title, platform: "VNOJ", url };
    }

    if (host.endsWith("leetcode.com") || host.endsWith("leetcode.cn")) {
      const m = path.match(/\/problems\/([A-Za-z0-9_-]+)/i);
      if (!m) return null;
      const code = `LC_${m[1]}`;
      let title = firstText([
        '[data-cy="question-title"]',
        'a[href*="/problems/"].no-underline',
        "div.text-title-large a",
        "h1"
      ]);
      title = title.replace(/^\d+\.\s*/, "");
      return { problem_code: code, problem_title: cleanText(title), platform: "LeetCode", url };
    }

    if (host.endsWith("hackerrank.com")) {
      const m = path.match(/\/challenges\/([A-Za-z0-9_-]+)/i);
      if (!m) return null;
      const code = `HR_${m[1]}`;
      const title = firstText(["h1.ui-icon-label", "h1", ".challenge-title"]);
      return { problem_code: code, problem_title: cleanText(title), platform: "HackerRank", url };
    }

    return null;
  }

  function sendViaBackground(ctx) {
    try {
      if (chrome && chrome.runtime && chrome.runtime.sendMessage) {
        chrome.runtime.sendMessage({ type: "AUTONAME_CONTEXT", payload: ctx }, () => {
          void chrome.runtime.lastError;
        });
        return;
      }
    } catch {}
    fetch(ENDPOINT, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(ctx),
      keepalive: true
    }).catch(() => {});
  }

  function syncContext(force) {
    if (document.visibilityState === "hidden" && !force) return;
    const ctx = extractContext();
    if (!ctx) return;

    const taggedTitle = ctx.problem_title
      ? `[${ctx.problem_code}] ${ctx.problem_title} - ${ctx.platform}`
      : `[${ctx.problem_code}] - ${ctx.platform}`;
    if (document.title !== taggedTitle) {
      document.title = taggedTitle;
    }

    const key = `${ctx.problem_code}|${ctx.problem_title}|${ctx.url}`;
    if (!force && key === lastSentKey) return;
    lastSentKey = key;

    sendViaBackground(ctx);
  }

  window.__autonameSyncNow = syncContext;

  syncContext(true);

  setInterval(() => {
    const changed = location.href !== lastUrl;
    if (changed) lastUrl = location.href;
    syncContext(true);
  }, 4000);

  const obs = new MutationObserver(() => {
    if (location.href !== lastUrl) {
      lastUrl = location.href;
      syncContext(true);
    } else {
      syncContext(false);
    }
  });
  if (document.body) {
    obs.observe(document.body, { childList: true, subtree: true });
  }
})();
