const BASE_URL = "http://127.0.0.1:48123";
const injectedByTab = new Map();
let bridgeToken = "";
let lastSentKey = "";
let lastSentAt = 0;

function cleanText(raw) {
  return (raw || "").replace(/\s+/g, " ").trim();
}

function stripCodePrefix(title, code) {
  let t = cleanText(title);
  t = t.replace(/^\[[^\]]+\]\s*/, "");
  t = t.replace(/\s*-\s*(Codeforces|Code\s*PTIT|PTIT|VNOJ|LeetCode|HackerRank)\s*$/i, "");
  if (code) {
    const shortCode = code.includes("_") ? code.split("_").pop() : code;
    for (const prefix of [code, shortCode]) {
      if (!prefix) continue;
      const esc = prefix.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
      t = t.replace(new RegExp(`^${esc}\\s*[-:.–—]?\\s*`, "i"), "");
    }
  }
  t = t.replace(/^[A-Z]\d?\.\s+/, "");
  return cleanText(t);
}

function extractFromTab(tab) {
  if (!tab || !tab.url || !tab.active) return null;
  let parsed;
  try {
    parsed = new URL(tab.url);
  } catch {
    return null;
  }
  const host = parsed.hostname.toLowerCase();
  const path = parsed.pathname;
  const rawTitle = tab.title || "";

  if (host.endsWith("code.ptit.edu.vn")) {
    const m = path.match(/\/(?:student\/)?questions?\/([A-Za-z0-9_-]+)/i);
    if (!m) return null;
    const code = m[1].toUpperCase();
    return { problem_code: code, problem_title: stripCodePrefix(rawTitle, code), platform: "PTIT", url: tab.url };
  }

  if (host.endsWith("codeforces.com")) {
    let m = path.match(/\/problemset\/problem\/(\d+)\/([A-Za-z0-9]+)/i);
    if (!m) m = path.match(/\/(?:contest|gym)\/(\d+)\/problem\/([A-Za-z0-9]+)/i);
    if (!m) return null;
    const code = `CF_${m[1]}${m[2].toUpperCase()}`;
    let title = stripCodePrefix(rawTitle, `${m[1]}${m[2]}`);
    if (/^Problem\s*-\s*/i.test(rawTitle) && title.toUpperCase() === `${m[1]}${m[2]}`.toUpperCase()) {
      title = "";
    }
    return { problem_code: code, problem_title: title, platform: "Codeforces", url: tab.url };
  }

  if (host.endsWith("oj.vnoi.info")) {
    const m = path.match(/\/problem\/([A-Za-z0-9_-]+)/i);
    if (!m) return null;
    const code = `VNOJ_${m[1]}`;
    return { problem_code: code, problem_title: stripCodePrefix(rawTitle, m[1]), platform: "VNOJ", url: tab.url };
  }

  if (host.endsWith("leetcode.com") || host.endsWith("leetcode.cn")) {
    const m = path.match(/\/problems\/([A-Za-z0-9_-]+)/i);
    if (!m) return null;
    const code = `LC_${m[1]}`;
    return { problem_code: code, problem_title: stripCodePrefix(rawTitle, m[1]), platform: "LeetCode", url: tab.url };
  }

  if (host.endsWith("hackerrank.com")) {
    const m = path.match(/\/challenges\/([A-Za-z0-9_-]+)/i);
    if (!m) return null;
    const code = `HR_${m[1]}`;
    return { problem_code: code, problem_title: stripCodePrefix(rawTitle, m[1]), platform: "HackerRank", url: tab.url };
  }

  return null;
}

async function ensureToken() {
  if (bridgeToken) return bridgeToken;
  try {
    const resp = await fetch(`${BASE_URL}/handshake`, { method: "GET" });
    if (resp.ok) {
      const data = await resp.json();
      if (data && data.token) {
        bridgeToken = data.token;
        return bridgeToken;
      }
    }
  } catch {}
  return "";
}

async function postToApp(payload, tabActive) {
  if (!payload || !payload.problem_code || !tabActive) return;

  const now = Date.now();
  const key = `${payload.platform}|${payload.problem_code}|${payload.problem_title}|${payload.url}`;
  if (key === lastSentKey && now - lastSentAt < 15000) {
    return;
  }

  const token = await ensureToken();
  if (!token) return;

  const body = {
    problem_code: payload.problem_code,
    problem_title: payload.problem_title || "",
    platform: payload.platform || "",
    url: payload.url || "",
    tab_active: true,
    timestamp: now
  };

  try {
    const resp = await fetch(`${BASE_URL}/context`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-GhostCode-Token": token
      },
      body: JSON.stringify(body)
    });
    if (resp.status === 401) {
      // App restarted with a new token; refresh token and retry once
      bridgeToken = "";
      const fresh = await ensureToken();
      if (fresh) {
        await fetch(`${BASE_URL}/context`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "X-GhostCode-Token": fresh
          },
          body: JSON.stringify(body)
        });
      }
    }
    if (resp.ok) {
      lastSentKey = key;
      lastSentAt = now;
    }
  } catch {}
}

async function processTab(tab) {
  if (!tab || !tab.active) return;
  const ctx = extractFromTab(tab);
  if (!ctx) return;

  await postToApp(ctx, true);

  if (tab.id && injectedByTab.get(tab.id) !== tab.url) {
    injectedByTab.set(tab.id, tab.url);
    try {
      await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        files: ["content.js"]
      });
    } catch {}
  }
}

async function scanFocusedActiveTab() {
  try {
    const active = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    for (const tab of active) {
      if (extractFromTab(tab)) {
        await processTab(tab);
        return;
      }
    }
  } catch {}
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.type === "AUTONAME_CONTEXT" && msg.payload) {
    // Only accept messages from an actually active tab
    const isActive = Boolean(sender && sender.tab && sender.tab.active);
    if (!isActive) {
      sendResponse({ ok: false, ignored: "background_tab" });
      return false;
    }
    postToApp(msg.payload, true).then(() => sendResponse({ ok: true }));
    return true;
  }
});

chrome.runtime.onInstalled.addListener(() => {
  injectedByTab.clear();
  bridgeToken = "";
  scanFocusedActiveTab();
});

chrome.runtime.onStartup.addListener(() => {
  injectedByTab.clear();
  bridgeToken = "";
  scanFocusedActiveTab();
});

chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  try {
    const tab = await chrome.tabs.get(tabId);
    await processTab(tab);
  } catch {}
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (!tab.active) return;
  if (changeInfo.url || changeInfo.status === "complete" || changeInfo.title) {
    if (changeInfo.url) {
      injectedByTab.delete(tabId);
      lastSentKey = "";
    }
    processTab(tab);
  }
});

if (chrome.windows && chrome.windows.onFocusChanged) {
  chrome.windows.onFocusChanged.addListener(() => {
    scanFocusedActiveTab();
  });
}

setInterval(scanFocusedActiveTab, 5000);
scanFocusedActiveTab();
