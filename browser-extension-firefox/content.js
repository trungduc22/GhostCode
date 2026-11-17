(() => {
  const isBrowser = typeof window !== "undefined" && typeof document !== "undefined";

  function cleanText(raw) {
    return (raw || "").replace(/\u00a0/g, " ").replace(/\s+/g, " ").trim();
  }

  function extractFormattedText(el) {
    if (!el) return "";
    const lines = el.querySelectorAll ? el.querySelectorAll(".test-example-line") : [];
    if (lines && lines.length > 0) {
      return Array.from(lines)
        .map((line) => (line.textContent || "").replace(/\u00a0/g, " ").replace(/\r?\n$/, ""))
        .join("\n")
        .replace(/^\n+|\s+$/g, "");
    }

    const clone = el.cloneNode(true);
    if (clone.querySelectorAll) {
      clone.querySelectorAll("br").forEach((br) => {
        br.replaceWith(el.ownerDocument.createTextNode("\n"));
      });
      clone.querySelectorAll("p, div, li, tr, h1, h2, h3, h4, h5, h6").forEach((blk) => {
        blk.appendChild(el.ownerDocument.createTextNode("\n"));
      });
    }

    return (clone.textContent || "")
      .replace(/\u00a0/g, " ")
      .replace(/\r\n/g, "\n")
      .replace(/\r/g, "\n")
      .replace(/[ \t]+\n/g, "\n")
      .replace(/\n{3,}/g, "\n\n")
      .replace(/^\n+|\s+$/g, "");
  }

  function isConstraintOrMetadataTable(tbl) {
    const firstRow = tbl.querySelector("tr");
    const firstRowText = firstRow ? cleanText(firstRow.textContent) : "";
    if (/^(thời\s*gian|time\s*limit|bộ\s*nhớ|memory\s*limit|giới\s*hạn|subtask|điểm|score|ràng\s*buộc|quy\s*ước)/i.test(firstRowText)) {
      return true;
    }
    const headerCells = firstRow ? Array.from(firstRow.querySelectorAll("th, td")).map((c) => cleanText(c.textContent)) : [];
    if (headerCells.some((h) => /time\s*limit|memory\s*limit|thời\s*gian|bộ\s*nhớ|subtask|điểm/i.test(h))) {
      return true;
    }
    return false;
  }

  function hasSampleTableHeader(tbl) {
    const firstRow = tbl.querySelector("tr");
    if (!firstRow) return { hasHeaderRow: false, isSampleTable: false };
    const cells = Array.from(firstRow.querySelectorAll("th, td"));
    if (cells.length < 2) return { hasHeaderRow: false, isSampleTable: false };

    const c0 = cleanText(cells[0].textContent);
    const c1 = cleanText(cells[1].textContent);
    const isInpHeader = /\b(input|dữ\s*liệu\s*vào|đầu\s*vào|mẫu\s*vào)\b/i.test(c0);
    const isOutHeader = /\b(output|kết\s*quả|dữ\s*liệu\s*ra|đầu\s*ra|mẫu\s*ra)\b/i.test(c1);
    if (isInpHeader && isOutHeader && c0.length <= 40 && c1.length <= 40) {
      return { hasHeaderRow: true, isSampleTable: true };
    }
    return { hasHeaderRow: false, isSampleTable: false };
  }

  function hasSampleHeadingBefore(el) {
    let prev = el.previousElementSibling;
    let steps = 0;
    while (prev && steps < 3) {
      const txt = cleanText(prev.textContent);
      if (txt) {
        if (/\b(ví\s*dụ|sample|example|test\s*mẫu)\b/i.test(txt) && txt.length <= 80) {
          return true;
        }
        if (/\b(giới\s*hạn|constraint|ràng\s*buộc|mô\s*tả|đề\s*bài)\b/i.test(txt)) {
          return false;
        }
      }
      prev = prev.previousElementSibling;
      steps++;
    }
    return false;
  }

  function looksLikeMetadataCell(txt) {
    return /^(input|dữ\s*liệu\s*vào|output|kết\s*quả|thời\s*gian|bộ\s*nhớ|time|memory|subtask)\s*:?$/i.test(cleanText(txt));
  }

  function pickCellSampleElement(cell) {
    if (!cell) return null;
    const pre = cell.querySelector("pre");
    if (pre) return pre;
    return cell;
  }

  function extractPtitSamples(root) {
    const pairs = [];
    if (!root) return pairs;

    const tables = Array.from(root.querySelectorAll("table"));
    for (const tbl of tables) {
      if (isConstraintOrMetadataTable(tbl)) continue;

      const { hasHeaderRow, isSampleTable } = hasSampleTableHeader(tbl);
      const nearExampleHeading = hasSampleHeadingBefore(tbl);
      if (!isSampleTable && !nearExampleHeading) continue;

      const rows = Array.from(tbl.querySelectorAll("tr"));
      const startIndex = hasHeaderRow ? 1 : 0;
      for (let i = startIndex; i < rows.length; i++) {
        const cells = Array.from(rows[i].querySelectorAll("td"));
        if (cells.length !== 2) continue;

        const inpTxt = extractFormattedText(pickCellSampleElement(cells[0]));
        const outTxt = extractFormattedText(pickCellSampleElement(cells[1]));
        if (!inpTxt && !outTxt) continue;
        if (looksLikeMetadataCell(inpTxt) && looksLikeMetadataCell(outTxt)) continue;
        if (looksLikeMetadataCell(inpTxt) && !outTxt) continue;

        pairs.push({ input: inpTxt, output: outTxt });
      }
    }

    if (pairs.length > 0) return pairs.slice(0, 10);

    // Fallback for PTIT problems rendered with labeled Input / Output blocks rather than a table
    const nodes = Array.from(root.querySelectorAll("p, h3, h4, strong, b, div, pre"));
    let pendingInput = null;
    let expecting = null;
    for (const node of nodes) {
      const tag = (node.tagName || "").toUpperCase();
      if (tag === "PRE") {
        const txt = extractFormattedText(node);
        if (!txt) continue;
        if (expecting === "input") {
          pendingInput = txt;
          expecting = null;
        } else if (expecting === "output" && pendingInput !== null) {
          pairs.push({ input: pendingInput, output: txt });
          pendingInput = null;
          expecting = null;
        }
        continue;
      }

      if (node.querySelector && node.querySelector("pre")) continue;
      const label = cleanText(node.textContent);
      if (label.length > 40) continue;
      if (/^(ví\s*dụ\s*)?(input|dữ\s*liệu\s*vào)\s*:?$/i.test(label)) {
        expecting = "input";
      } else if (/^(ví\s*dụ\s*)?(output|kết\s*quả)\s*:?$/i.test(label)) {
        expecting = "output";
      }
    }

    return pairs.slice(0, 10);
  }

  function extractSamplePairs(platform, rootNode) {
    const pairs = [];
    const doc = rootNode && rootNode.ownerDocument ? rootNode.ownerDocument : (typeof document !== "undefined" ? document : null);
    const scope = rootNode || doc;
    if (!scope) return pairs;

    if (platform === "Codeforces") {
      (doc || scope).querySelectorAll(".problem-statement .sample-test").forEach((box) => {
        const inputs = box.querySelectorAll(".input pre");
        const outputs = box.querySelectorAll(".output pre");
        const count = Math.min(inputs.length, outputs.length);
        for (let i = 0; i < count; i++) {
          pairs.push({ input: extractFormattedText(inputs[i]), output: extractFormattedText(outputs[i]) });
        }
      });
      return pairs.slice(0, 10);
    }

    if (platform === "HackerRank") {
      const ins = (doc || scope).querySelectorAll(".challenge_sample_input pre, pre.sample-input");
      const outs = (doc || scope).querySelectorAll(".challenge_sample_output pre, pre.sample-output");
      const count = Math.min(ins.length, outs.length);
      for (let i = 0; i < count; i++) {
        pairs.push({ input: extractFormattedText(ins[i]), output: extractFormattedText(outs[i]) });
      }
      return pairs.slice(0, 10);
    }

    if (platform === "LeetCode") {
      // LeetCode uses function signatures rather than standalone STDIN/STDOUT test harnesses
      return [];
    }

    if (platform === "PTIT" || platform === "VNOJ") {
      const cfStyle = scope.querySelectorAll ? scope.querySelectorAll(".sample-test") : [];
      if (cfStyle.length > 0) {
        cfStyle.forEach((box) => {
          const inputs = box.querySelectorAll(".input pre");
          const outputs = box.querySelectorAll(".output pre");
          const count = Math.min(inputs.length, outputs.length);
          for (let i = 0; i < count; i++) {
            pairs.push({ input: extractFormattedText(inputs[i]), output: extractFormattedText(outputs[i]) });
          }
        });
        if (pairs.length > 0) return pairs.slice(0, 10);
      }

      return extractPtitSamples(scope);
    }

    return [];
  }

  function findProblemContainer(platform, docObj) {
    const d = docObj || document;
    const selectors = platform === "PTIT"
      ? [".submit__des", ".problem__content", ".question-content", ".submit__main .card-body", "main .card-body", "#problem-content", ".problem-statement"]
      : [".problem-statement", ".submit__des", ".content-description", "[data-track-load='description_content']", ".challenge-body-html"];

    for (const sel of selectors) {
      const el = d.querySelector(sel);
      if (el && cleanText(el.textContent).length > 0) return el;
    }
    return null;
  }

  function extractProblemDetailsFromDocument(ctx, docObj) {
    if (!ctx) return null;
    const d = docObj || document;
    const container = findProblemContainer(ctx.platform, d);
    if (!container) return null;

    const fullText = cleanText(container.textContent);
    const description = fullText.slice(0, 3500);
    const firstFrom = (sels) => {
      for (const s of sels) {
        const el = d.querySelector(s);
        if (el) {
          const txt = cleanText(el.textContent);
          if (txt) return txt;
        }
      }
      return "";
    };

    const inputSpec = firstFrom([".input-specification", ".input-spec", "[data-section='input']"]);
    const outputSpec = firstFrom([".output-specification", ".output-spec", "[data-section='output']"]);
    const timeLimit = firstFrom([".time-limit", ".problem-info .time-limit"]);
    const memoryLimit = firstFrom([".memory-limit", ".problem-info .memory-limit"]);
    const interactive = ctx.platform === "LeetCode"
      || /\b(interactive|custom checker|special judge)\b/i.test(fullText);
    const samples = interactive && ctx.platform === "LeetCode"
      ? []
      : extractSamplePairs(ctx.platform, container);

    return {
      problem_code: ctx.problem_code,
      problem_title: ctx.problem_title || "",
      platform: ctx.platform,
      url: ctx.url,
      description,
      input_spec: inputSpec,
      output_spec: outputSpec,
      constraints: "",
      time_limit: timeLimit,
      memory_limit: memoryLimit,
      interactive_or_custom: interactive,
      samples,
      tab_active: true,
      timestamp: Date.now()
    };
  }

  if (typeof module !== "undefined" && module.exports) {
    module.exports = {
      cleanText,
      extractFormattedText,
      extractPtitSamples,
      extractSamplePairs,
      extractProblemDetailsFromDocument
    };
  }

  if (!isBrowser) return;

  if (window.__autonameContentLoaded) {
    if (typeof window.__autonameSyncNow === "function") {
      window.__autonameSyncNow(true);
    }
    return;
  }
  window.__autonameContentLoaded = true;

  let lastUrl = location.href;
  let lastSentKey = "";
  let lastSentProblemKey = "";
  let lastSentVerdictKey = "";
  let mutationTimer = null;

  function extractProblemDetails(ctx) {
    return extractProblemDetailsFromDocument(ctx, document);
  }

  function extractSubmissionVerdict(ctx) {
    if (!ctx) return null;
    let verdictText = "";
    let submissionId = "";
    const language = "";

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
    if (/^(judging|running|in queue|pending|waiting)/i.test(verdictText)) {
      verdictText = "Pending";
    }

    return {
      platform: ctx.platform,
      problem_code: ctx.problem_code,
      url: ctx.url,
      submission_id: submissionId || null,
      submitted_at: Date.now(),
      language,
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
      if (chrome && chrome.runtime && chrome.runtime.sendMessage) {
        chrome.runtime.sendMessage({ type, payload }, () => {
          void chrome.runtime.lastError;
        });
      }
    } catch {}
  }

  function sendViaBackground(ctx) {
    sendMessageSafe("AUTONAME_CONTEXT", ctx);
  }

  function syncProblemAndVerdicts(ctx, force) {
    const details = extractProblemDetails(ctx);
    if (details) {
      const sampleSig = details.samples.map((s) => `${s.input.length}:${s.output.length}`).join(",");
      const pKey = `${details.platform}|${details.problem_code}|${details.url}|${details.samples.length}|${sampleSig}|${details.description.length}`;
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

  function canonicalizeUrl(rawUrl) {
    try {
      const u = new URL(rawUrl);
      u.hash = "";
      return u.toString();
    } catch {
      return rawUrl || "";
    }
  }

  function syncContext(force) {
    const ctx = extractContext();
    if (!ctx) return;

    const taggedTitle = ctx.problem_title
      ? `[${ctx.problem_code}] ${ctx.problem_title} - ${ctx.platform}`
      : `[${ctx.problem_code}] - ${ctx.platform}`;
    if (document.title !== taggedTitle && ctx.problem_title) {
      document.title = taggedTitle;
    }

    const canon = canonicalizeUrl(ctx.url);
    const key = `${ctx.platform}|${ctx.problem_code}|${ctx.problem_title}|${canon}`;
    if (force || key !== lastSentKey) {
      lastSentKey = key;
      sendViaBackground(ctx);
      syncProblemAndVerdicts(ctx, true);
      return;
    }

    syncProblemAndVerdicts(ctx, false);
  }

  function handleUrlOrDomChange(force = false) {
    const cur = location.href;
    const urlChanged = cur !== lastUrl;
    if (urlChanged) lastUrl = cur;
    syncContext(force || urlChanged);
  }

  window.__autonameSyncNow = syncContext;
  syncContext(true);

  if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.onMessage) {
    chrome.runtime.onMessage.addListener((msg) => {
      if (msg && msg.type === "GHOSTCODE_RESCAN_PROBLEM") {
        syncContext(true);
      }
    });
  }

  // Event-driven SPA & tab visibility navigation hooks (zero 5s blind DOM polling)
  const wrapHistoryMethod = (fnName) => {
    try {
      const orig = history[fnName];
      if (typeof orig === "function") {
        history[fnName] = function () {
          const ret = orig.apply(this, arguments);
          queueMicrotask(() => handleUrlOrDomChange(true));
          return ret;
        };
      }
    } catch {}
  };
  wrapHistoryMethod("pushState");
  wrapHistoryMethod("replaceState");
  window.addEventListener("popstate", () => handleUrlOrDomChange(true));
  window.addEventListener("hashchange", () => handleUrlOrDomChange(true));
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) handleUrlOrDomChange(false);
  });

  // Scoped MutationObserver with 200ms debounce so irrelevant header/clock/ad churn does not rescan DOM
  const targetNode = document.querySelector(
    ".submit__des, .problem-statement, [data-track-load='description_content'], .challenge-body-html, main"
  ) || document.body;
  if (targetNode) {
    const obs = new MutationObserver(() => {
      if (mutationTimer) clearTimeout(mutationTimer);
      mutationTimer = setTimeout(() => handleUrlOrDomChange(false), 200);
    });
    obs.observe(targetNode, { childList: true, subtree: true });
  }
})();
