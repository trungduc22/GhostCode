import assert from "node:assert/strict";
import fs from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const contentPath = path.resolve(__dirname, "../../../browser-extension/content.js");
const { extractProblemDetailsFromDocument } = require(contentPath);

// Minimal deterministic HTML DOM tree builder for fixture testing without external npm dependencies
class TextNode {
  constructor(data, ownerDocument) {
    this.nodeType = 3;
    this.data = data;
    this.ownerDocument = ownerDocument;
  }
  get textContent() { return this.data; }
  cloneNode() { return new TextNode(this.data, this.ownerDocument); }
}

class ElementNode {
  constructor(tagName, attrs = {}, ownerDocument = null) {
    this.nodeType = 1;
    this.tagName = tagName.toUpperCase();
    this.attrs = { ...attrs };
    this.children = [];
    this.parentNode = null;
    this.ownerDocument = ownerDocument;
  }
  appendChild(child) {
    child.parentNode = this;
    child.ownerDocument = this.ownerDocument;
    this.children.push(child);
    return child;
  }
  replaceWith(node) {
    if (!this.parentNode) return;
    const idx = this.parentNode.children.indexOf(this);
    if (idx >= 0) {
      node.parentNode = this.parentNode;
      this.parentNode.children[idx] = node;
    }
  }
  cloneNode(deep = false) {
    const c = new ElementNode(this.tagName, this.attrs, this.ownerDocument);
    if (deep) {
      for (const ch of this.children) c.appendChild(ch.cloneNode(true));
    }
    return c;
  }
  get textContent() {
    return this.children.map((c) => c.textContent).join("");
  }
  get previousElementSibling() {
    if (!this.parentNode) return null;
    const sibs = this.parentNode.children.filter((c) => c.nodeType === 1);
    const idx = sibs.indexOf(this);
    return idx > 0 ? sibs[idx - 1] : null;
  }
  matchesSelector(sel) {
    const s = sel.trim();
    if (s.startsWith(".")) {
      const cls = s.slice(1);
      return (this.attrs.class || "").split(/\s+/).includes(cls);
    }
    if (s.startsWith("#")) return this.attrs.id === s.slice(1);
    if (s.startsWith("[")) {
      const m = s.match(/^\[([^=\]]+)='?([^'\]]+)'?\]$/);
      if (m) return this.attrs[m[1]] === m[2];
    }
    return this.tagName === s.toUpperCase();
  }
  matchesCompound(compound) {
    const parts = compound.trim().split(/\s+/);
    if (!this.matchesSingleCompound(parts[parts.length - 1])) return false;
    let cur = this.parentNode;
    for (let i = parts.length - 2; i >= 0; i--) {
      while (cur && !cur.matchesSingleCompound(parts[i])) cur = cur.parentNode;
      if (!cur) return false;
      cur = cur.parentNode;
    }
    return true;
  }
  matchesSingleCompound(part) {
    const tokens = part.match(/(?:#[^.#[]+|\.[^.#[]+|\[[^\]]+\]|^[a-zA-Z0-9_-]+)/g) || [];
    return tokens.length > 0 && tokens.every((t) => this.matchesSelector(t));
  }
  querySelectorAll(selectorList) {
    const sels = selectorList.split(",").map((s) => s.trim()).filter(Boolean);
    const results = [];
    const walk = (node) => {
      for (const ch of node.children) {
        if (ch.nodeType === 1) {
          if (sels.some((s) => ch.matchesCompound(s))) results.push(ch);
          walk(ch);
        }
      }
    };
    walk(this);
    return results;
  }
  querySelector(selectorList) {
    return this.querySelectorAll(selectorList)[0] || null;
  }
}

class Doc {
  constructor() {
    this.root = new ElementNode("body", {}, this);
  }
  createTextNode(t) { return new TextNode(t, this); }
  el(tag, attrs = {}, ...kids) {
    const node = new ElementNode(tag, attrs, this);
    for (const k of kids) {
      node.appendChild(typeof k === "string" ? this.createTextNode(k) : k);
    }
    return node;
  }
  querySelector(s) { return this.root.querySelector(s); }
  querySelectorAll(s) { return this.root.querySelectorAll(s); }
}

// 1. CodePTIT C04032 fixture: constraint table MUST be ignored, TinyMCE <p>-multiline sample table MUST preserve \n
{
  const doc = new Doc();
  const des = doc.el(
    "div",
    { class: "submit__des" },
    doc.el("p", {}, "Cho dãy số nguyên dương."),
    doc.el(
      "table",
      {},
      doc.el("tr", {}, doc.el("th", {}, "Thời gian giới hạn"), doc.el("th", {}, "Bộ nhớ")),
      doc.el("tr", {}, doc.el("td", {}, "1.0 giây"), doc.el("td", {}, "256 MB"))
    ),
    doc.el(
      "table",
      {},
      doc.el("tr", {}, doc.el("td", {}, doc.el("strong", {}, "Input")), doc.el("td", {}, doc.el("strong", {}, "Output"))),
      doc.el(
        "tr",
        {},
        doc.el("td", {}, doc.el("p", {}, "5"), doc.el("p", {}, "1 2 3 2 1")),
        doc.el("td", {}, doc.el("p", {}, "1 2"), doc.el("p", {}, "2 2"), doc.el("p", {}, "3 1"))
      )
    )
  );
  doc.root.appendChild(des);

  const details = extractProblemDetailsFromDocument(
    { platform: "PTIT", problem_code: "C04032", problem_title: "Liệt kê và đếm (DLab)", url: "https://code.ptit.edu.vn/student/question/C04032" },
    doc
  );
  assert.ok(details, "C04032 details should extract");
  assert.equal(details.samples.length, 1, "Should skip constraint table and extract exact 1 sample pair");
  assert.equal(details.samples[0].input, "5\n1 2 3 2 1");
  assert.equal(details.samples[0].output, "1 2\n2 2\n3 1");
}

// 2. CodePTIT fixture with NO samples (only prose & metadata) must return 0 samples cleanly
{
  const doc = new Doc();
  const des = doc.el(
    "div",
    { class: "submit__des" },
    doc.el("p", {}, "Mô tả bài toán không có bảng ví dụ."),
    doc.el("table", {}, doc.el("tr", {}, doc.el("td", {}, "Giới hạn"), doc.el("td", {}, "N <= 1000")))
  );
  doc.root.appendChild(des);
  const details = extractProblemDetailsFromDocument(
    { platform: "PTIT", problem_code: "C99999", problem_title: "Empty Sample Test", url: "https://code.ptit.edu.vn/student/question/C99999" },
    doc
  );
  assert.equal(details.samples.length, 0);
}

// 3. Codeforces .sample-test with .test-example-line
{
  const doc = new Doc();
  const stmt = doc.el(
    "div",
    { class: "problem-statement" },
    doc.el("div", { class: "time-limit" }, "time limit per test 1 second"),
    doc.el(
      "div",
      { class: "sample-test" },
      doc.el("div", { class: "input" }, doc.el("pre", {}, doc.el("div", { class: "test-example-line" }, "2"), doc.el("div", { class: "test-example-line" }, "3 5 1"))),
      doc.el("div", { class: "output" }, doc.el("pre", {}, doc.el("div", { class: "test-example-line" }, "1")))
    )
  );
  doc.root.appendChild(stmt);
  const details = extractProblemDetailsFromDocument(
    { platform: "Codeforces", problem_code: "CF_1872A", problem_title: "Two Vessels", url: "https://codeforces.com/problemset/problem/1872/A" },
    doc
  );
  assert.equal(details.samples.length, 1);
  assert.equal(details.samples[0].input, "2\n3 5 1");
  assert.equal(details.samples[0].output, "1");
}

// 4. LeetCode marks interactive_or_custom = true and produces no stdin sample
{
  const doc = new Doc();
  doc.root.appendChild(doc.el("div", { "data-track-load": "description_content" }, doc.el("p", {}, "Given nums, return indices.")));
  const details = extractProblemDetailsFromDocument(
    { platform: "LeetCode", problem_code: "LC_two-sum", problem_title: "Two Sum", url: "https://leetcode.com/problems/two-sum/" },
    doc
  );
  assert.equal(details.interactive_or_custom, true);
  assert.equal(details.samples.length, 0);
}

// 5. Verify fast SPA hooks + zero 5s blind DOM polling + fast HMAC before native spawn
{
  const bgText = fs.readFileSync(path.resolve(__dirname, "../../../browser-extension/background.js"), "utf8");
  const ctText = fs.readFileSync(contentPath, "utf8");
  assert.ok(!ctText.includes("setInterval("), "content.js must not run blind setInterval DOM polling");
  assert.ok(ctText.includes("pushState") && ctText.includes("visibilitychange"), "content.js must use event-driven SPA & visibility hooks");
  const idxPairing = bgText.indexOf("loadLocalPairing(");
  const idxNative = bgText.indexOf("tryNativeHandshake()", bgText.indexOf("async function ensureToken"));
  assert.ok(idxPairing > 0 && idxNative > idxPairing, "background.js ensureToken must authenticate via fast HMAC pairing before slow native spawn fallback");
}

console.log("ALL_CONTENT_JS_EXTRACTION_FIXTURES_PASSED");
