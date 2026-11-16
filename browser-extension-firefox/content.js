(() => {
  if (window.__autonameContentLoaded) {
    if (typeof window.__autonameSyncNow === "function") {
      window.__autonameSyncNow(true);
    }
    return;
  }
  window.__autonameContentLoaded = true;

  const ext = globalThis.browser ?? globalThis.chrome;
  let lastUrl = location.href;
  let lastSentKey = "";
  let lastSentProblemKey = "";
  let lastSentVerdictKey = "";

  function cleanText(raw) {
    return (raw || "").replace(/\s+/g, " ").trim();
  }

  function extractPreText(pre) {
    if (!pre) return "";
    const lines = pre.querySelectorAll(".test-example-line");
    if (lines && lines.length > 0) {
      return Array.from(lines).map((el) => (el.textContent || "").replace(/\r?\n$/, "")).join("\n");
    }
    const clone = pre.cloneNode(true);
    clone.querySelectorAll("br").forEach((br) => br.replaceWith("\n"));
    return (clone.textContent || "").replace(/\r\n/g, "\n").replace(/^\n+|\s+$/g, "");
  }

  function extractSamplePairs(platform) {
    const pairs = [];
    if (platform === "Codeforces") {
      document.querySelectorAll(".problem-statement .sample-test").forEach((box) => {
        const inputs = box.querySelectorAll(".input pre");
        const outputs = box.querySelectorAll(".output pre");
        const count = Math.min(inputs.length, outputs.length);
        for (let i = 0; i < count; i++) {
          pairs.push({ input: extractPreText(inputs[i]), output: extractPreText(outputs[i]) });
        }
      });
      return pairs;
    }
    if (platform === "HackerRank") {
      const ins = document.querySelectorAll(".challenge_sample_input pre, pre.sample-input");
      const outs = document.querySelectorAll(".challenge_sample_output pre, pre.sample-output");
      const count = Math.min(ins.length, outs.length);
      for (let i = 0; i < count; i++) {
        pairs.push({ input: extractPreText(ins[i]), output: extractPreText(outs[i]) });
      }
      if (pairs.length > 0) return pairs;
    }
    const tables = document.querySelectorAll(".submit__des table, .problem-statement table, .content-description table");
    tables.forEach((tbl) => {
      const rows = Array.from(tbl.querySelectorAll("tr"));
      for (const row of rows) {
        const cells = Array.from(row.querySelectorAll("td"));
        if (cells.length === 2) {
          const inpEl = cells[0].querySelector("pre") || cells[0];
          const outEl = cells[1].querySelector("pre") || cells[1];
          const inpTxt = extractPreText(inpEl);
          const outTxt = extractPreText(outEl);
          if (inpTxt || outTxt) {
            if (/^(input|dữ\s*liệu\s*vào)$/i.test(cleanText(inpTxt))) continue;
            pairs.push({ input: inpTxt, output: outTxt });
          }
        }
      }
    });
    if (pairs.length > 0) return pairs;
    const pres = Array.from(document.querySelectorAll(".submit__des pre, .content-description pre, .challenge-body-html pre"));
    for (let i = 0; i + 1 < pres.length; i += 2) {
      pairs.push({ input: extractPreText(pres[i]), output: extractPreText(pres[i + 1]) });
    }
    return pairs.slice(0, 10);
  }

  function extractProblemDetails(ctx) {
    if (!ctx) return null;
    const container = document.querySelector(".problem-statement, .submit__des, .content-description, [data-track-load='description_content'], .challenge-body-html");
    if (!container) return null;
    const fullText = cleanText(container.textContent);
    return {
      problem_code: ctx.problem_code,
      problem_title: ctx.problem_title || "",
      platform: ctx.platform,
      url: ctx.url,
      description: fullText.slice(0, 3500),
      input_spec: firstText([".input-specification", ".input-spec", "[data-section='input']"]),
      output_spec: firstText([".output-specification", ".output-spec", "[data-section='output']"]),
      constraints: "",
      time_limit: firstText([".time-limit", ".problem-info .time-limit"]),
      memory_limit: firstText([".memory-limit", ".problem-info .memory-limit"]),
      interactive_or_custom: /\b(interactive|custom checker|special judge)\b/i.test(fullText),
      samples: extractSamplePairs(ctx.platform),
      tab_active: true,
      timestamp: Date.now()
    };
  }

  function extractSubmissionVerdict(ctx) {
    if (!ctx) return null;
    let verdictText = "";
    let submissionId = "";
    if (ctx.platform === "Codeforces") {
      const cell = document.querySelector(".status-verdict-cell[submissionid], td.status-verdict-cell");
      if (cell) {
        verdictText = cleanText(cell.textContent);
        submissionId = cell.getAttribute("submissionid") || "";
      }
    } else if (ctx.platform === "LeetCode") {
      verdictText = firstText(['[data-e2e-locator="submission-result"]', ".text-green-s", ".text-red-s"]);
    } else if (ctx.platform === "PTIT") {
      verdictText = firstText([".submit__history tbody tr:first-child td .badge", ".submit__history tbody tr:first-child td:last-child"]);
      submissionId = firstText([".submit__history tbody tr:first-child td:first-child"]);
    } else if (ctx.platform === "VNOJ") {
      verdictText = firstText(["#submission-info .verdict", ".submission-row .status"]);
    } else if (ctx.platform === "HackerRank") {
      verdictText = firstText([".submission-status", ".congratulations h3"]);
    }
    if (!verdictText) return null;
    if (/^(judging|running|in queue|pending|waiting)/i.test(verdictText)) verdictText = "Pending";
    return {
      platform: ctx.platform,
      problem_code: ctx.problem_code,
      url: ctx.url,
      submission_id: submissionId || null,
      submitted_at: Date.now(),
      language: "",
      verdict: verdictText,
      is_version_certain: false,
      tab_active: true
    };
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

  function sendMessageSafe(type, payload) {
    try {
      if (ext && ext.runtime && ext.runtime.sendMessage) {
        const res = ext.runtime.sendMessage({ type, payload });
        if (res && typeof res.catch === "function") {
          res.catch(() => {});
        }
      }
    } catch {}
  }

  function sendViaBackground(ctx) {
    sendMessageSafe("AUTONAME_CONTEXT", ctx);
  }

  function syncProblemAndVerdicts(ctx, force) {
    const details = extractProblemDetails(ctx);
    if (details) {
      const pKey = `${details.platform}|${details.problem_code}|${details.samples.length}|${details.description.length}`;
      if (force || pKey !== lastSentProblemKey) {
        lastSentProblemKey = pKey;
        sendMessageSafe("GHOSTCODE_PROBLEM", details);
      }
    }
    const sub = extractSubmissionVerdict(ctx);
    if (sub) {
      const sKey = `${sub.platform}|${sub.problem_code}|${sub.submission_id || ""}|${sub.verdict}`;
      if (sKey !== lastSentVerdictKey) {
        lastSentVerdictKey = sKey;
        sendMessageSafe("GHOSTCODE_SUBMISSION", sub);
      }
    }
  }

  function syncContext(force) {
    if (document.visibilityState === "hidden") return;
    const ctx = extractContext();
    if (!ctx) return;

    const taggedTitle = ctx.problem_title
      ? `[${ctx.problem_code}] ${ctx.problem_title} - ${ctx.platform}`
      : `[${ctx.problem_code}] - ${ctx.platform}`;
    if (document.title !== taggedTitle) {
      document.title = taggedTitle;
    }

    const key = `${ctx.problem_code}|${ctx.problem_title}|${ctx.url}`;
    if (force || key !== lastSentKey) {
      lastSentKey = key;
      sendViaBackground(ctx);
    }

    syncProblemAndVerdicts(ctx, force);
  }

  window.__autonameSyncNow = syncContext;
  syncContext(true);

  setInterval(() => {
    if (document.visibilityState !== "visible") return;
    const changed = location.href !== lastUrl;
    if (changed) lastUrl = location.href;
    syncContext(changed);
  }, 5000);

  const obs = new MutationObserver(() => {
    if (document.visibilityState !== "visible") return;
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
