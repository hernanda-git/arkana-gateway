'use strict';
/*
 * Knowledge Base graph — themed for the ARKANA GATEWAY (gateway.arkana.dev).
 * Inherits the gateway design system via CSS custom properties (--accent, --bg,
 * --text, --border, ...), so it auto-follows light/dark and the warm terracotta
 * palette. Physics are the Obsidian-grade port (exact seed + one-shot d3-force
 * settle + drag-auto-reposition + label font counter-scale), unchanged from the
 * verified local render (32 nodes / 77 edges).
 *
 * Served from the /kb/ static mount: fetches /kb/vault/manifest.json + .md files.
 */
window.KBGraph = (function () {
  const VAULT_BASE = '/kb';

  // Resolve a gateway CSS variable to a concrete color (so SVG fills follow theme).
  function cssVar(name, fallback) {
    const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return v || fallback;
  }
  function palette() {
    return {
      accent: cssVar('--accent', '#E56A4A'),
      accentSubtle: cssVar('--accent-subtle', 'rgba(229,106,74,0.12)'),
      node: cssVar('--text-secondary', '#57534e'),
      nodeMuted: cssVar('--text-muted', '#a8a29e'),
      nodeHi: cssVar('--accent-500', '#E56A4A'),
      edge: cssVar('--border-strong', '#d6cfc6'),
      label: cssVar('--text-secondary', '#57534e'),
      labelMuted: cssVar('--text-muted', '#a8a29e'),
    };
  }

  const state = { manifest: null, notes: {}, byBasename: {}, byPath: {} };
  let graphRAF = null;

  async function loadVault() {
    const res = await fetch(`${VAULT_BASE}/vault/manifest.json`, { cache: 'no-store' });
    if (!res.ok) throw new Error('manifest.json not found (HTTP ' + res.status + ')');
    const manifest = await res.json();
    state.manifest = manifest;
    state.byBasename = {}; state.byPath = {};
    for (const f of manifest.files) {
      state.byPath[f.path] = f;
      const b = f.basename.toLowerCase();
      if (!(b in state.byBasename)) state.byBasename[b] = f.path;
    }
    renderTree(manifest.files);
    buildGraphInto(document.getElementById('kb-graph-body'), manifest);
  }

  function resolveTarget(name) {
    const tb = name.trim().toLowerCase();
    if (state.byPath[name]) return name;
    if (state.byBasename[tb]) return state.byBasename[tb];
    const lc = tb + '.md';
    if (state.byBasename[lc]) return state.byBasename[lc];
    const partial = Object.keys(state.byPath).find(p => p.toLowerCase().endsWith('/' + lc) || p.toLowerCase() === lc);
    return partial || null;
  }

  // ---- file tree (gateway-styled) ----
  function renderTree(files) {
    const host = document.getElementById('kb-filetree');
    if (!host) return;
    host.innerHTML = '';
    const root = { children: {}, files: [] };
    for (const f of files) {
      const parts = f.path.split('/');
      let node = root;
      for (let i = 0; i < parts.length - 1; i++) node = (node.children[parts[i]] ||= { name: parts[i], children: {}, files: [] });
      node.files.push(parts[parts.length - 1]);
    }
    const walk = (n, depth, base) => {
      for (const key of Object.keys(n.children).sort()) {
        const c = n.children[key];
        const fp = base ? base + '/' + key : key;
        const fe = document.createElement('div');
        fe.className = 'kb-tree-folder';
        fe.style.paddingLeft = (depth * 12 + 10) + 'px';
        fe.textContent = '▾ ' + key;
        fe.addEventListener('click', () => {
          const next = fe.nextElementSibling;
          // simple collapse: toggle a hidden class on following siblings until next folder
          let sib = fe.nextElementSibling;
          const block = [];
          while (sib && !sib.classList.contains('kb-tree-folder')) { block.push(sib); sib = sib.nextElementSibling; }
          const willHide = !fe.dataset.collapsed;
          block.forEach(b => b.style.display = willHide ? 'none' : '');
          fe.dataset.collapsed = willHide ? '1' : '';
          fe.textContent = (willHide ? '▸ ' : '▾ ') + key;
        });
        host.appendChild(fe);
        walk(c, depth + 1, fp);
      }
      for (const fn of n.files.sort()) {
        const fp = base ? base + '/' + fn : fn;
        const fe = document.createElement('div');
        fe.className = 'kb-tree-file';
        fe.style.paddingLeft = (depth * 12 + 24) + 'px';
        fe.textContent = '📄 ' + fn;
        fe.dataset.path = fp;
        fe.addEventListener('click', () => openNote(fp));
        host.appendChild(fe);
      }
    };
    walk(root, 0, '');
  }

  // ---- note reader (markdown via marked) ----
  const notePanel = () => document.getElementById('kb-note-panel');
  async function openNote(path) {
    let content = state.notes[path];
    if (content === undefined) {
      const res = await fetch(`${VAULT_BASE}/${encodeURI(path)}`, { cache: 'no-store' });
      content = res.ok ? await res.text() : '# Not found\n\n' + path;
      state.notes[path] = content;
    }
    const clean = content.replace(/^---\n[\s\S]*?\n---\n?/, '');
    const title = state.byPath[path] ? state.byPath[path].basename : path;
    const titleEl = document.getElementById('kb-note-title');
    const bodyEl = document.getElementById('kb-note-body');
    if (titleEl) titleEl.textContent = title;
    if (bodyEl) bodyEl.innerHTML = window.marked ? marked.parse(clean) : clean;
    notePanel()?.classList.add('open');
    document.querySelectorAll('.kb-tree-file').forEach(el => el.classList.toggle('active', el.dataset.path === path));
  }
  function closeNote() { notePanel()?.classList.remove('open'); }

  // ---- graph (Obsidian-grade, gateway-themed) ----
  function buildGraphInto(host, manifest) {
    if (graphRAF) { cancelAnimationFrame(graphRAF); graphRAF = null; }
    const rect = host.getBoundingClientRect();
    const W = Math.max(320, Math.round(rect.width) || window.innerWidth);
    const H = Math.max(240, Math.round(rect.height) || window.innerHeight);
    const files = manifest.files;
    const cx = W / 2, cy = H / 2;

    const L = Math.max(1, files.length);
    const I = 3600 * L, O = Math.sqrt(I / Math.PI), F = Math.sqrt(I);
    let _seed = 1337;
    const rnd = () => { _seed |= 0; _seed = (_seed + 0x6D2B79F5) | 0; let t = Math.imul(_seed ^ (_seed >>> 15), 1 | _seed); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };

    const nodes = files.map(f => ({ id: f.path, label: f.basename, x: 0, y: 0, vx: 0, vy: 0, deg: 0, radius: 2 }));
    const idx = Object.fromEntries(nodes.map(n => [n.id, n]));
    const adj = {}; nodes.forEach(n => adj[n.id] = []);
    const links = []; const seenLink = new Set();
    for (const l of manifest.links) {
      const a = l.source, b = l.target;
      if (!idx[a] || !idx[b]) continue;
      const key = a < b ? a + ' ' + b : b + ' ' + a;
      if (!seenLink.has(key)) { seenLink.add(key); links.push({ source: a, target: b }); adj[a].push(b); adj[b].push(a); idx[a].deg++; idx[b].deg++; }
    }
    for (const n of nodes) n.radius = 2 + Math.min(2, n.deg * 0.25);
    const order = [...nodes].sort((p, q) => idx[q.id].deg - idx[p.id].deg);
    const placed = {};
    for (const n of order) {
      const rel = adj[n.id].filter(id => placed[id]);
      if (rel.length) {
        let bx = 0, by = 0; for (const id of rel) { bx += placed[id].x; by += placed[id].y; }
        n.x = cx + bx / rel.length + (rnd() - 0.5) * F; n.y = cy + by / rel.length + (rnd() - 0.5) * F;
      } else {
        const ang = rnd() * 2 * Math.PI, r = Math.sqrt(rnd()) * O;
        n.x = cx + r * Math.cos(ang); n.y = cy + r * Math.sin(ang);
      }
      placed[n.id] = { x: n.x, y: n.y };
    }
    const neighborMap = {};
    for (const n of nodes) neighborMap[n.id] = new Set();
    for (const l of links) { neighborMap[l.source].add(l.target); neighborMap[l.target].add(l.source); }

    const pal = palette();
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('width', '100%'); svg.setAttribute('height', '100%');
    svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
    svg.style.cursor = 'grab';
    const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
    svg.appendChild(g);

    const linkEls = links.map(l => {
      const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
      line.setAttribute('stroke', pal.edge); line.setAttribute('stroke-width', '1');
      g.appendChild(line); return { line, l };
    });
    const nodeEls = nodes.map(n => {
      const c = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
      c.setAttribute('r', n.radius); c.setAttribute('fill', pal.node); c.style.cursor = 'pointer';
      g.appendChild(c);
      const baseFont = 9 + Math.min(7, n.deg);
      const t = document.createElementNS('http://www.w3.org/2000/svg', 'text');
      t.setAttribute('font-size', baseFont.toFixed(1)); t.setAttribute('text-anchor', 'middle');
      t.setAttribute('fill', pal.label); t.setAttribute('pointer-events', 'none'); t.textContent = n.label;
      g.appendChild(t);
      const entry = { c, t, n, radius: n.radius, baseFont, fixed: false };
      c.addEventListener('mousedown', (e) => startNodeDrag(e, entry));
      return entry;
    });
    host.innerHTML = ''; host.appendChild(svg);

    const view = { x: 0, y: 0, k: 1 };
    function applyTransform() {
      g.setAttribute('transform', `translate(${view.x.toFixed(2)},${view.y.toFixed(2)}) scale(${view.k.toFixed(4)})`);
      const fs = 1 / view.k;
      for (const { t, baseFont } of nodeEls) t.setAttribute('font-size', (baseFont * fs).toFixed(2));
    }
    function screenToWorld(clientX, clientY) { const r = svg.getBoundingClientRect(); return { x: (clientX - r.left - view.x) / view.k, y: (clientY - r.top - view.y) / view.k }; }
    svg.addEventListener('wheel', (e) => {
      e.preventDefault(); const r = svg.getBoundingClientRect();
      const mx = e.clientX - r.left, my = e.clientY - r.top;
      const factor = e.deltaY < 0 ? 1.12 : 1 / 1.12;
      const k2 = Math.max(0.15, Math.min(4, view.k * factor));
      view.x = mx - (mx - view.x) * (k2 / view.k); view.y = my - (my - view.y) * (k2 / view.k); view.k = k2; applyTransform();
    }, { passive: false });
    let panning = false, panStart = null;
    svg.addEventListener('mousedown', (e) => { if (e.target !== svg && e.target !== g) return; panning = true; svg.style.cursor = 'grabbing'; panStart = { x: e.clientX - view.x, y: e.clientY - view.y }; });
    window.addEventListener('mousemove', (e) => { if (panning) { view.x = e.clientX - panStart.x; view.y = e.clientY - panStart.y; applyTransform(); } });
    window.addEventListener('mouseup', () => { panning = false; svg.style.cursor = 'grab'; });

    let dragging = false, dragNode = null, moved = false, downPt = null;
    function startNodeDrag(e, entry) { e.stopPropagation(); dragging = true; moved = false; dragNode = entry; entry.fixed = true; downPt = { x: e.clientX, y: e.clientY }; svg.style.cursor = 'grabbing'; alpha = 0.3; if (!graphRAF) graphRAF = requestAnimationFrame(tickDrag); }
    window.addEventListener('mousemove', (e) => { if (dragging && dragNode) { if (downPt && Math.hypot(e.clientX - downPt.x, e.clientY - downPt.y) > 4) moved = true; const w = screenToWorld(e.clientX, e.clientY); dragNode.n.x = w.x; dragNode.n.y = w.y; alpha = Math.max(alpha, 0.3); if (!graphRAF) graphRAF = requestAnimationFrame(tickDrag); } });
    window.addEventListener('mouseup', () => { if (dragging) { const node = dragNode; dragging = false; dragNode = null; svg.style.cursor = 'grab'; if (node) { node.fixed = false; if (!moved) openNote(node.n.id); } if (!graphRAF) graphRAF = requestAnimationFrame(tickDrag); } });

    const velocityDecay = 0.6, charge = -1000, linkDist = 110, centerStrength = 0.1, maxV = 100000, collideR = 60;
    let alpha = 1;
    function tickDrag() {
      if (alpha <= 0.02 && !dragging) { graphRAF = null; return; }
      if (dragging) alpha = 0.3;
      const drag = dragging ? dragNode.n : null;
      if (drag) { drag.vx = 0; drag.vy = 0; }
      let affected = null;
      if (drag) { affected = new Set([drag]); for (const l of links) { if (l.source === drag.id) affected.add(idx[l.target]); else if (l.target === drag.id) affected.add(idx[l.source]); } }
      for (const n of nodes) {
        if (drag && !affected.has(n)) continue; if (n === drag) continue;
        let fx = 0, fy = 0;
        for (const m of nodes) { if (m === n) continue; const dx = n.x - m.x, dy = n.y - m.y, d = Math.max(1, Math.hypot(dx, dy)); const f = charge / (d * d); fx += dx / d * f; fy += dy / d * f; }
        for (const l of links) { if (l.source === n.id || l.target === n.id) { const other = idx[l.source === n.id ? l.target : l.source]; const dx = other.x - n.x, dy = other.y - n.y, d = Math.max(1, Math.hypot(dx, dy)); const f = (d - linkDist) * 0.08; fx += dx / d * f; fy += dy / d * f; } }
        fx += (cx - n.x) * centerStrength; fy += (cy - n.y) * centerStrength; fx *= alpha; fy *= alpha;
        n.vx = (n.vx || 0) + fx; n.vy = (n.vy || 0) + fy;
      }
      for (const n of nodes) { if (drag && !affected.has(n)) continue; if (n === drag) continue; n.vx *= velocityDecay; n.vy *= velocityDecay; const sp = Math.hypot(n.vx, n.vy); if (sp > maxV) { n.vx = n.vx / sp * maxV; n.vy = n.vy / sp * maxV; } n.x += n.vx; n.y += n.vy; n.x = Math.max(24, Math.min(W - 24, n.x)); n.y = Math.max(24, Math.min(H - 24, n.y)); }
      const gapD = collideR;
      for (let i = 0; i < nodes.length; i++) for (let j = i + 1; j < nodes.length; j++) { const a = nodes[i], b = nodes[j]; if (drag) { if (!affected.has(a) || !affected.has(b)) continue; } let dx = b.x - a.x, dy = b.y - a.y, d = Math.hypot(dx, dy); if (d < gapD && d > 0.01) { const push = (gapD - d) / 2; dx /= d; dy /= d; a.x -= dx * push; a.y -= dy * push; b.x += dx * push; b.y += dy * push; } }
      alpha *= 0.985; render();
      if (alpha > 0.001) graphRAF = requestAnimationFrame(tickDrag); else graphRAF = null;
    }

    nodeEls.forEach(entry => { entry.c.addEventListener('mouseenter', () => highlight(entry.n.id)); entry.c.addEventListener('mouseleave', () => highlight(null)); });
    function highlight(id) {
      const p = palette();
      if (!id) { for (const { c, t } of nodeEls) { c.setAttribute('opacity', '1'); t.setAttribute('opacity', '1'); c.setAttribute('fill', p.node); } for (const { line } of linkEls) { line.setAttribute('stroke', p.edge); line.setAttribute('stroke-opacity', '1'); } return; }
      const neigh = neighborMap[id];
      for (const e of nodeEls) { const on = e.n.id === id || neigh.has(e.n.id); e.c.setAttribute('opacity', on ? '1' : '0.18'); e.t.setAttribute('opacity', on ? '1' : '0.12'); e.c.setAttribute('fill', e.n.id === id ? p.nodeHi : p.node); }
      for (const { line, l } of linkEls) { const on = l.source === id || l.target === id; line.setAttribute('stroke', on ? p.accent : p.edge); line.setAttribute('stroke-opacity', on ? '1' : '0.12'); }
    }

    // one-shot settle (Obsidian d3-force math)
    const linkDistSettle = 110, chargeSettle = -1000, centerSettle = 0.1, collideRadiusSettle = 60, maxVSettle = 100000;
    let alphaS = 1;
    function forceStep() {
      for (const n of nodes) { let fx = 0, fy = 0; for (const m of nodes) { if (m === n) continue; const dx = n.x - m.x, dy = n.y - m.y, d = Math.max(1, Math.hypot(dx, dy)); const f = chargeSettle / (d * d); fx += dx / d * f; fy += dy / d * f; } for (const l of links) { if (l.source === n.id || l.target === n.id) { const other = idx[l.source === n.id ? l.target : l.source]; const dx = other.x - n.x, dy = other.y - n.y, d = Math.max(1, Math.hypot(dx, dy)); const f = (d - linkDistSettle) * 0.08; fx += dx / d * f; fy += dy / d * f; } } fx += (cx - n.x) * centerSettle; fy += (cy - n.y) * centerSettle; fx *= alphaS; fy *= alphaS; n.vx = (n.vx || 0) + fx; n.vy = (n.vy || 0) + fy; }
      for (const n of nodes) { n.vx *= 0.6; n.vy *= 0.6; const sp = Math.hypot(n.vx, n.vy); if (sp > maxVSettle) { n.vx = n.vx / sp * maxVSettle; n.vy = n.vy / sp * maxVSettle; } n.x += n.vx; n.y += n.vy; }
      const gapS = collideRadiusSettle;
      for (let i = 0; i < nodes.length; i++) for (let j = i + 1; j < nodes.length; j++) { const a = nodes[i], b = nodes[j]; let dx = b.x - a.x, dy = b.y - a.y, d = Math.hypot(dx, dy); if (d < gapS && d > 0.01) { const push = (gapS - d) / 2; dx /= d; dy /= d; a.x -= dx * push; a.y -= dy * push; b.x += dx * push; b.y += dy * push; } }
      alphaS *= 0.985;
    }
    let guard = 0; while (alphaS > 0.001 && guard++ < 4000) forceStep();
    const minGap = 40;
    for (let iter = 0; iter < 40; iter++) { let mv = false; for (let i = 0; i < nodes.length; i++) for (let j = i + 1; j < nodes.length; j++) { const a = nodes[i], b = nodes[j]; let dx = b.x - a.x, dy = b.y - a.y, d = Math.hypot(dx, dy); if (d < minGap) { if (d < 0.01) { dx = 0.01; dy = 0; d = 0.01; } const push = (minGap - d) / 2 + 0.5; dx /= d; dy /= d; a.x -= dx * push; a.y -= dy * push; b.x += dx * push; b.y += dy * push; a.x = Math.max(24, Math.min(W - 24, a.x)); a.y = Math.max(24, Math.min(H - 24, a.y)); b.x = Math.max(24, Math.min(W - 24, b.x)); b.y = Math.max(24, Math.min(H - 24, b.y)); mv = true; } } if (!mv) break; }

    function render() { for (const { line, l } of linkEls) { line.setAttribute('x1', idx[l.source].x); line.setAttribute('y1', idx[l.source].y); line.setAttribute('x2', idx[l.target].x); line.setAttribute('y2', idx[l.target].y); } for (const { c, t, n, radius } of nodeEls) { c.setAttribute('cx', n.x); c.setAttribute('cy', n.y); t.setAttribute('x', n.x); t.setAttribute('y', n.y + radius + 4); } }

    // recenter + auto-fit
    let cxs = 0, cys = 0; for (const n of nodes) { cxs += n.x; cys += n.y; } cxs /= nodes.length; cys /= nodes.length;
    const shiftX = cx - cxs, shiftY = cy - cys; for (const n of nodes) { n.x += shiftX; n.y += shiftY; }
    let minX = 1e9, minY = 1e9, maxX = -1e9, maxY = -1e9; for (const n of nodes) { minX = Math.min(minX, n.x); maxX = Math.max(maxX, n.x); minY = Math.min(minY, n.y); maxY = Math.max(maxY, n.y); }
    const gw = Math.max(1, maxX - minX), gh = Math.max(1, maxY - minY); const pad = 80;
    const fitK = Math.min((W - pad * 2) / gw, (H - pad * 2) / gh, 2.5);
    view.k = Math.max(0.15, Math.min(4, fitK)); view.x = (W - gw * view.k) / 2 - minX * view.k; view.y = (H - gh * view.k) / 2 - minY * view.k;
    applyTransform(); render();

    // expose zoom/fit
    window.__kbZoom = (f) => { const k2 = Math.max(0.15, Math.min(4, view.k * f)); view.x = (W / 2) - (W / 2 - view.x) * (k2 / view.k); view.y = (H / 2) - (H / 2 - view.y) * (k2 / view.k); view.k = k2; applyTransform(); };
    window.__kbFit = () => { view.k = Math.max(0.15, Math.min(4, fitK)); view.x = (W - gw * view.k) / 2 - minX * view.k; view.y = (H - gh * view.k) / 2 - minY * view.k; applyTransform(); };

    // re-theme on dark-mode toggle
    new MutationObserver(() => {
      const p = palette();
      for (const { c } of nodeEls) c.setAttribute('fill', p.node);
      for (const { line } of linkEls) line.setAttribute('stroke', p.edge);
      for (const { t } of nodeEls) t.setAttribute('fill', p.label);
    }).observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
  }

  return {
    init() {
      const body = document.getElementById('kb-graph-body');
      if (!body) return;
      const loading = document.getElementById('kb-loading');
      loadVault().then(() => {
        loading && loading.remove();
        const c = document.getElementById('kb-count');
        if (c && state.manifest) c.textContent = state.manifest.files.length + ' notes';
      }).catch(err => {
        if (loading) loading.textContent = 'Failed to load vault: ' + err.message;
        console.error(err);
      });
      const search = document.getElementById('kb-search');
      if (search) search.addEventListener('input', (e) => {
        const q = e.target.value.toLowerCase();
        document.querySelectorAll('.kb-tree-file').forEach(el => {
          el.style.display = (!q || (el.dataset.path || '').toLowerCase().includes(q)) ? '' : 'none';
        });
      });
    },
    openNote, closeNote
  };
})();
