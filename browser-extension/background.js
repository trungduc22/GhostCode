const DEFAULT_PORT = 48123;
const EXT_VERSION = "1.4.0";
const KEEPALIVE_ALARM = "ghostcode-keepalive";
const injectedByTab = new Map();
let activePort = DEFAULT_PORT;
let configuredBrowserName = "";
let sessionToken = "";
let sessionExpiresAt = 0;
let lastSentKey = "";
let lastSentAt = 0;

function detectBrowserName() {
  if (configuredBrowserName) return configuredBrowserName;
  try {
    const ua = (navigator.userAgent || "").toLowerCase();
    const brands = Array.isArray(navigator.userAgentData?.brands)
      ? navigator.userAgentData.brands.map((b) => String(b.brand || "").toLowerCase()).join(" ")
      : "";
    const combined = `${ua} ${brands}`;
    if (combined.includes("thorium")) return "Thorium";
    if (combined.includes("edg/") || combined.includes("edge")) return "Edge";
    if (combined.includes("firefox")) return "Firefox";
    if (combined.includes("chrome")) return "Chrome";
  } catch {}
  return "Chrome";
}

function buildBridgeHeaders(extra = {}) {
  return {
    "Content-Type": "application/json",
    "X-GhostCode-Browser": detectBrowserName(),
    "X-GhostCode-Ext-Version": EXT_VERSION,
    ...extra
  };
}

function cleanText(raw) {
  return (raw || "").replace(/\s+/g, " ").trim();
}

function isDomainMatch(host, domain) {
  return host === domain || host.endsWith("." + domain);
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
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return null;
  const host = parsed.hostname.toLowerCase();
  const path = parsed.pathname;
  const rawTitle = tab.title || "";

  if (isDomainMatch(host, "code.ptit.edu.vn")) {
    const m = path.match(/\/(?:student\/)?questions?\/([A-Za-z0-9_-]+)/i);
    if (!m) return null;
    const code = m[1].toUpperCase();
    return { problem_code: code, problem_title: stripCodePrefix(rawTitle, code), platform: "PTIT", url: tab.url };
  }

  if (isDomainMatch(host, "codeforces.com")) {
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

  if (isDomainMatch(host, "oj.vnoi.info")) {
    const m = path.match(/\/problem\/([A-Za-z0-9_-]+)/i);
    if (!m) return null;
    const code = `VNOJ_${m[1]}`;
    return { problem_code: code, problem_title: stripCodePrefix(rawTitle, m[1]), platform: "VNOJ", url: tab.url };
  }

  if (isDomainMatch(host, "leetcode.com") || isDomainMatch(host, "leetcode.cn")) {
    const m = path.match(/\/problems\/([A-Za-z0-9_-]+)/i);
    if (!m) return null;
    const code = `LC_${m[1]}`;
    return { problem_code: code, problem_title: stripCodePrefix(rawTitle, m[1]), platform: "LeetCode", url: tab.url };
  }

  if (isDomainMatch(host, "hackerrank.com")) {
    const m = path.match(/\/challenges\/([A-Za-z0-9_-]+)/i);
    if (!m) return null;
    const code = `HR_${m[1]}`;
    return { problem_code: code, problem_title: stripCodePrefix(rawTitle, m[1]), platform: "HackerRank", url: tab.url };
  }

  return null;
}

async function computeHmacHex(secret, message) {
  const enc = new TextEncoder();
  const key = await crypto.subtle.importKey(
    "raw",
    enc.encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  const sig = await crypto.subtle.sign("HMAC", key, enc.encode(message));
  return Array.from(new Uint8Array(sig))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

async function tryNativeHandshake() {
  if (!chrome.runtime || !chrome.runtime.sendNativeMessage) return "";
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendNativeMessage("com.ghostcode.bridge", { type: "handshake" }, (resp) => {
        if (chrome.runtime.lastError || !resp) {
          resolve("");
          return;
        }
        if (resp.port) activePort = resp.port;
        if (resp.browser_name) configuredBrowserName = resp.browser_name;
        if (!resp.session_token) {
          resolve("");
          return;
        }
        sessionToken = resp.session_token;
        sessionExpiresAt = Date.now() + 240_000;
        resolve(sessionToken);
      });
    } catch {
      resolve("");
    }
  });
}

async function loadLocalPairing() {
  try {
    const url = chrome.runtime.getURL("pairing.generated.json");
    const resp = await fetch(url);
    if (!resp.ok) return null;
    const data = await resp.json();
    if (!data || !data.pairing_secret || !data.pairing_id) return null;
    if (data.port) activePort = data.port;
    if (data.browser_name) configuredBrowserName = data.browser_name;
    return data;
  } catch {
    return null;
  }
}

async function ensureToken(forceRefresh = false) {
  if (!forceRefresh && sessionToken && Date.now() < sessionExpiresAt) {
    return sessionToken;
  }

  const pairing = await loadLocalPairing();
  const nativeTok = await tryNativeHandshake();
  if (nativeTok) return nativeTok;

  if (!pairing) return "";

  const nonceBytes = crypto.getRandomValues(new Uint8Array(16));
  const nonce = Array.from(nonceBytes).map((b) => b.toString(16).padStart(2, "0")).join("");
  const timestamp = Date.now();
  const proof = await computeHmacHex(pairing.pairing_secret, `${nonce}:${timestamp}`);

  try {
    const resp = await fetch(`http://127.0.0.1:${activePort}/handshake`, {
      method: "POST",
      headers: buildBridgeHeaders({
        "X-GhostCode-Pairing-Id": pairing.pairing_id,
        "X-GhostCode-Nonce": nonce,
        "X-GhostCode-Timestamp": String(timestamp),
        "X-GhostCode-Proof": proof
      }),
      body: "{}"
    });
    if (resp.ok) {
      const data = await resp.json();
      if (data && data.session_token) {
        sessionToken = data.session_token;
        const ttlMs = (data.expires_in || 300) * 1000;
        sessionExpiresAt = Date.now() + Math.max(30_000, ttlMs - 15_000);
        return sessionToken;
      }
    }
  } catch {}
  return "";
}

async function postAuthenticated(path, bodyObj) {
  let token = await ensureToken(false);
  if (!token) return null;
  const serialized = JSON.stringify(bodyObj ?? {});
  try {
    let resp = await fetch(`http://127.0.0.1:${activePort}${path}`, {
      method: "POST",
      headers: buildBridgeHeaders({ "X-GhostCode-Token": token }),
      body: serialized
    });
    if (resp.status === 401) {
      token = await ensureToken(true);
      if (token) {
        resp = await fetch(`http://127.0.0.1:${activePort}${path}`, {
          method: "POST",
          headers: buildBridgeHeaders({ "X-GhostCode-Token": token }),
          body: serialized
        });
      }
    }
    return resp;
  } catch {
    return null;
  }
}

async function sendHeartbeat() {
  await postAuthenticated("/heartbeat", {
    browser_name: detectBrowserName(),
    ext_version: EXT_VERSION,
    timestamp: Date.now()
  });
}

async function clearContextInApp() {
  if (!lastSentKey) return;
  lastSentKey = "";
  lastSentAt = 0;
  await postAuthenticated("/context/clear", {});
}

async function postToApp(payload, tabActive) {
  if (!payload || !payload.problem_code || !payload.url || !tabActive) return;

  const now = Date.now();
  const key = `${payload.platform}|${payload.problem_code}|${payload.problem_title}|${payload.url}`;
  if (key === lastSentKey && now - lastSentAt < 15000) {
    return;
  }

  const body = {
    problem_code: payload.problem_code,
    problem_title: payload.problem_title || "",
    platform: payload.platform || "",
    url: payload.url || "",
    tab_active: true,
    timestamp: now,
    browser_name: detectBrowserName()
  };

  const resp = await postAuthenticated("/context", body);
  if (resp && resp.ok) {
    lastSentKey = key;
    lastSentAt = now;
  }
}

async function processTab(tab) {
  if (!tab || !tab.active) return;
  const ctx = extractFromTab(tab);
  if (!ctx) {
    if (tab.url && /^https?:\/\//i.test(tab.url)) {
      await clearContextInApp();
    }
    return;
  }

  await postToApp(ctx, true);

  if (tab.id && injectedByTab.get(tab.id) !== tab.url) {
    injectedByTab.set(tab.id, tab.url);
    try {
      await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        files: ["content.js"]
      });
    } catch {}
  } else if (tab.id) {
    try {
      chrome.tabs.sendMessage(tab.id, { type: "GHOSTCODE_RESCAN_PROBLEM" }, () => {
        void chrome.runtime.lastError;
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
      // If active tab is chrome://extensions or non-problem URL, check active problem tabs in other windows first
      const allActive = await chrome.tabs.query({ active: true });
      const problemTab = allActive.find((t) => extractFromTab(t) !== null);
      if (problemTab) {
        await processTab(problemTab);
        return;
      }
      await processTab(tab);
      return;
    }
  } catch {}
}

function ensureKeepaliveAlarm() {
  try {
    if (chrome.alarms && chrome.alarms.create) {
      chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: 0.4 });
    }
  } catch {}
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || !msg.payload) return false;
  const isActive = Boolean(sender && sender.tab && sender.tab.active);
  if (!isActive) {
    sendResponse({ ok: false, ignored: "background_tab" });
    return false;
  }

  if (msg.type === "AUTONAME_CONTEXT") {
    postToApp(msg.payload, true).then(() => sendResponse({ ok: true }));
    return true;
  }
  if (msg.type === "GHOSTCODE_PROBLEM") {
    postAuthenticated("/problem", { ...msg.payload, tab_active: true, browser_name: detectBrowserName() })
      .then(() => sendResponse({ ok: true }));
    return true;
  }
  if (msg.type === "GHOSTCODE_SUBMISSION") {
    postAuthenticated("/submission", { ...msg.payload, tab_active: true, browser_name: detectBrowserName() })
      .then(() => sendResponse({ ok: true }));
    return true;
  }
});

chrome.runtime.onInstalled.addListener(() => {
  injectedByTab.clear();
  sessionToken = "";
  sessionExpiresAt = 0;
  ensureKeepaliveAlarm();
  sendHeartbeat();
  scanFocusedActiveTab();
});

chrome.runtime.onStartup.addListener(() => {
  injectedByTab.clear();
  sessionToken = "";
  sessionExpiresAt = 0;
  ensureKeepaliveAlarm();
  sendHeartbeat();
  scanFocusedActiveTab();
});

if (chrome.alarms && chrome.alarms.onAlarm) {
  chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm && alarm.name === KEEPALIVE_ALARM) {
      sendHeartbeat();
      scanFocusedActiveTab();
    }
  });
}

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
    sendHeartbeat();
    scanFocusedActiveTab();
  });
}

ensureKeepaliveAlarm();
setInterval(sendHeartbeat, 4000);
setInterval(scanFocusedActiveTab, 5000);
sendHeartbeat();
scanFocusedActiveTab();
