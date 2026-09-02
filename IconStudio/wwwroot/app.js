// NexusTK Icon Studio frontend. One icon at a time: pick it from the list, load a source or a
// downscaled candidate into the pixel editor, fix it against the frame's real 256-colour
// palette, save it as a draft, approve it. The backend owns every file; this page only ever
// holds one frame in memory.
//
// Frame model (mirrors IconStudio/ItemArt.cs): w*h palette indices + w*h alpha flags + a 256
// entry RGB palette. Index 0 is the transparent key; opaque pixels use 1..255.
'use strict';
const $ = id => document.getElementById(id);
const b64 = {
  dec: s => Uint8Array.from(atob(s), c => c.charCodeAt(0)),
  enc: u8 => { let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000)); return btoa(s); },
};

const S = {
  meta: null, icons: [], atlas: {}, atlasImg: {},
  filter: '', scope: 'all', shown: [], cols: 5, rowH: 64, selId: null,
  detail: null,
  edit: null,        // { w, h, top, left, pal: Uint8Array(768), idx: Uint8Array, alpha: Uint8Array, from }
  dirty: false, undo: [], redo: [],
  tool: 'pencil', color: 1, zoom: 12, grid: true,
  hover: { x: -1, y: -1 }, painting: false,
  exportClient: '495',
};

const TOOLS = [
  ['pencil', 'pencil (B) — paint the selected palette colour', '<path d="M3 17l1-4 9.5-9.5a1.77 1.77 0 0 1 2.5 2.5L6.5 15.5 3 17z"/><path d="M12 5l2.5 2.5"/>'],
  ['eraser', 'eraser (E) — make pixels transparent', '<path d="M11.5 3.5l5 5-7 7H6l-2.5-2.5z"/><path d="M8 6.5l5 5"/><path d="M11 17h6"/>'],
  ['picker', 'eyedropper (I) — pick the colour under the cursor', '<path d="M13.2 2.8a2 2 0 0 1 2.8 0l1.2 1.2a2 2 0 0 1 0 2.8l-2.1 2.1-4-4z"/><path d="M10.6 5.4l4 4-6.4 6.4c-.5.5-1.1.8-1.8.9l-2.6.4.4-2.6c.1-.7.4-1.3.9-1.8z"/>'],
  ['fill', 'flood fill (G) — same colour region', '<path d="M9.5 2.5v3"/><path d="M4 10.5l5.5-5.5 5 5-5.5 5.5z"/><path d="M16.8 13.5c.9 1.2.9 2.6 0 3.4-.9-.8-.9-2.2 0-3.4z"/>'],
  null,
  ['undo', 'undo (Ctrl+Z)', '<path d="M8 6H4v4"/><path d="M4 10c2-3.5 8-4.5 11-1.5s1 7.5-3 7.5H9"/>'],
  ['redo', 'redo (Ctrl+Y)', '<path d="M12 6h4v4"/><path d="M16 10c-2-3.5-8-4.5-11-1.5s-1 7.5 3 7.5h3"/>'],
  null,
  ['shiftL', 'nudge left', '<path d="M12 5l-5 5 5 5"/>'],
  ['shiftR', 'nudge right', '<path d="M8 5l5 5-5 5"/>'],
  ['shiftU', 'nudge up', '<path d="M5 12l5-5 5 5"/>'],
  ['shiftD', 'nudge down', '<path d="M5 8l5 5 5-5"/>'],
];

// --------------------------------------------------------------------------- boot
async function boot() {
  const mr = await fetch('/api/meta', { cache: 'no-store' });
  if (!mr.ok) throw new Error('meta HTTP ' + mr.status);
  S.meta = await mr.json();
  S.icons = S.meta.icons;
  const chips = $('srcChips');
  for (const key of ['retail', '495', '533']) {
    const src = S.meta.sources[key];
    const el = document.createElement('span');
    el.className = 'chip' + (src ? ' on' : '');
    el.textContent = (key === 'retail' ? 'retail' : key === '495' ? '4.95' : '5.33') + (src ? ` · ${src.count}` : ' · not found');
    el.title = src ? src.path : 'client archive not found';
    chips.appendChild(el);
  }
  for (const key of Object.keys(S.meta.sources)) {
    const img = new Image();
    img.src = `/api/atlas/${key}.png`;
    S.atlasImg[key] = img;
    img.onload = () => drawGrid();
  }
  buildRail();
  wireUi();
  applyFilter();
  drawPreview();
  setInterval(() => fetch('/api/ping', { method: 'POST' }).catch(() => {}), 3000);
  updateDraftCount();
  msg('ready');
}

function buildRail() {
  const rail = $('rail');
  for (const t of TOOLS) {
    if (!t) { const s = document.createElement('div'); s.className = 'tsep'; rail.appendChild(s); continue; }
    const b = document.createElement('button');
    b.className = 'tool'; b.dataset.tool = t[0]; b.title = t[1];
    b.innerHTML = `<svg width="20" height="20" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${t[2]}</svg>`;
    b.onclick = () => toolClick(t[0]);
    rail.appendChild(b);
  }
  setTool('pencil');
}

function toolClick(name) {
  if (name === 'undo') return undo();
  if (name === 'redo') return redo();
  if (name.startsWith('shift')) return nudge(name[5]);
  setTool(name);
}

function setTool(name) {
  S.tool = name;
  for (const b of document.querySelectorAll('#rail .tool')) b.classList.toggle('on', b.dataset.tool === name);
}

// --------------------------------------------------------------------------- list
function applyFilter() {
  const f = S.filter.trim().toLowerCase();
  const idNum = /^\d+$/.test(f) ? parseInt(f, 10) : null;
  S.shown = S.icons.filter(ic => {
    if (S.scope === 'missing' && (ic.a || ic.nc === 0)) return false;
    if (S.scope === 'draft' && ic.d === 0) return false;
    if (S.scope === 'approved' && ic.d !== 2) return false;
    if (!f) return true;
    if (idNum !== null && ic.id === idNum) return true;
    return ic.n.some(n => n.toLowerCase().includes(f));
  }).map(ic => ic.id);
  $('listNote').textContent = `${S.shown.length} icon${S.shown.length === 1 ? '' : 's'}`;
  layoutGrid();
  drawGrid();
}

function layoutGrid() {
  const wrap = $('gridScroll');
  const w = wrap.clientWidth - 8;
  S.cols = Math.max(3, Math.floor(w / 62));
  const rows = Math.ceil(S.shown.length / S.cols);
  $('gridInner').style.height = rows * S.rowH + 'px';
  const c = $('grid');
  c.width = S.cols * 62; c.height = Math.min(rows * S.rowH, wrap.clientHeight + S.rowH * 2);
}

function drawGrid() {
  const wrap = $('gridScroll'), c = $('grid'), ctx = c.getContext('2d');
  const first = Math.floor(wrap.scrollTop / S.rowH);
  c.style.top = first * S.rowH + 'px';
  ctx.clearRect(0, 0, c.width, c.height);
  const slot = S.meta.slot, acols = S.meta.atlasCols;
  const visRows = Math.ceil(c.height / S.rowH);
  for (let r = 0; r < visRows; r++) {
    for (let col = 0; col < S.cols; col++) {
      const k = (first + r) * S.cols + col;
      if (k >= S.shown.length) break;
      const id = S.shown[k], ic = S.icons[id];
      const x = col * 62 + 4, y = r * S.rowH + 2;
      ctx.fillStyle = id === S.selId ? 'rgba(63,168,122,.18)' : '#1f2124';
      ctx.fillRect(x, y, 56, 60);
      // thumbnail: the 4.95 frame when it exists, else retail — a glance shows what the client draws today
      const src = ic.a ? '495' : ic.r ? 'retail' : ic.b ? '533' : null;
      const img = src && S.atlasImg[src];
      if (img && img.complete && img.naturalWidth) ctx.drawImage(img, id % acols * slot, Math.floor(id / acols) * slot, slot, slot, x + 4, y + 2, 48, 48);
      ctx.font = '10px "IBM Plex Mono", monospace';
      ctx.fillStyle = id === S.selId ? '#e8eaec' : '#8b9096';
      ctx.fillText(String(id), x + 4, y + 58);
      if (ic.d) { ctx.fillStyle = ic.d === 2 ? '#3fa87a' : '#d9962e'; ctx.beginPath(); ctx.arc(x + 50, y + 8, 3.5, 0, 7); ctx.fill(); }
      if (!ic.a) { ctx.fillStyle = '#be3c2d'; ctx.fillRect(x + 46, y + 52, 8, 3); }
      if (id === S.selId) { ctx.strokeStyle = '#3fa87a'; ctx.strokeRect(x + .5, y + .5, 55, 59); }
    }
  }
}

function gridHit(ev) {
  const wrap = $('gridScroll'), rect = $('grid').getBoundingClientRect();
  const x = ev.clientX - rect.left, y = ev.clientY - rect.top + (wrap.scrollTop - Math.floor(wrap.scrollTop / S.rowH) * S.rowH);
  const col = Math.floor(x / 62), row = Math.floor(wrap.scrollTop / S.rowH) + Math.floor((ev.clientY - rect.top) / S.rowH);
  if (col < 0 || col >= S.cols) return null;
  const k = row * S.cols + col;
  return k < S.shown.length ? S.shown[k] : null;
}

// --------------------------------------------------------------------------- detail
async function select(id) {
  if (S.dirty && !confirm('Discard unsaved edits on icon ' + S.selId + '?')) return;
  S.selId = id; S.edit = null; S.dirty = false; S.undo = []; S.redo = [];
  drawGrid();
  const r = await fetch(`/api/icon/${id}`, { cache: 'no-store' });
  if (!r.ok) return msg('load failed: HTTP ' + r.status, true);
  S.detail = await r.json();
  const ic = S.icons[id];
  $('edTitle').textContent = `icon ${id}`;
  $('edNames').textContent = S.detail.names.length ? S.detail.names.join(', ') : 'no item uses this icon';
  $('edNames').title = $('edNames').textContent;
  $('stIcon').textContent = `icon ${id}` + (ic.a ? ' · in 4.95' : ' · NOT in 4.95') + (ic.b ? ' · in 5.33' : '') + (ic.r ? ' · retail 2x' : '');
  renderCards();
  // Load the most useful thing automatically: the saved draft, else the snap candidate, else what exists.
  const d = S.detail.draft;
  if (d) loadFrame(d, 'draft');
  else if (S.detail.candidates.snap) loadFrame(S.detail.candidates.snap, 'snap');
  else if (S.detail.c495) loadFrame(S.detail.c495, '4.95');
  else if (S.detail.c533) loadFrame(S.detail.c533, '5.33');
  else { S.edit = null; drawView(); }
  $('draftNote').value = d?.note || '';
  updateDraftUi();
}

function card(label, fr, key, sub) {
  const el = document.createElement('div');
  el.className = 'card' + (fr ? '' : ' none');
  const cv = document.createElement('canvas');
  const scale = fr && Math.max(fr.w, fr.h) > 40 ? 1 : 2;
  cv.width = 84; cv.height = 84;
  if (fr) drawFrameTo(cv, fr, scale);
  el.appendChild(cv);
  const l = document.createElement('div'); l.className = 'cl'; l.textContent = label; el.appendChild(l);
  const s = document.createElement('div'); s.className = 'cs'; s.textContent = fr ? `${fr.w}×${fr.h}${sub ? ' ' + sub : ''}` : 'none'; el.appendChild(s);
  if (fr) { el.dataset.key = key; el.onclick = () => { if (S.dirty && !confirm('Replace the current edit with ' + label + '?')) return; loadFrame(fr, key); }; }
  return el;
}

function renderCards() {
  const d = S.detail;
  const src = $('sources'); src.innerHTML = '';
  src.appendChild(card('retail 2x', d.retail, 'retail'));
  src.appendChild(card('4.95', d.c495, '4.95'));
  src.appendChild(card('5.33', d.c533, '5.33'));
  src.appendChild(card('draft', d.draft, 'draft', d.draft?.approved ? '✓' : ''));
  const cd = $('cands'); cd.innerHTML = '';
  cd.appendChild(card('snap', d.candidates.snap, 'snap'));
  cd.appendChild(card('box', d.candidates.box, 'box'));
  cd.appendChild(card('nearest', d.candidates.nearest, 'nearest'));
  if (d.retail) { $('candW').value = Math.ceil(d.retail.w / 2); $('candH').value = Math.ceil(d.retail.h / 2); }
  markCard();
}

function markCard() {
  for (const el of document.querySelectorAll('.card')) el.classList.toggle('on', !!S.edit && el.dataset.key === S.edit.from);
}

function drawFrameTo(cv, fr, scale) {
  const ctx = cv.getContext('2d');
  ctx.clearRect(0, 0, cv.width, cv.height);
  const pal = b64.dec(fr.pal), idx = b64.dec(fr.idx), alpha = b64.dec(fr.alpha);
  const img = ctx.createImageData(fr.w, fr.h);
  for (let i = 0; i < fr.w * fr.h; i++) {
    if (!alpha[i]) continue;
    const c = idx[i] * 3;
    img.data[i * 4] = pal[c]; img.data[i * 4 + 1] = pal[c + 1]; img.data[i * 4 + 2] = pal[c + 2]; img.data[i * 4 + 3] = 255;
  }
  const tmp = document.createElement('canvas'); tmp.width = fr.w; tmp.height = fr.h;
  tmp.getContext('2d').putImageData(img, 0, 0);
  ctx.imageSmoothingEnabled = false;
  const dw = fr.w * scale, dh = fr.h * scale;
  ctx.drawImage(tmp, Math.floor((cv.width - dw) / 2), Math.floor((cv.height - dh) / 2), dw, dh);
}

// --------------------------------------------------------------------------- editor state
function loadFrame(fr, from) {
  S.edit = { w: fr.w, h: fr.h, top: fr.top, left: fr.left, pal: b64.dec(fr.pal), idx: b64.dec(fr.idx), alpha: b64.dec(fr.alpha), from };
  S.dirty = false; S.undo = []; S.redo = [];
  if (S.color >= 256) S.color = 1;
  $('empty').hidden = true;
  markCard();
  drawPalette();
  drawView();
  updateDraftUi();
}

function snapshot() { return { idx: S.edit.idx.slice(), alpha: S.edit.alpha.slice(), w: S.edit.w, h: S.edit.h, top: S.edit.top, left: S.edit.left }; }
function restore(s) { Object.assign(S.edit, { idx: s.idx.slice(), alpha: s.alpha.slice(), w: s.w, h: s.h, top: s.top, left: s.left }); }
function pushUndo() { S.undo.push(snapshot()); if (S.undo.length > 100) S.undo.shift(); S.redo = []; }
function undo() { if (!S.edit || !S.undo.length) return; S.redo.push(snapshot()); restore(S.undo.pop()); touched(); }
function redo() { if (!S.edit || !S.redo.length) return; S.undo.push(snapshot()); restore(S.redo.pop()); touched(); }
function touched() { S.dirty = true; drawView(); updateDraftUi(); }

function setPixel(x, y, color) {
  const e = S.edit;
  if (x < 0 || y < 0 || x >= e.w || y >= e.h) return false;
  const i = y * e.w + x;
  if (color === 0) { if (!e.alpha[i]) return false; e.alpha[i] = 0; e.idx[i] = 0; return true; }
  if (e.alpha[i] && e.idx[i] === color) return false;
  e.alpha[i] = 1; e.idx[i] = color;
  return true;
}

function floodFill(x, y, color) {
  const e = S.edit;
  if (x < 0 || y < 0 || x >= e.w || y >= e.h) return;
  const key = i => e.alpha[i] ? e.idx[i] : -1;
  const target = key(y * e.w + x);
  if (target === (color === 0 ? -1 : color)) return;
  const stack = [[x, y]], seen = new Uint8Array(e.w * e.h);
  while (stack.length) {
    const [px, py] = stack.pop();
    if (px < 0 || py < 0 || px >= e.w || py >= e.h) continue;
    const i = py * e.w + px;
    if (seen[i] || key(i) !== target) continue;
    seen[i] = 1;
    setPixel(px, py, color);
    stack.push([px + 1, py], [px - 1, py], [px, py + 1], [px, py - 1]);
  }
}

function nudge(dir) {
  if (!S.edit) return;
  pushUndo();
  const e = S.edit, dx = dir === 'L' ? -1 : dir === 'R' ? 1 : 0, dy = dir === 'U' ? -1 : dir === 'D' ? 1 : 0;
  const idx = new Uint8Array(e.w * e.h), alpha = new Uint8Array(e.w * e.h);
  for (let y = 0; y < e.h; y++) for (let x = 0; x < e.w; x++) {
    const sx = x - dx, sy = y - dy;
    if (sx < 0 || sy < 0 || sx >= e.w || sy >= e.h) continue;
    idx[y * e.w + x] = e.idx[sy * e.w + sx]; alpha[y * e.w + x] = e.alpha[sy * e.w + sx];
  }
  e.idx = idx; e.alpha = alpha;
  touched();
}

// --------------------------------------------------------------------------- editor canvas
function drawView() {
  const c = $('view'), ctx = c.getContext('2d');
  if (!S.edit) { c.width = c.height = 1; $('empty').hidden = false; drawPreview(); return; }
  const e = S.edit, z = S.zoom;
  c.width = e.w * z; c.height = e.h * z;
  c.style.width = c.width + 'px'; c.style.height = c.height + 'px';
  // checkerboard
  for (let y = 0; y < e.h; y++) for (let x = 0; x < e.w; x++) {
    ctx.fillStyle = (x + y) & 1 ? '#2a2c30' : '#232529';
    ctx.fillRect(x * z, y * z, z, z);
  }
  for (let y = 0; y < e.h; y++) for (let x = 0; x < e.w; x++) {
    const i = y * e.w + x;
    if (!e.alpha[i]) continue;
    const p = e.idx[i] * 3;
    ctx.fillStyle = `rgb(${e.pal[p]},${e.pal[p + 1]},${e.pal[p + 2]})`;
    ctx.fillRect(x * z, y * z, z, z);
  }
  if (S.grid && z >= 6) {
    ctx.strokeStyle = 'rgba(0,0,0,.35)'; ctx.lineWidth = 1;
    ctx.beginPath();
    for (let x = 1; x < e.w; x++) { ctx.moveTo(x * z + .5, 0); ctx.lineTo(x * z + .5, c.height); }
    for (let y = 1; y < e.h; y++) { ctx.moveTo(0, y * z + .5); ctx.lineTo(c.width, y * z + .5); }
    ctx.stroke();
    // centre cross: where the frame's origin sits (the client centres the box on the slot)
    ctx.strokeStyle = 'rgba(63,168,122,.45)';
    ctx.beginPath();
    ctx.moveTo(-e.left * z + .5, 0); ctx.lineTo(-e.left * z + .5, c.height);
    ctx.moveTo(0, -e.top * z + .5); ctx.lineTo(c.width, -e.top * z + .5);
    ctx.stroke();
  }
  if (S.hover.x >= 0) { ctx.strokeStyle = '#e8eaec'; ctx.strokeRect(S.hover.x * z + .5, S.hover.y * z + .5, z - 1, z - 1); }
  drawPreview();
}

function drawPreview() {
  for (const [id, s] of [['pv1', 1], ['pv2', 2], ['pv3', 3]]) {
    const cv = $(id);
    if (!S.edit) { cv.width = cv.height = 1; continue; }
    // a fixed 52px-tall strip: small icons sit in a bag-slot-sized box, big ones are centre-cropped
    const e = S.edit;
    cv.width = Math.min(96, Math.max(e.w * s + 6, 26 * s)); cv.height = 52;
    drawFrameTo(cv, { w: e.w, h: e.h, pal: b64.enc(e.pal), idx: b64.enc(e.idx), alpha: b64.enc(e.alpha) }, s);
  }
}

function viewCell(ev) {
  const r = $('view').getBoundingClientRect();
  return { x: Math.floor((ev.clientX - r.left) / S.zoom), y: Math.floor((ev.clientY - r.top) / S.zoom) };
}

function applyTool(x, y, first) {
  const e = S.edit;
  if (!e) return;
  if (S.tool === 'picker') {
    if (x >= 0 && y >= 0 && x < e.w && y < e.h) { const i = y * e.w + x; S.color = e.alpha[i] ? e.idx[i] : 0; drawPalette(); }
    return;
  }
  if (first) pushUndo();
  if (S.tool === 'fill') { floodFill(x, y, S.color); touched(); return; }
  if (setPixel(x, y, S.tool === 'eraser' ? 0 : S.color)) touched();
}

// --------------------------------------------------------------------------- palette
function drawPalette() {
  const c = $('palCanvas'), ctx = c.getContext('2d');
  ctx.clearRect(0, 0, 256, 256);
  if (!S.edit) return;
  const pal = S.edit.pal;
  for (let i = 0; i < 256; i++) {
    const x = i % 16 * 16, y = Math.floor(i / 16) * 16;
    if (i === 0) { ctx.fillStyle = '#2a2c30'; ctx.fillRect(x, y, 16, 16); ctx.fillStyle = '#232529'; ctx.fillRect(x, y, 8, 8); ctx.fillRect(x + 8, y + 8, 8, 8); }
    else { ctx.fillStyle = `rgb(${pal[i * 3]},${pal[i * 3 + 1]},${pal[i * 3 + 2]})`; ctx.fillRect(x, y, 16, 16); }
  }
  const sx = S.color % 16 * 16, sy = Math.floor(S.color / 16) * 16;
  ctx.strokeStyle = '#fff'; ctx.lineWidth = 2; ctx.strokeRect(sx + 1, sy + 1, 14, 14);
  ctx.strokeStyle = '#000'; ctx.lineWidth = 1; ctx.strokeRect(sx + 2.5, sy + 2.5, 11, 11);
  const p = S.color * 3;
  $('palSel').textContent = S.color === 0 ? 'index 0 · transparent' : `index ${S.color} · rgb(${pal[p]}, ${pal[p + 1]}, ${pal[p + 2]})`;
  $('palInfo').textContent = S.detail && S.edit ? `block ${paletteBlockLabel()}` : '';
}

function paletteBlockLabel() {
  const d = S.detail, from = S.edit.from;
  const fr = from === 'retail' ? d.retail : from === '4.95' ? d.c495 : from === '5.33' ? d.c533 : from === 'draft' ? null : d.candidates[from] || null;
  return fr && fr.palIndex !== undefined ? fr.palIndex + (from === 'retail' || from === 'snap' || from === 'box' || from === 'nearest' || from === 'custom' ? ' (retail)' : ' (' + from + ')') : 'of the draft';
}

// --------------------------------------------------------------------------- draft actions
function updateDraftUi() {
  const has = !!S.edit, d = S.detail?.draft;
  $('btnSave').disabled = !has || !S.dirty && S.edit?.from === 'draft';
  $('btnApprove').disabled = !d;
  $('btnApprove').textContent = d?.approved ? 'Unapprove' : 'Approve';
  $('btnApprove').classList.toggle('warn', !!d && !d.approved);
  $('btnRevert').disabled = !d || !S.dirty;
  $('btnDiscard').disabled = !d;
  $('draftState').textContent = d ? (d.approved ? 'approved' : 'saved') + (S.dirty ? ' · unsaved edits' : '') : (S.dirty ? 'unsaved edits' : 'none');
  $('draftState').className = 'mono ' + (d?.approved ? 'accent' : S.dirty ? 'bright' : 'dim');
}

function editPayload() {
  const e = S.edit;
  return { id: S.selId, w: e.w, h: e.h, top: e.top, left: e.left, pal: b64.enc(e.pal), idx: b64.enc(e.idx), alpha: b64.enc(e.alpha),
           approved: !!S.detail?.draft?.approved, source: e.from === 'draft' ? (S.detail.draft.source || 'draft') : e.from, note: $('draftNote').value.trim() || null };
}

async function saveDraft() {
  if (!S.edit) return;
  const r = await fetch(`/api/icon/${S.selId}/draft`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(editPayload()) });
  if (!r.ok) return msg('save failed: ' + await r.text(), true);
  const j = await r.json();
  S.icons[S.selId] = j.row;
  S.detail.draft = editPayload(); S.detail.draft.approved = j.row.d === 2;
  S.edit.from = 'draft'; S.dirty = false;
  renderCards(); updateDraftUi(); drawGrid(); updateDraftCount();
  msg('saved ' + j.saved);
}

async function toggleApprove() {
  const d = S.detail?.draft;
  if (!d) return;
  const r = await fetch(`/api/icon/${S.selId}/approve`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ approved: !d.approved }) });
  if (!r.ok) return msg('approve failed: ' + await r.text(), true);
  const j = await r.json();
  S.icons[S.selId] = j.row; d.approved = j.row.d === 2;
  renderCards(); updateDraftUi(); drawGrid(); updateDraftCount();
  msg(d.approved ? `icon ${S.selId} approved — it will be in the next export` : `icon ${S.selId} unapproved`);
}

async function discardDraft() {
  if (!S.detail?.draft || !confirm(`Delete the saved draft for icon ${S.selId}?`)) return;
  const r = await fetch(`/api/icon/${S.selId}/draft`, { method: 'DELETE' });
  if (!r.ok) return msg('discard failed: ' + await r.text(), true);
  const j = await r.json();
  S.icons[S.selId] = j.row; S.detail.draft = null; S.dirty = false;
  renderCards(); updateDraftUi(); drawGrid(); updateDraftCount();
  msg('draft deleted');
}

function revertDraft() {
  if (!S.detail?.draft) return;
  loadFrame(S.detail.draft, 'draft');
}

async function makeCandidate() {
  if (S.selId === null || !S.detail?.retail) return;
  const w = parseInt($('candW').value, 10), h = parseInt($('candH').value, 10);
  if (!(w >= 1 && h >= 1 && w <= 128 && h <= 128)) return msg('size must be 1..128', true);
  const r = await fetch(`/api/icon/${S.selId}/candidate`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ method: $('candMethod').value, w, h }) });
  if (!r.ok) return msg('candidate failed: ' + await r.text(), true);
  if (S.dirty && !confirm('Replace the current edit with the custom candidate?')) return;
  loadFrame(await r.json(), 'custom');
  msg(`${$('candMethod').value} ${w}×${h} loaded`);
}

// Import PNG: decode in the browser, quantize into the CURRENT palette (nearest RGB, alpha < 50%
// = transparent). Sized as the file is — export what the tool saved (saved/icons/<id>.png), edit it
// in your pixel editor, bring it back at the same size.
function importPng(file) {
  if (!S.edit) return msg('load a source or candidate first — the import needs its palette', true);
  const img = new Image();
  img.onload = () => {
    if (img.width > 128 || img.height > 128) return msg('PNG larger than 128px — downscale it first', true);
    const cv = document.createElement('canvas'); cv.width = img.width; cv.height = img.height;
    const ctx = cv.getContext('2d'); ctx.drawImage(img, 0, 0);
    const data = ctx.getImageData(0, 0, img.width, img.height).data;
    pushUndo();
    const e = S.edit, pal = e.pal;
    e.w = img.width; e.h = img.height; e.left = -Math.floor(e.w / 2); e.top = -Math.floor(e.h / 2);
    e.idx = new Uint8Array(e.w * e.h); e.alpha = new Uint8Array(e.w * e.h);
    for (let i = 0; i < e.w * e.h; i++) {
      if (data[i * 4 + 3] < 128) continue;
      let best = 1, bd = 1e9;
      for (let c = 1; c < 256; c++) {
        const dr = pal[c * 3] - data[i * 4], dg = pal[c * 3 + 1] - data[i * 4 + 1], db = pal[c * 3 + 2] - data[i * 4 + 2];
        const d = dr * dr + dg * dg + db * db;
        if (d < bd) { bd = d; best = c; if (!d) break; }
      }
      e.idx[i] = best; e.alpha[i] = 1;
    }
    e.from = 'import';
    touched(); markCard();
    msg(`imported ${file.name} (${e.w}×${e.h}) into the current palette`);
  };
  img.onerror = () => msg('could not decode that PNG', true);
  img.src = URL.createObjectURL(file);
}

async function exportApproved() {
  const n = S.icons.filter(i => i.d === 2).length;
  if (!n) return msg('nothing approved yet', true);
  const client = S.exportClient === '495' ? '4.95' : '5.33';
  if (!confirm(`Build the ${client} item archive with ${n} approved icon${n === 1 ? '' : 's'}?\n\nOutput goes to ${S.meta.export}/${S.exportClient}/ — nothing is installed.`)) return;
  const r = await fetch('/api/export', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ client: S.exportClient }) });
  if (!r.ok) return msg('export failed: ' + await r.text(), true);
  const j = await r.json();
  msg(`exported ${j.dir}/${j.dat}: ${j.replaced} replaced, ${j.appended} appended, ${j.gaps} blank gap frames, ${j.framesBefore}→${j.framesAfter} frames, ${j.paletteBlocks} palette blocks`);
  alert(`Export written to ${j.dir}/\n\n${j.dat}: ${j.framesBefore} → ${j.framesAfter} frames\nreplaced ${j.replaced}, appended ${j.appended}, blank gap frames ${j.gaps}\npalette blocks ${j.paletteBlocks}\n\nInstall by hand: back up the client's ${j.dat} and copy this one over it. See manifest.json there.`);
}

function updateDraftCount() {
  const d = S.icons.filter(i => i.d).length, a = S.icons.filter(i => i.d === 2).length;
  $('stDrafts').textContent = `${d} draft${d === 1 ? '' : 's'} · ${a} approved`;
}

function msg(text, err) {
  const el = $('stMsg'); el.textContent = text; el.className = 'mono ' + (err ? 'err' : 'ok');
}

// --------------------------------------------------------------------------- wiring
function wireUi() {
  $('filter').oninput = () => { S.filter = $('filter').value; applyFilter(); };
  for (const b of document.querySelectorAll('#scope .segbtn')) b.onclick = () => {
    S.scope = b.dataset.scope;
    for (const o of document.querySelectorAll('#scope .segbtn')) o.classList.toggle('on', o === b);
    applyFilter();
  };
  for (const b of document.querySelectorAll('#exportClient .segbtn')) b.onclick = () => {
    S.exportClient = b.dataset.client;
    for (const o of document.querySelectorAll('#exportClient .segbtn')) o.classList.toggle('on', o === b);
  };
  $('gridScroll').onscroll = () => drawGrid();
  $('grid').onclick = ev => { const id = gridHit(ev); if (id !== null) select(id); };
  $('grid').onmousemove = ev => { const id = gridHit(ev); $('grid').title = id === null ? '' : `${id}: ${S.icons[id].n.join(', ') || 'unused'}`; };
  window.addEventListener('resize', () => { layoutGrid(); drawGrid(); });

  const view = $('view');
  view.onmousedown = ev => { if (ev.button !== 0) return; const { x, y } = viewCell(ev); S.painting = true; applyTool(x, y, true); };
  view.onmousemove = ev => {
    const { x, y } = viewCell(ev);
    if (x !== S.hover.x || y !== S.hover.y) { S.hover = { x, y }; drawView(); }
    if (S.edit) { const i = y * S.edit.w + x; const inside = x >= 0 && y >= 0 && x < S.edit.w && y < S.edit.h; $('stCell').textContent = inside ? `(${x}, ${y}) ${S.edit.alpha[i] ? 'idx ' + S.edit.idx[i] : 'transparent'}` : ''; }
    if (S.painting && (S.tool === 'pencil' || S.tool === 'eraser')) applyTool(x, y, false);
  };
  view.onmouseleave = () => { S.hover = { x: -1, y: -1 }; S.painting = false; drawView(); };
  window.addEventListener('mouseup', () => { S.painting = false; });
  view.oncontextmenu = ev => { ev.preventDefault(); const { x, y } = viewCell(ev); const t = S.tool; S.tool = 'picker'; applyTool(x, y, false); S.tool = t; };
  $('canvasWrap').addEventListener('wheel', ev => { if (!ev.ctrlKey) return; ev.preventDefault(); setZoom(S.zoom + (ev.deltaY < 0 ? 1 : -1)); }, { passive: false });
  $('zoomIn').onclick = () => setZoom(S.zoom + 1);
  $('zoomOut').onclick = () => setZoom(S.zoom - 1);
  $('showGrid').onchange = () => { S.grid = $('showGrid').checked; drawView(); };

  $('palCanvas').onclick = ev => {
    const r = $('palCanvas').getBoundingClientRect();
    const x = Math.floor((ev.clientX - r.left) / r.width * 16), y = Math.floor((ev.clientY - r.top) / r.height * 16);
    S.color = y * 16 + x; drawPalette();
    if (S.tool === 'eraser' || S.tool === 'picker') setTool('pencil');
  };
  $('btnSave').onclick = saveDraft;
  $('btnApprove').onclick = toggleApprove;
  $('btnDiscard').onclick = discardDraft;
  $('btnRevert').onclick = revertDraft;
  $('btnImport').onclick = () => $('fileImport').click();
  $('fileImport').onchange = () => { const f = $('fileImport').files[0]; if (f) importPng(f); $('fileImport').value = ''; };
  $('candGo').onclick = makeCandidate;
  $('btnExport').onclick = exportApproved;
  $('draftNote').oninput = () => { if (S.edit) { S.dirty = true; updateDraftUi(); } };

  window.addEventListener('keydown', ev => {
    if (ev.target.tagName === 'INPUT' || ev.target.tagName === 'SELECT') return;
    if (ev.ctrlKey && ev.key.toLowerCase() === 'z') { ev.preventDefault(); undo(); return; }
    if (ev.ctrlKey && ev.key.toLowerCase() === 'y') { ev.preventDefault(); redo(); return; }
    if (ev.ctrlKey && ev.key.toLowerCase() === 's') { ev.preventDefault(); saveDraft(); return; }
    const k = ev.key.toLowerCase();
    if (k === 'b') setTool('pencil'); else if (k === 'e') setTool('eraser'); else if (k === 'i') setTool('picker'); else if (k === 'g') setTool('fill');
    else if (k === '+' || k === '=') setZoom(S.zoom + 1); else if (k === '-') setZoom(S.zoom - 1);
  });
  window.addEventListener('beforeunload', ev => { if (S.dirty) { ev.preventDefault(); ev.returnValue = ''; } });
}

function setZoom(z) {
  S.zoom = Math.max(2, Math.min(40, z));
  $('zoomLbl').textContent = S.zoom + 'x';
  drawView();
}

boot().catch(e => { msg('boot failed: ' + e.message, true); console.error(e); });
