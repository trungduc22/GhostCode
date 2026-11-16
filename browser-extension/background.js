const ENDPOINT = "http://127.0.0.1:48123/context";
const injectedByTab = new Map();

function cleanText(raw) {
  return (raw || "").replace(/\s+/g, " ").trim();
}

function stripCodePrefix(title, code) {
  let t = cleanText(title);
  // Remove leading "[CODE]" tag if we added it previously
  t = t.replace(/^\[[^\]]+\]\s*/, "");
  // Remove trailing " - Codeforces" / " - Code PTIT" / " - LeetCode"
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
  if (!tab || !tab.url) return null;
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
    const title = stripCodePrefix(rawTitle, code);
    return { problem_code: code, problem_title: title, platform: "PTIT", url: tab.url };
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

async function postToApp(payload) {
  if (!payload || !payload.problem_code) return;
  try {
    await fetch(ENDPOINT, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
  } catch {
    // App may not be running yet; ignore silently
  }
}

async function processTab(tab) {
  const ctx = extractFromTab(tab);
  if (!ctx) return;

  // Send URL-based context right away even before DOM script runs
  await postToApp(ctx);

  if (tab.id && injectedByTab.get(tab.id) !== tab.url) {
    injectedByTab.set(tab.id, tab.url);
    try {
      await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        files: ["content.js"]
      });
    } catch {
      // Ignore if host permission is unavailable or tab closed
    }
  }
}

async function scanActiveTabs() {
  try {
    const active = await chrome.tabs.query({ active: true });
    for (const tab of active) {
      if (extractFromTab(tab)) {
        await processTab(tab);
        return;
      }
    }
    // Fallback: check any open OJ tab in current browser if user switched temporarily
    const all = await chrome.tabs.query({});
    for (const tab of all) {
      if (extractFromTab(tab)) {
        await processTab(tab);
        return;
      }
    }
  } catch {
    // Ignore transient tab errors
  }
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg && msg.type === "AUTONAME_CONTEXT" && msg.payload) {
    postToApp(msg.payload).then(() => sendResponse({ ok: true }));
    return true;
  }
});

chrome.runtime.onInstalled.addListener(() => {
  injectedByTab.clear();
  scanActiveTabs();
});

chrome.runtime.onStartup.addListener(() => {
  injectedByTab.clear();
  scanActiveTabs();
});

chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  try {
    const tab = await chrome.tabs.get(tabId);
    await processTab(tab);
  } catch {}
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.url || changeInfo.status === "complete" || changeInfo.title) {
    if (changeInfo.url) {
      injectedByTab.delete(tabId);
    }
    processTab(tab);
  }
});

if (chrome.windows && chrome.windows.onFocusChanged) {
  chrome.windows.onFocusChanged.addListener(() => {
    scanActiveTabs();
  });
}

setInterval(scanActiveTabs, 3000);
scanActiveTabs();
