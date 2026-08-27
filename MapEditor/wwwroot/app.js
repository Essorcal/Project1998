// NexusTK Map Editor frontend. Renders game-data/maps with the real 5.33 tile atlases the
// backend bakes from Tile.dat, and edits the raw 4.x cell words in place.
//
// Cell model (Server/MapData.cs): 4 bytes/cell [g16][o16] LE. g16 is TAGGED: 0 = void,
// < 0xC000 = TILE.EPF frame directly, >= 0xC000 = legacy sheet-2 frame (remap via Tile533Map
// runs). The top two bits double as the 4.x pass flag, so "sheet 2" IS "blocked".
// o16 is an SObj id; its TILEC frame column draws from the anchor cell growing NORTH, which
// is what makes southern objects occlude the player (walk-behind).
'use strict';
const CELL = 24, S2BASE = 0xC000, PAL_ROWS = 10;
let PAL_COLS = 8, PAL_PAGE = PAL_COLS * PAL_ROWS;
function setPanelWidth(w) {
  w = Math.max(340, Math.min(620, w));
  document.documentElement.style.setProperty('--panelw', w + 'px');
  try { localStorage.setItem('mapeditor.panelw', String(w)); } catch {}
  const cols = Math.max(6, Math.min(18, Math.floor((w - 36) / 31)));
  if (cols !== PAL_COLS) { PAL_COLS = cols; PAL_PAGE = PAL_COLS * PAL_ROWS; S.palPage = 0; }
  if (S.meta) drawPalette();
}
const $ = id => document.getElementById(id);

const S = {
  meta: null, sheet2: new Map(), sheet2Legacy: [],
  groundImg: new Image(), tilecImg: new Image(),
  mode: '4x',
  mapId: null, mapName: '', xs: 0, ys: 0, cells: null, // Uint16Array, interleaved [g,o] (x64 = LE)
  modified: false, savedMark: 0, isDraft: false,       // isDraft: cells came from game-data/maps-edited

  tool: 'brush', tab: 'ground', sheet: 1,
  selWord: 1, selObj: 1, selPass: 3,
  palPage: 0, palMode: 'all', palBlock: null, palAnchorIdx: null,
  favs: (() => { try { return JSON.parse(localStorage.getItem('mapeditor.favs')) || { ground: [], obj: [] }; } catch { return { ground: [], obj: [] }; } })(),
  cam: { x: 0, y: 0 },
  hover: { x: -1, y: -1 },
  drag: null, stroke: null, undoStack: [],
  selection: null, clipboard: null,
  walker: { x: -1, y: -1 }, bump: '',
  selMob: null, placed: [],      // spawn tool: chosen mob + this map's pending points (localStorage)
  warpArm: null,                 // warp tool: the clicked source waiting for its destination
  placedWarps: (() => { try { return JSON.parse(localStorage.getItem('mapeditor.placedWarps')) || []; } catch { return []; } })(),
  selNpc: null,                  // npc tool: the template NPC being copied
  placedNpcs: (() => { try { return JSON.parse(localStorage.getItem('mapeditor.placedNpcs')) || []; } catch { return []; } })(),
  layers: { ground: true, obj: true, pass: false, warp: true, spawn: true, npc: true, override: true, grid: false },
  markers: null,   // /api/map/<id>/markers payload + a byCell index for the hover status line
};

const TOOLS = [
  ['select', 'inspect', '<path d="M5 3l10 7-4.6 1L8 16z"/>'],
  ['marquee', 'select region (drag) — copies on release', '<rect x="4.5" y="4.5" width="11" height="11" stroke-dasharray="2.5 2"/><rect x="3" y="3" width="3" height="3" fill="currentColor" stroke="none"/><rect x="14" y="3" width="3" height="3" fill="currentColor" stroke="none"/><rect x="3" y="14" width="3" height="3" fill="currentColor" stroke="none"/><rect x="14" y="14" width="3" height="3" fill="currentColor" stroke="none"/>'],
  ['stamp', 'paste the copied block', '<path d="M7 7.5a3 3 0 1 1 6 0c0 1.6-1.2 2-1.2 3.5H8.2C8.2 9.5 7 9.1 7 7.5z"/><path d="M4.5 14.5v-1c0-1.2 1-2 2.5-2h6c1.5 0 2.5.8 2.5 2v1z"/><path d="M4.5 17h11"/>'],
  ['brush', 'paint (drag)', '<path d="M3 17l1-4 9.5-9.5a1.77 1.77 0 0 1 2.5 2.5L6.5 15.5 3 17z"/><path d="M12 5l2.5 2.5"/>'],
  ['fill', 'flood fill', '<path d="M9.5 2.5v3"/><path d="M4 10.5l5.5-5.5 5 5-5.5 5.5z"/><path d="M16.8 13.5c.9 1.2.9 2.6 0 3.4-.9-.8-.9-2.2 0-3.4z"/>'],
  ['rect', 'rectangle fill (drag)', '<rect x="4" y="4" width="12" height="12" rx="1" stroke-dasharray="3 2"/>'],
  ['picker', 'eyedropper — pick the tile under the cursor', '<path d="M13.2 2.8a2 2 0 0 1 2.8 0l1.2 1.2a2 2 0 0 1 0 2.8l-2.1 2.1-4-4z"/><path d="M10.6 5.4l4 4-6.4 6.4c-.5.5-1.1.8-1.8.9l-2.6.4.4-2.6c.1-.7.4-1.3.9-1.8z"/><path d="M2.5 17.5l1.8-.5"/>'],
  ['erase', 'erase (ground→void, object→none, pass→walkable)', '<path d="M11.5 3.5l5 5-7 7H6l-2.5-2.5z"/><path d="M8 6.5l5 5"/><path d="M11 17h6"/>'],
  ['pass', 'toggle passability', '<circle cx="10" cy="10" r="7"/><path d="M5.5 5.5l9 9"/>'],
  null,
  ['walk', 'test character', '<circle cx="10" cy="4.5" r="2.2"/><path d="M10 7v5"/><path d="M10 12l-3 5"/><path d="M10 12l3 5"/><path d="M6.5 9.5h7"/>'],
  ['spawn', 'place spawn points — exports Spawns.csv rows, game files never written', '<circle cx="10" cy="12.8" r="3.4"/><circle cx="5.2" cy="8.6" r="1.9"/><circle cx="10" cy="6.6" r="1.9"/><circle cx="14.8" cy="8.6" r="1.9"/>'],
  ['warp', 'place warp pairs — exports Warps.csv rows, game files never written', '<path d="M10 3.2l6.8 6.8-6.8 6.8-6.8-6.8z"/><path d="M7.2 10h5"/><path d="M10.4 8.2l1.8 1.8-1.8 1.8"/>'],
  ['npc', 'place NPCs (copies of an existing NPC) — exports NPCs.csv rows, game files never written', '<circle cx="10" cy="6.2" r="2.6"/><path d="M4.6 16.8c.6-3.6 2.7-5.3 5.4-5.3s4.8 1.7 5.4 5.3"/>'],
];

// --------------------------------------------------------------------------- boot
const bootStage = m => { const el = document.getElementById('loading'); if (el) el.textContent = m; };
async function boot() {
  bootStage('fetching tileset metadata…');
  const mr = await fetch('/api/meta', { cache: 'no-store' });
  if (!mr.ok) throw new Error('meta HTTP ' + mr.status);
  const meta = await mr.json();
  S.meta = meta;
  for (const [legacy, count, s533] of meta.sheet2)
    for (let k = 0; k < count; k++) { S.sheet2.set(legacy + k, s533 + k); S.sheet2Legacy.push(legacy + k); }
  S.sheet2Legacy.sort((a, b) => a - b);
  bootStage('loading ground atlas (12 MB)…');
  await loadImg(S.groundImg, '/api/tiles/ground.png');
  bootStage('loading object atlas (9 MB)…');
  await loadImg(S.tilecImg, '/api/tiles/tilec.png');
  bootStage('building ui…');
  buildRail(); buildMapList(); bindUI();
  bootStage('ui built, configuring…');
  setMode('4x'); setTool('brush'); setTab('ground');
  bootStage('loading map…');
  const q = new URLSearchParams(location.search);
  const qMap = q.get('map') !== null ? meta.maps.find(m => m.file && m.id === +q.get('map')) : null;
  const first = qMap || meta.maps.find(m => m.file && m.id === 0) || meta.maps.find(m => m.file);
  if (first) await loadMap(first.id);
  if (q.get('zoom')) {
    const dpr = devicePixelRatio || 1;
    const raw = Math.max(10, Math.min(500, +q.get('zoom'))) / 100 * dpr;
    setScale(raw >= 1 ? Math.round(raw) : raw);
  }
  if (q.get('at')) {
    const [cx, cy] = q.get('at').split(',').map(Number);
    const v = viewSize();
    S.cam.x = cx * CELL - v.w / 2; S.cam.y = cy * CELL - v.h / 2;
    clampCam();
  }
  if (q.get('walk')) {
    const [wx, wy] = q.get('walk').split(',').map(Number);
    setTool('walk');
    S.walker = { x: wx, y: wy };
  }
  bootStage('map loaded, starting…');
  $('loading').hidden = true;
  requestAnimationFrame(frame);
  // Heartbeat: in --browser mode the server exits itself ~15s after the last open tab
  // stops pinging, so closing the tab is enough to stop the editor.
  setInterval(() => fetch('/api/ping', { method: 'POST' }).catch(() => {}), 3000);
}
const loadImg = (img, src) => new Promise((res, rej) => { img.onload = res; img.onerror = rej; img.src = src; });

// --------------------------------------------------------------------------- map io
async function loadMap(id) {
  if (S.modified && !confirm('Discard unsaved changes?')) return;
  const m = S.meta.maps.find(x => x.id === id);
  if (!m) return;
  const resp = await fetch(`/api/map/${id}`, { cache: 'no-store' });
  const buf = await resp.arrayBuffer();
  S.mapId = id; S.mapName = m.name; S.xs = m.xs; S.ys = m.ys;
  S.isDraft = resp.headers.get('X-Draft') === '1';
  S.cells = new Uint16Array(buf);
  S.cam = { x: 0, y: 0 };
  clampCam();                    // centers maps smaller than the viewport
  S.undoStack = []; S.modified = false; S.savedMark = 0;
  S.selection = null; S.walker = { x: -1, y: -1 };
  S.markers = null;
  loadMarkers(id);               // async — the overlay pops in when it arrives
  S.placed = placedStore()[id] || [];
  updateSpawnBox();
  mini.world = null; rebuildMini();
  $('lintList').innerHTML = ''; $('lintNote').textContent = '';
  buildMapList();
  drawPalette();                 // the On-map palette follows the newly loaded map
  invalidate(); updateStatus(); updateButtons();
}

// Save writes a DRAFT (game-data/maps-edited/) — the shipped map is never touched.
// Publishing a map into game-data/maps is a deliberate manual copy outside the editor.
async function saveMap() {
  if (S.mapId === null || !S.modified) return;
  const r = await fetch(`/api/map/${S.mapId}`, { method: 'PUT', body: S.cells.buffer });
  if (r.ok) {
    S.modified = false; S.savedMark = S.undoStack.length; S.isDraft = true;
    const m = S.meta.maps.find(x => x.id === S.mapId);
    if (m && !m.draft) { m.draft = true; buildMapList(); }
    flashHint('draft saved to dist/NexusTK-Map-Editor/saved/maps — the shipped map is untouched');
  }
  else flashHint('save failed: ' + await r.text());
  updateStatus(); updateButtons();
}

async function discardDraft() {
  if (S.mapId === null || !S.isDraft) return;
  const m = S.meta.maps.find(x => x.id === S.mapId);
  const ask = m && m.custom
    ? `TK${S.mapId} is a NEW map that exists only as this draft — delete the map entirely?`
    : `Delete the draft of TK${S.mapId} and reload the shipped map?`;
  if (!confirm(ask)) return;
  const r = await fetch(`/api/map/${S.mapId}/draft`, { method: 'DELETE' });
  if (!r.ok && r.status !== 404) { flashHint('discard failed: ' + await r.text()); return; }
  const res = r.ok ? await r.json().catch(() => ({})) : {};
  S.modified = false;                 // skip the unsaved-changes prompt — discarding is the point
  if (res.removedMap) {               // a new map is gone entirely; land somewhere real
    S.meta.maps = S.meta.maps.filter(x => x.id !== S.mapId);
    S.mapId = null;
    buildMapList();
    const first = S.meta.maps.find(x => x.file);
    if (first) await loadMap(first.id);
    flashHint('new map deleted');
    return;
  }
  if (m) m.draft = false;
  await loadMap(S.mapId);
  flashHint('draft discarded — shipped map loaded');
}

// New map: a blank all-void draft + a row in saved/new-maps.csv (an exact map_index.csv
// row) — publishing is the usual deliberate append + copy, documented in the README.
async function newMap() {
  if (!S.meta) return;
  let suggest = 9000;                 // high ids keep clear of upstream's ranges
  while (S.meta.maps.some(m => m.id === suggest)) suggest++;
  const idStr = prompt('New map id (unused number; high ids avoid colliding with upstream content):', String(suggest));
  if (!idStr) return;
  const id = parseInt(idStr, 10);
  if (!Number.isFinite(id)) { flashHint('not a number: ' + idStr); return; }
  const name = prompt('Map name:', '');
  if (name === null) return;
  const dims = prompt('Dimensions W,H (5–255 each):', '100,100');
  if (!dims) return;
  const [xs, ys] = dims.split(/[,x×\s]+/).map(Number);
  const r = await fetch('/api/maps', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id, name, xs, ys }),
  });
  if (!r.ok) { alert(await r.text()); return; }
  const m = await r.json();
  S.meta.maps.push({ id: m.id, name: m.name, xs: m.xs, ys: m.ys, file: true, draft: true, custom: true });
  S.meta.maps.sort((a, b) => a.id - b.id);
  S.modified = false;
  await loadMap(m.id);
  flashHint(`TK${m.id} "${m.name}" created — an all-void canvas; paint ground, then Save (publishing: see the README)`);
}

async function importFile(file) {
  const bytes = await file.arrayBuffer();
  const head = new Uint8Array(bytes.slice(0, 4));
  let url = '/api/import';
  const isCmp = head[0] === 67 && head[1] === 77 && head[2] === 65 && head[3] === 80;
  if (!isCmp) {
    let xs = S.xs, ys = S.ys;
    if (bytes.byteLength !== xs * ys * 4) {
      const ans = prompt(`Headerless .map (${bytes.byteLength} bytes = ${bytes.byteLength / 4} cells). Dimensions W,H:`, '');
      if (!ans) return;
      [xs, ys] = ans.split(/[,x×\s]+/).map(Number);
    }
    url += `?xs=${xs}&ys=${ys}`;
  }
  const r = await fetch(url, { method: 'POST', body: bytes });
  if (!r.ok) { alert(await r.text()); return; }
  const xs = +r.headers.get('X-Xs'), ys = +r.headers.get('X-Ys');
  if (xs !== S.xs || ys !== S.ys) {
    alert(`Imported ${xs}×${ys} but the selected map is ${S.xs}×${S.ys}.\nSelect a map with matching dimensions first — the import replaces its cells.`);
    return;
  }
  const words = new Uint16Array(await r.arrayBuffer());
  beginStroke();
  for (let i = 0; i < S.xs * S.ys; i++) setCell(i, words[i * 2], words[i * 2 + 1]);
  endStroke();
  flashHint(`imported ${file.name}`);
}

// World markers (read-only): cells that Warps.csv / WorldMapTriggers.csv / Spawns.csv /
// AreaSpawns.csv / NPCs.csv point at, so nobody paints over a warp tile or a spawn point
// without knowing. byCell backs the hover status line; the raw lists back the draw pass.
async function loadMarkers(id) {
  try {
    const r = await fetch(`/api/map/${id}/markers`, { cache: 'no-store' });
    if (!r.ok) return;
    const m = await r.json();
    if (S.mapId !== id) return;                    // user already switched maps
    const byCell = new Map();
    const note = (x, y, text) => {
      if (x < 0 || y < 0 || x >= S.xs || y >= S.ys) return;
      const k = idx(x, y);
      let a = byCell.get(k);
      if (!a) byCell.set(k, a = []);
      a.push(text);
    };
    const mapLbl = w => (w.name || 'TK' + w.m) + ` (${w.m})`;
    for (const w of m.warpsOut) note(w.x, w.y, `warp → ${mapLbl(w)} ${w.dx},${w.dy}`);
    for (const w of m.warpsIn) note(w.x, w.y, `arrival ← ${mapLbl(w)}`);
    for (const c of m.world) note(c.x, c.y, 'world-map trigger');
    for (const a of m.worldArrivals) note(a.x, a.y, `world-map arrival · ${a.name}`);
    for (const s of m.spawns) note(s.x, s.y, `spawn: ${s.name || 'mob ' + s.mob}`);
    for (const p of m.npcs) note(p.x, p.y, `npc: ${p.name}`);
    for (const c of m.overrides) {
      const parts = [];
      if (c.tile !== null) parts.push('tile ' + c.tile);
      if (c.pass !== null) parts.push('pass ' + c.pass);
      if (c.obj !== null) parts.push('obj ' + c.obj);
      note(c.x, c.y, `override → ${parts.join(', ') || '(empty row)'}${c.src ? ` (${c.src})` : ''}`);
    }
    S.markers = { ...m, byCell };
    const wide = m.areas.filter(a => !a.x0 && !a.y0 && !a.x1 && !a.y1);
    const boxes = m.areas.length - wide.length;
    $('warpNote').textContent = m.warpsOut.length + m.warpsIn.length + m.world.length
      ? `${m.warpsOut.length}→ ${m.warpsIn.length}←` : '';
    $('spawnNote').textContent = m.spawns.length + m.areas.length
      ? `${m.spawns.length}·${boxes}·${wide.length}` : '';
    $('spawnNote').title = `${m.spawns.length} points · ${boxes} boxes · ${wide.length} map-wide`
      + (wide.length ? '\nmap-wide (anywhere walkable): ' + wide.map(a => `${a.name || 'mob ' + a.mob} ×${a.count}`).join(', ') : '');
    $('npcNote').textContent = m.npcs.length || '';
    $('overrideNote').textContent = m.overrides.length || '';
    invalidate(); updateStatus();
  } catch (e) { console.error('markers', e); }
}

// --------------------------------------------------------------------------- cell model
const idx = (x, y) => y * S.xs + x;
const gAt = i => S.cells[i * 2];
const oAt = i => S.cells[i * 2 + 1];
const blockedWord = g => g >= S2BASE;
function groundFrame(g) {
  if (!g) return 0;
  if (g >= S2BASE) return S.sheet2.get(g - S2BASE) || 0;
  return g < S.meta.groundCount ? g : 0;
}

function beginStroke() { S.stroke = new Map(); }
function setCell(i, g, o) {
  if (gAt(i) === g && oAt(i) === o) return;
  if (S.stroke && !S.stroke.has(i)) S.stroke.set(i, [gAt(i), oAt(i)]);
  S.cells[i * 2] = g; S.cells[i * 2 + 1] = o;
  invalidate();
}
function endStroke() {
  if (S.stroke && S.stroke.size) {
    S.undoStack.push(S.stroke);
    if (S.undoStack.length > 200) { S.undoStack.shift(); S.savedMark--; }
    S.modified = true;
  }
  S.stroke = null; updateStatus(); updateButtons(); scheduleMini();
}
function undo() {
  const st = S.undoStack.pop();
  if (!st) return;
  for (const [i, [g, o]] of st) { S.cells[i * 2] = g; S.cells[i * 2 + 1] = o; }
  S.modified = S.undoStack.length !== S.savedMark;
  invalidate(); updateStatus(); updateButtons(); scheduleMini();
}

function applyAt(x, y) {
  if (x < 0 || y < 0 || x >= S.xs || y >= S.ys) return;
  const i = idx(x, y), g = gAt(i), o = oAt(i);
  switch (S.tool) {
    case 'brush':
      if (S.palBlock && S.tab !== 'pass') { paintBlockAt(x, y); break; }
      if (S.tab === 'ground') setCell(i, S.selWord, o);
      else if (S.tab === 'obj') setCell(i, g, S.selObj);
      else setCell(i, (g & 0x3FFF) | (S.selPass << 14), o);
      break;
    case 'erase':
      if (S.tab === 'ground') setCell(i, 0, o);
      else if (S.tab === 'obj') setCell(i, g, 0);
      else setCell(i, g & 0x3FFF, o);
      break;
    case 'pass':
      setCell(i, blockedWord(g) ? g & 0x3FFF : g | S2BASE, o);
      break;
  }
}

function flood(x, y) {
  const i0 = idx(x, y), g0 = gAt(i0), o0 = oAt(i0);
  const match = S.tab === 'ground' ? i => gAt(i) === g0
    : S.tab === 'obj' ? i => oAt(i) === o0
    : i => blockedWord(gAt(i)) === blockedWord(g0);
  const seen = new Uint8Array(S.xs * S.ys), stack = [[x, y]];
  while (stack.length) {
    const [cx, cy] = stack.pop();
    if (cx < 0 || cy < 0 || cx >= S.xs || cy >= S.ys) continue;
    const i = idx(cx, cy);
    if (seen[i] || !match(i)) continue;
    seen[i] = 1;
    applyAt(cx, cy);
    stack.push([cx + 1, cy], [cx - 1, cy], [cx, cy + 1], [cx, cy - 1]);
  }
}

function pick(x, y) {
  const i = idx(x, y), g = gAt(i), o = oAt(i);
  if (o > 0) { setTab('obj'); S.selObj = o; S.palPage = Math.floor((o - 1) / PAL_PAGE); }
  else if (g >= S2BASE) {
    setTab('ground'); S.sheet = 2; S.selWord = g;
    const li = S.sheet2Legacy.indexOf(g - S2BASE);
    if (li >= 0) S.palPage = Math.floor(li / PAL_PAGE);
  } else {
    setTab('ground'); S.sheet = 1; S.selWord = g;
    S.palPage = Math.floor(Math.max(0, g - 1) / PAL_PAGE);
  }
  setTool('brush'); drawPalette();
}

function pasteAt(x, y) {
  const c = S.clipboard;
  if (!c) { flashHint('nothing copied — use the marquee first'); return; }
  beginStroke();
  for (let dy = 0; dy < c.h; dy++) for (let dx = 0; dx < c.w; dx++) {
    const tx = x + dx, ty = y + dy;
    if (tx >= S.xs || ty >= S.ys) continue;
    setCell(idx(tx, ty), c.cells[(dy * c.w + dx) * 2], c.cells[(dy * c.w + dx) * 2 + 1]);
  }
  endStroke();
}

function copySelection() {
  const s = S.selection;
  if (!s) return;
  const w = s.x1 - s.x0 + 1, h = s.y1 - s.y0 + 1;
  const cells = new Uint16Array(w * h * 2);
  for (let dy = 0; dy < h; dy++) for (let dx = 0; dx < w; dx++) {
    const i = idx(s.x0 + dx, s.y0 + dy);
    cells[(dy * w + dx) * 2] = gAt(i); cells[(dy * w + dx) * 2 + 1] = oAt(i);
  }
  S.clipboard = { w, h, cells };
  flashHint(`copied ${w}×${h} — stamp tool pastes it`);
}

function moveWalker(dx, dy) {
  const w = S.walker;
  if (w.x < 0) return;
  const nx = w.x + dx, ny = w.y + dy;
  if (nx < 0 || ny < 0 || nx >= S.xs || ny >= S.ys || blockedWord(gAt(idx(nx, ny)))) {
    S.bump = dx < 0 ? '←' : dx > 0 ? '→' : dy < 0 ? '↑' : '↓';
  } else {
    w.x = nx; w.y = ny; S.bump = '';
    const cvs = $('view');
    const px = nx * CELL - S.cam.x, py = ny * CELL - S.cam.y;   // keep in view
    if (px < 60) S.cam.x = nx * CELL - 60;
    if (py < 60) S.cam.y = ny * CELL - 60;
    if (px > cvs.clientWidth - 84) S.cam.x = nx * CELL - cvs.clientWidth + 84;
    if (py > cvs.clientHeight - 84) S.cam.y = ny * CELL - cvs.clientHeight + 84;
    clampCam();
  }
  invalidate(); updateStatus();
}

// --------------------------------------------------------------------------- render
let needsDraw = true;
const invalidate = () => { needsDraw = true; };
function frame() {
  try {
    if (needsDraw) { needsDraw = false; draw(); }
  } catch (e) {
    const el = $('loading');
    el.hidden = false;
    el.textContent = 'draw error: ' + (e && e.stack || e);
    console.error(e);
    return;                       // stop the loop; the overlay now shows why
  }
  requestAnimationFrame(frame);
}

// Tiles are pixel art: the world must land on WHOLE device pixels or every tile boundary gets
// nearest-neighbor resampled (at Windows 125% display scale that reads as "everything slightly
// shifted"). So the canvas backs at full device resolution and the world draws at an INTEGER
// device-pixel scale — S.scale, default round(dpr) — never at fractional dpr.
function viewScale() { return S.scale || Math.max(1, Math.round(devicePixelRatio || 1)); }
// Zoom ladder: fractional steps for whole-map overviews (smoothed), integer device scales
// for pixel-crisp editing. ~10% shows all of a 220x220 map at once.
function scaleLadder() {
  const l = [0.125, 0.25, 0.5];
  for (let i = 1; i <= maxScale(); i++) l.push(i);
  return l;
}
function stepScale(dir) {
  const l = scaleLadder(), s = viewScale();
  let i = l.length - 1;
  for (let k = 0; k < l.length; k++) if (l[k] >= s - 1e-6) { i = k; break; }
  setScale(l[Math.max(0, Math.min(l.length - 1, i + dir))]);
}
function maxScale() { return Math.max(4, Math.floor(5 * (devicePixelRatio || 1))); }   // caps zoom at ~500%
function setScale(ns) {
  ns = Math.max(0.125, Math.min(maxScale(), ns));
  if (ns === viewScale()) return;
  const before = viewSize();
  const cx = S.cam.x + before.w / 2, cy = S.cam.y + before.h / 2;
  S.scale = ns;
  const after = viewSize();
  S.cam.x = cx - after.w / 2; S.cam.y = cy - after.h / 2;
  clampCam(); invalidate();
}
function viewSize() {
  const cvs = $('view'), dpr = devicePixelRatio || 1, s = viewScale();
  return { w: cvs.clientWidth * dpr / s, h: cvs.clientHeight * dpr / s };
}

function clampCam() {
  const v = viewSize();
  S.cam.x = Math.max(0, Math.min(S.cam.x, S.xs * CELL - v.w));
  S.cam.y = Math.max(0, Math.min(S.cam.y, S.ys * CELL - v.h));
  if (S.xs * CELL < v.w) S.cam.x = (S.xs * CELL - v.w) / 2;
  if (S.ys * CELL < v.h) S.cam.y = (S.ys * CELL - v.h) / 2;
}

function draw() {
  const cvs = $('view'), dpr = devicePixelRatio || 1, s = viewScale();
  if (cvs.width !== Math.round(cvs.clientWidth * dpr) || cvs.height !== Math.round(cvs.clientHeight * dpr)) {
    cvs.width = Math.round(cvs.clientWidth * dpr); cvs.height = Math.round(cvs.clientHeight * dpr);
  }
  const w = cvs.width / s, h = cvs.height / s;
  const ctx = cvs.getContext('2d');
  ctx.setTransform(s, 0, 0, s, 0, 0);
  ctx.imageSmoothingEnabled = s < 1;   // smooth when zoomed out; chunky-crisp at 100%+
  ctx.fillStyle = '#0e0f10';
  ctx.fillRect(0, 0, w, h);
  const zpct = `${Math.round(s / dpr * 100)}%`;
  $('stZoom').textContent = 'zoom ' + zpct;
  if (document.activeElement !== $('zoomLbl')) $('zoomLbl').value = zpct;
  if (!S.cells) return;

  const AC = S.meta.atlasCols;
  const camX = Math.round(S.cam.x), camY = Math.round(S.cam.y);
  const x0 = Math.max(0, Math.floor(camX / CELL)), y0 = Math.max(0, Math.floor(camY / CELL));
  const x1 = Math.min(S.xs - 1, Math.ceil((camX + w) / CELL)), y1 = Math.min(S.ys - 1, Math.ceil((camY + h) / CELL));

  if (S.layers.ground)
    for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
      const fr = groundFrame(gAt(idx(x, y)));
      if (fr > 0)
        ctx.drawImage(S.groundImg, fr % AC * CELL, Math.floor(fr / AC) * CELL, CELL, CELL,
          x * CELL - camX, y * CELL - camY, CELL, CELL);
    }

  if (S.layers.obj) {
    const yMax = Math.min(S.ys - 1, y1 + 16);        // southern anchors whose columns reach into view
    const drawObjRow = y => {
      for (let x = x0; x <= x1; x++) {
        const o = oAt(idx(x, y));
        if (!o) continue;
        const fids = S.meta.objs[o];
        if (!fids) continue;
        for (let k = 0; k < fids.length; k++) {
          const ry = y - k;
          const dy = ry * CELL - camY;
          if (dy < -CELL || dy > h) continue;
          const fid = fids[k];
          ctx.drawImage(S.tilecImg, fid % AC * CELL, Math.floor(fid / AC) * CELL, CELL, CELL,
            x * CELL - camX, dy, CELL, CELL);
        }
      }
    };
    const wy = S.walker.y;
    for (let y = y0; y <= yMax; y++) {
      if (y === wy && S.walker.x >= 0) drawWalker(ctx, camX, camY, false);
      drawObjRow(y);
    }
    if ((wy < y0 || wy > yMax) && S.walker.x >= 0) drawWalker(ctx, camX, camY, false);
    if (S.walker.x >= 0) drawWalker(ctx, camX, camY, true);   // client-style translucent ghost over occluders
  } else if (S.walker.x >= 0) drawWalker(ctx, camX, camY, false);

  if (S.layers.pass) {
    ctx.fillStyle = 'rgba(190,60,45,0.42)';
    for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++)
      if (blockedWord(gAt(idx(x, y)))) ctx.fillRect(x * CELL - camX, y * CELL - camY, CELL, CELL);
  }

  if (S.layers.grid) {
    ctx.strokeStyle = 'rgba(255,255,255,0.07)';
    ctx.beginPath();
    for (let x = x0; x <= x1 + 1; x++) { ctx.moveTo(x * CELL - camX + .5, 0); ctx.lineTo(x * CELL - camX + .5, h); }
    for (let y = y0; y <= y1 + 1; y++) { ctx.moveTo(0, y * CELL - camY + .5); ctx.lineTo(w, y * CELL - camY + .5); }
    ctx.stroke();
  }

  drawMarkers(ctx, camX, camY, s);

  const accent = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();

  if (S.drag && (S.drag.kind === 'marquee' || S.drag.kind === 'rect')) {
    const r = normRect(S.drag.start, S.drag.cur);
    ctx.strokeStyle = accent; ctx.setLineDash([5, 4]); ctx.lineWidth = 2;
    ctx.strokeRect(r.x0 * CELL - camX + 1, r.y0 * CELL - camY + 1, (r.x1 - r.x0 + 1) * CELL - 2, (r.y1 - r.y0 + 1) * CELL - 2);
    ctx.setLineDash([]);
  }
  if (S.selection) {
    const r = S.selection;
    ctx.strokeStyle = accent; ctx.setLineDash([5, 4]); ctx.lineWidth = 2;
    ctx.strokeRect(r.x0 * CELL - camX + 1, r.y0 * CELL - camY + 1, (r.x1 - r.x0 + 1) * CELL - 2, (r.y1 - r.y0 + 1) * CELL - 2);
    ctx.setLineDash([]);
    ctx.fillStyle = accent.replace(')', ',0.08)').replace('rgb', 'rgba');
  }

  if (S.tool === 'stamp' && S.clipboard && S.hover.x >= 0) {
    ctx.globalAlpha = 0.55;
    const c = S.clipboard;
    for (let dy = 0; dy < c.h; dy++) for (let dx = 0; dx < c.w; dx++) {
      const fr = groundFrame(c.cells[(dy * c.w + dx) * 2]);
      if (fr > 0)
        ctx.drawImage(S.groundImg, fr % AC * CELL, Math.floor(fr / AC) * CELL, CELL, CELL,
          (S.hover.x + dx) * CELL - camX, (S.hover.y + dy) * CELL - camY, CELL, CELL);
    }
    ctx.globalAlpha = 1;
    ctx.strokeStyle = accent;
    ctx.strokeRect(S.hover.x * CELL - camX, S.hover.y * CELL - camY, c.w * CELL, c.h * CELL);
  }

  if (S.hover.x >= 0 && S.tool !== 'stamp') {
    ctx.strokeStyle = accent; ctx.lineWidth = 2;
    ctx.strokeRect(S.hover.x * CELL - camX + 1, S.hover.y * CELL - camY + 1, CELL - 2, CELL - 2);
  }

  drawMini();
}

// --------------------------------------------------------------------------- spawn placement
// Development-tool rule: placements live in localStorage only, and Export downloads
// Spawns.csv rows for a deliberate hand-append — game files are never written.
function placedStore() { try { return JSON.parse(localStorage.getItem('mapeditor.placed')) || {}; } catch { return {}; } }
function savePlaced() {
  const all = placedStore();
  if (S.placed.length) all[S.mapId] = S.placed; else delete all[S.mapId];
  try { localStorage.setItem('mapeditor.placed', JSON.stringify(all)); } catch {}
}

function placeSpawnAt(x, y) {
  const i = S.placed.findIndex(p => p.x === x && p.y === y);
  if (i >= 0) { S.placed.splice(i, 1); flashHint('spawn point removed'); }
  else if (!S.selMob) { flashHint('pick a mob in the spawn box first'); return; }
  else if (blockedWord(gAt(idx(x, y)))) { flashHint('blocked cell — the mob would stand in a wall'); return; }
  else S.placed.push({ x, y, mob: S.selMob.id, name: S.selMob.name });
  savePlaced(); updateSpawnBox(); invalidate(); updateStatus();
}

function updateSpawnBox() {
  $('sbCount').textContent = S.placed.length ? `${S.placed.length} pending` : '';
  $('sbSel').textContent = S.selMob ? `${S.selMob.name} (${S.selMob.id})` : 'none';
  $('sbExport').disabled = !S.placed.length;
  $('sbClear').disabled = !S.placed.length;
}

function buildMobList() {
  const q = ($('sbSearch').value || '').toLowerCase();
  const list = $('sbList');
  list.innerHTML = '';
  let shown = 0;
  for (const m of S.meta.mobs) {
    if (q && !(`${m.id} ${m.name}`.toLowerCase().includes(q))) continue;
    if (++shown > 60) break;
    const d = document.createElement('div');
    d.className = 'mobrow' + (S.selMob && S.selMob.id === m.id ? ' on' : '');
    d.innerHTML = `<span class="name">${m.name || '(unnamed)'}</span><span class="mono dim">${m.id}</span>`;
    d.onclick = () => { S.selMob = m; buildMobList(); updateSpawnBox(); updateStatus(); };
    list.appendChild(d);
  }
}

async function exportSpawns() {
  if (S.mapId === null || !S.placed.length) return;
  const r = await fetch(`/api/map/${S.mapId}/spawns.csv`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(S.placed.map(p => ({ x: p.x, y: p.y, mob: p.mob }))),
  });
  if (!r.ok) { flashHint('export failed: ' + await r.text()); return; }
  flashHint(`${S.placed.length} Spawns.csv row${S.placed.length === 1 ? '' : 's'} → ${r.headers.get('X-Saved')} — append by hand, then @reload`);
}

// --------------------------------------------------------------------------- npc placement
// Each pending NPC is a COPY of an existing NPCs.csv row (the template) at a new cell —
// look/type/flags are what the editor can't author, so they come from the template; the
// identifier/description can be overridden (scripts key off the identifier, so keeping
// the template's identifier reuses its dialog/shop wiring). Same contract as the other
// placement tools: localStorage only, Export writes rows to the tool's saved folder.
function saveNpcs() { try { localStorage.setItem('mapeditor.placedNpcs', JSON.stringify(S.placedNpcs)); } catch {} }

function placeNpcAt(x, y) {
  const i = S.placedNpcs.findIndex(p => p.map === S.mapId && p.x === x && p.y === y);
  if (i >= 0) { S.placedNpcs.splice(i, 1); flashHint('pending NPC removed'); }
  else if (!S.selNpc) { flashHint('pick a template NPC in the box first'); return; }
  else {
    // no blocked-cell refusal: the server deliberately stands NPCs where NPCs.csv says, wall or not
    S.placedNpcs.push({
      map: S.mapId, x, y, template: S.selNpc.id,
      identifier: $('nbIdent').value.trim(), description: $('nbDesc').value.trim(),
    });
  }
  saveNpcs(); updateNpcBox(); invalidate(); updateStatus();
}

function updateNpcBox() {
  $('nbCount').textContent = S.placedNpcs.length ? `${S.placedNpcs.length} pending` : '';
  $('nbSel').textContent = S.selNpc ? `${S.selNpc.name || S.selNpc.ident} (${S.selNpc.id}, look ${S.selNpc.look})` : 'none';
  $('nbExport').disabled = !S.placedNpcs.length;
  $('nbClear').disabled = !S.placedNpcs.length;
}

function buildNpcList() {
  const q = ($('nbSearch').value || '').toLowerCase();
  const list = $('nbList');
  list.innerHTML = '';
  let shown = 0;
  for (const n of S.meta.npcTemplates) {
    if (q && !(`${n.id} ${n.ident} ${n.name}`.toLowerCase().includes(q))) continue;
    if (++shown > 60) break;
    const d = document.createElement('div');
    d.className = 'mobrow' + (S.selNpc && S.selNpc.id === n.id ? ' on' : '');
    d.title = `${n.ident} — lives on TK${n.map}`;
    d.innerHTML = `<span class="name">${n.name || n.ident || '(unnamed)'}</span><span class="mono dim">${n.id}</span>`;
    d.onclick = () => {
      S.selNpc = n;
      $('nbIdent').value = n.ident;
      $('nbDesc').value = n.name;
      buildNpcList(); updateNpcBox(); updateStatus();
    };
    list.appendChild(d);
  }
}

async function exportNpcs() {
  if (!S.placedNpcs.length) return;
  const r = await fetch('/api/npcs.csv', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(S.placedNpcs),
  });
  if (!r.ok) { flashHint('export failed: ' + await r.text()); return; }
  flashHint(`${S.placedNpcs.length} NPCs.csv row${S.placedNpcs.length === 1 ? '' : 's'} → ${r.headers.get('X-Saved')} — append by hand, then @reload`);
}

// --------------------------------------------------------------------------- warp placement
// Two clicks make a leg: source cell, then destination cell (switch maps freely between
// them). No auto-reverse: in Warps.csv only 1 of ~4600 rows is an exact mirror — real
// return legs are separate doorway cells the mapper places, so the tool nudges ("one-way")
// instead of guessing. Pairs are global (they span maps) and live in localStorage;
// Export writes Warps.csv rows to the tool's saved folder — game files never written.
function saveWarps() { try { localStorage.setItem('mapeditor.placedWarps', JSON.stringify(S.placedWarps)); } catch {} }

function placeWarpAt(x, y) {
  if (!S.warpArm) {
    // click on a pending endpoint on this map removes that pair
    const i = S.placedWarps.findIndex(w => (w.sm === S.mapId && w.sx === x && w.sy === y)
      || (w.dm === S.mapId && w.dx === x && w.dy === y));
    if (i >= 0) {
      S.placedWarps.splice(i, 1);
      flashHint('warp pair removed');
    } else {
      S.warpArm = { m: S.mapId, x, y, name: S.mapName };
      flashHint('source set — now click the destination cell (switch maps if needed, Esc cancels)');
    }
  } else if (S.warpArm.m === S.mapId && S.warpArm.x === x && S.warpArm.y === y) {
    S.warpArm = null;               // clicking the armed source again cancels it
  } else {
    if (blockedWord(gAt(idx(x, y)))) flashHint('note: this arrival cell is blocked — players would land in a wall');
    const pair = {
      sm: S.warpArm.m, sx: S.warpArm.x, sy: S.warpArm.y, sname: S.warpArm.name,
      dm: S.mapId, dx: x, dy: y, dname: S.mapName,
    };
    S.placedWarps.push(pair);
    S.warpArm = null;
    if (!S.placedWarps.some(o => o.sm === pair.dm && o.dm === pair.sm))
      flashHint('pair added — remember the return leg (a one-way door strands players) unless Warps.csv already has one');
  }
  saveWarps(); updateWarpBox(); invalidate(); updateStatus();
}

function updateWarpBox() {
  $('wbCount').textContent = S.placedWarps.length ? `${S.placedWarps.length} pending` : '';
  $('wbStep').textContent = S.warpArm
    ? `source: ${S.warpArm.name || 'TK' + S.warpArm.m} (${S.warpArm.x},${S.warpArm.y}) — click the destination`
    : 'click the SOURCE cell';
  $('wbExport').disabled = !S.placedWarps.length;
  $('wbClear').disabled = !S.placedWarps.length;
  const list = $('wbList');
  list.innerHTML = '';
  for (const [i, w] of S.placedWarps.entries()) {
    const back = S.placedWarps.some(o => o.sm === w.dm && o.dm === w.sm);
    const d = document.createElement('div');
    d.className = 'mobrow';
    d.title = 'click to jump to the landing cell' + (back ? '' : ' — ⚠ no pending return leg (fine if Warps.csv already has one)');
    const label = document.createElement('span');
    label.className = 'name';
    label.textContent = `${back ? '' : '⚠ '}TK${w.sm} (${w.sx},${w.sy}) → TK${w.dm} (${w.dx},${w.dy})`;
    const rm = document.createElement('span');
    rm.className = 'mono dim';
    rm.textContent = '✕';
    rm.title = 'remove this pair';
    rm.onclick = e => { e.stopPropagation(); S.placedWarps.splice(i, 1); saveWarps(); updateWarpBox(); invalidate(); };
    d.onclick = async () => {   // jump to the landing — the natural spot to start the return leg
      if (S.mapId !== w.dm) await loadMap(w.dm);
      if (S.mapId !== w.dm) return;   // load refused (unsaved changes prompt)
      const v = viewSize();
      S.cam.x = w.dx * CELL - v.w / 2; S.cam.y = w.dy * CELL - v.h / 2;
      clampCam(); invalidate();
    };
    d.append(label, rm);
    list.appendChild(d);
  }
}

async function exportWarps() {
  if (!S.placedWarps.length) return;
  const r = await fetch('/api/warps.csv', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(S.placedWarps.map(w => ({ sm: w.sm, sx: w.sx, sy: w.sy, dm: w.dm, dx: w.dx, dy: w.dy }))),
  });
  if (!r.ok) { flashHint('export failed: ' + await r.text()); return; }
  flashHint(`${S.placedWarps.length} Warps.csv row${S.placedWarps.length === 1 ? '' : 's'} → ${r.headers.get('X-Saved')} — append by hand, then @reload`);
}

// --------------------------------------------------------------------------- minimap
// One offscreen pixel per cell (ground + the object's anchor frame, GPU-downsampled from
// the atlases), rebuilt on load and ~300ms after the last edit; the on-panel canvas blits
// it each frame with marker pixels and the viewport rectangle on top.
const mini = { world: null, timer: null };
function scheduleMini() { clearTimeout(mini.timer); mini.timer = setTimeout(rebuildMini, 300); }
function rebuildMini() {
  if (!S.cells) return;
  const AC = S.meta.atlasCols;
  const w = document.createElement('canvas');
  w.width = S.xs; w.height = S.ys;
  const g = w.getContext('2d');
  g.imageSmoothingEnabled = true; g.imageSmoothingQuality = 'high';
  for (let y = 0; y < S.ys; y++) for (let x = 0; x < S.xs; x++) {
    const i = idx(x, y);
    const fr = groundFrame(gAt(i));
    if (fr > 0) g.drawImage(S.groundImg, fr % AC * CELL, (fr / AC | 0) * CELL, CELL, CELL, x, y, 1, 1);
    const o = oAt(i);
    const fids = o && S.meta.objs[o];
    if (fids && fids.length) g.drawImage(S.tilecImg, fids[0] % AC * CELL, (fids[0] / AC | 0) * CELL, CELL, CELL, x, y, 1, 1);
  }
  mini.world = w;
  invalidate();
}
function drawMini() {
  const cvs = $('miniCanvas');
  if (!cvs || !mini.world || !S.cells || $('miniWrap').hidden) return;
  const dpr = devicePixelRatio || 1;
  // Size the canvas ELEMENT to exactly the drawn map — no letterbox, so a click maps to
  // a cell by plain proportion of the element's rect.
  const k = Math.min(208 / S.xs, 176 / S.ys);                // css px per cell, corner-box cap
  const cw = Math.max(24, Math.round(S.xs * k)), ch = Math.max(24, Math.round(S.ys * k));
  if (cvs.style.width !== cw + 'px') cvs.style.width = cw + 'px';
  if (cvs.style.height !== ch + 'px') cvs.style.height = ch + 'px';
  const bw = Math.round(cw * dpr), bh = Math.round(ch * dpr);
  if (cvs.width !== bw || cvs.height !== bh) { cvs.width = bw; cvs.height = bh; }
  const g = cvs.getContext('2d');
  const sx = bw / S.xs, sy = bh / S.ys;
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.imageSmoothingEnabled = false;
  g.fillStyle = '#0e0f10'; g.fillRect(0, 0, bw, bh);
  g.drawImage(mini.world, 0, 0, bw, bh);
  const px = Math.max(1, Math.ceil(sx)), py = Math.max(1, Math.ceil(sy));
  const dot = (x, y, c) => { g.fillStyle = c; g.fillRect(Math.floor(x * sx), Math.floor(y * sy), px, py); };
  if (S.markers) {
    if (S.layers.override) for (const c of S.markers.overrides) dot(c.x, c.y, '#ec4899');
    if (S.layers.spawn) for (const s of S.markers.spawns) dot(s.x, s.y, '#f97316');
    if (S.layers.npc) for (const p of S.markers.npcs) dot(p.x, p.y, '#4ade80');
    if (S.layers.warp) {
      for (const c of S.markers.world) dot(c.x, c.y, '#a78bfa');
      for (const w of S.markers.warpsIn) dot(w.x, w.y, '#38bdf8');
      for (const w of S.markers.warpsOut) dot(w.x, w.y, '#38bdf8');
    }
  }
  for (const p of S.placed) dot(p.x, p.y, '#eab308');
  for (const w of S.placedWarps) {
    if (w.sm === S.mapId) dot(w.sx, w.sy, '#67e8f9');
    if (w.dm === S.mapId) dot(w.dx, w.dy, '#67e8f9');
  }
  for (const p of S.placedNpcs) if (p.map === S.mapId) dot(p.x, p.y, '#00e676');
  const v = viewSize();
  g.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();
  g.lineWidth = 1;
  g.strokeRect(S.cam.x / CELL * sx + .5, S.cam.y / CELL * sy + .5,
    Math.min(v.w, S.xs * CELL) / CELL * sx - 1, Math.min(v.h, S.ys * CELL) / CELL * sy - 1);
}
function bindMini() {
  const cvs = $('miniCanvas');
  const jump = e => {
    const r = cvs.getBoundingClientRect();
    const v = viewSize();
    S.cam.x = (e.clientX - r.left) / r.width * S.xs * CELL - v.w / 2;
    S.cam.y = (e.clientY - r.top) / r.height * S.ys * CELL - v.h / 2;
    clampCam(); invalidate();
  };
  let down = false;
  cvs.addEventListener('pointerdown', e => { down = true; jump(e); try { cvs.setPointerCapture(e.pointerId); } catch {} });
  cvs.addEventListener('pointermove', e => { if (down) jump(e); });
  cvs.addEventListener('pointerup', () => { down = false; });
  const setMiniVisible = v => {
    $('miniWrap').hidden = !v;
    $('miniShow').hidden = v;
    try { localStorage.setItem('mapeditor.mini', v ? '1' : '0'); } catch {}
    if (v) invalidate();
  };
  $('miniHide').onclick = () => setMiniVisible(false);
  $('miniShow').onclick = () => setMiniVisible(true);
  let vis = true;
  try { vis = localStorage.getItem('mapeditor.mini') !== '0'; } catch {}
  setMiniVisible(vis);
}

// --------------------------------------------------------------------------- checks
// Data-driven lint over the loaded map: content rows pointing at blocked cells, and
// walkable steps into void. (Art-level checks like "walkable water" would need a tile
// classification we don't have — the pass overlay is the tool for eyeballing those.)
function runChecks() {
  const list = $('lintList');
  list.innerHTML = '';
  if (!S.cells) return;
  const rows = [];
  const inB = (x, y) => x >= 0 && y >= 0 && x < S.xs && y < S.ys;
  const blockedAt = (x, y) => blockedWord(gAt(idx(x, y)));
  const mk = S.markers;
  if (mk) {
    // NOT a finding: a blocked warp SOURCE. Warp precedence beats collision in
    // Session.Movement.HandleWalk, so a doorway warp on a blocked cell is the standard
    // working idiom here — flagging it would bury the real findings under every door.
    for (const w of mk.warpsIn) if (inB(w.x, w.y) && blockedAt(w.x, w.y))
      rows.push([w.x, w.y, `arrival from ${w.name || 'TK' + w.m} lands on a blocked cell`]);
    for (const a of mk.worldArrivals) if (inB(a.x, a.y) && blockedAt(a.x, a.y)) rows.push([a.x, a.y, 'world-map arrival lands on a blocked cell']);
    for (const s of mk.spawns) if (inB(s.x, s.y) && blockedAt(s.x, s.y)) rows.push([s.x, s.y, `spawn ${s.name || 'mob ' + s.mob} is inside a wall`]);
  }
  // A void cell (word 0 = walkable) whose 4-neighborhood contains walkable real ground is
  // a step into blackness; interior void seas behind blocked fences are fine and skipped.
  let voidEdges = 0;
  const VOID_CAP = 60;
  for (let y = 0; y < S.ys; y++) for (let x = 0; x < S.xs; x++) {
    if (gAt(idx(x, y)) !== 0) continue;
    let hot = false;
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const nx = x + dx, ny = y + dy;
      if (!inB(nx, ny)) continue;
      const ng = gAt(idx(nx, ny));
      if (ng !== 0 && !blockedWord(ng)) { hot = true; break; }
    }
    if (hot && ++voidEdges <= VOID_CAP) rows.push([x, y, 'walkable edge into void']);
  }
  $('lintNote').textContent = rows.length || voidEdges > VOID_CAP ? `${rows.length + Math.max(0, voidEdges - VOID_CAP)} findings` : 'clean';
  for (const [x, y, why] of rows) {
    const d = document.createElement('div');
    d.className = 'lintrow';
    const wh = document.createElement('span'); wh.className = 'where'; wh.textContent = `${x},${y}`;
    const tx = document.createElement('span'); tx.textContent = why;
    d.append(wh, tx);
    d.onclick = () => {
      const v = viewSize();
      S.cam.x = x * CELL - v.w / 2; S.cam.y = y * CELL - v.h / 2;
      S.selection = { x0: x, y0: y, x1: x, y1: y };
      clampCam(); invalidate();
    };
    list.appendChild(d);
  }
  if (voidEdges > VOID_CAP) {
    const d = document.createElement('div');
    d.className = 'lintrow more';
    d.textContent = `…and ${voidEdges - VOID_CAP} more void edges`;
    list.appendChild(d);
  }
}

// Marker glyphs: warps are diamonds (filled = a warp OUT lives here, hollow = somewhere
// warps IN here — violet for the world-map screen), spawn points orange dots, NPCs green
// squares, area-spawn boxes dashed orange rects. Below device scale 1 (whole-map overview)
// a glyph would be a couple of pixels, so each marked cell gets a solid tint instead.
function drawMarkers(ctx, camX, camY, s) {
  const mk = S.markers;
  const glyph = (x, y, color, shape, fill) => {
    const px = x * CELL - camX, py = y * CELL - camY;
    if (px < -CELL || py < -CELL) return;
    if (s < 1) {
      ctx.globalAlpha = 0.7; ctx.fillStyle = color;
      ctx.fillRect(px, py, CELL, CELL);
      ctx.globalAlpha = 1; return;
    }
    const cx = px + CELL / 2, cy = py + CELL / 2, r = 7;
    ctx.beginPath();
    if (shape === 'diamond') {
      ctx.moveTo(cx, cy - r); ctx.lineTo(cx + r, cy); ctx.lineTo(cx, cy + r); ctx.lineTo(cx - r, cy); ctx.closePath();
    } else if (shape === 'circle') ctx.arc(cx, cy, r - 1.5, 0, Math.PI * 2);
    else ctx.rect(cx - r + 2, cy - r + 2, 2 * r - 4, 2 * r - 4);
    if (fill) { ctx.globalAlpha = 0.9; ctx.fillStyle = color; ctx.fill(); ctx.globalAlpha = 1; }
    ctx.strokeStyle = fill ? '#17181a' : color; ctx.lineWidth = fill ? 1 : 2;
    ctx.stroke();
  };
  if (mk) {
    if (S.layers.override) for (const c of mk.overrides) glyph(c.x, c.y, '#ec4899', 'rect', false);
    if (S.layers.npc) for (const p of mk.npcs) glyph(p.x, p.y, '#4ade80', 'rect', true);
    if (S.layers.spawn) {
      for (const sp of mk.spawns) glyph(sp.x, sp.y, '#f97316', 'circle', true);
      ctx.strokeStyle = '#f97316'; ctx.setLineDash([6, 4]); ctx.lineWidth = 2;
      ctx.fillStyle = '#f97316';
      ctx.font = `${Math.max(10, Math.round(11 * (devicePixelRatio || 1) / s))}px "IBM Plex Mono", monospace`;
      for (const a of mk.areas) {
        if (!a.x0 && !a.y0 && !a.x1 && !a.y1) continue;   // map-wide — see the layer row's tooltip
        const bx = a.x0 * CELL - camX, by = a.y0 * CELL - camY;
        ctx.strokeRect(bx + 1, by + 1, (a.x1 - a.x0 + 1) * CELL - 2, (a.y1 - a.y0 + 1) * CELL - 2);
        ctx.fillText(`${a.name || 'mob ' + a.mob} ×${a.count}`, bx + 3, by - 4);
      }
      ctx.setLineDash([]);
    }
    if (S.layers.warp) {
      for (const c of mk.world) glyph(c.x, c.y, '#a78bfa', 'diamond', true);
      for (const a of mk.worldArrivals) glyph(a.x, a.y, '#a78bfa', 'diamond', false);
      for (const w of mk.warpsIn) glyph(w.x, w.y, '#38bdf8', 'diamond', false);
      for (const w of mk.warpsOut) glyph(w.x, w.y, '#38bdf8', 'diamond', true);
    }
  }
  // Pending placements — always on top; they're the working set, not a toggleable layer.
  // Each keeps its marker family's hue, brightened: yellow spawns, light-cyan warps,
  // bright-green NPCs — so the types read apart at a glance.
  for (const p of S.placed) glyph(p.x, p.y, '#eab308', 'circle', true);
  for (const w of S.placedWarps) {
    if (w.sm === S.mapId) glyph(w.sx, w.sy, '#67e8f9', 'diamond', true);
    if (w.dm === S.mapId) glyph(w.dx, w.dy, '#67e8f9', 'diamond', false);
  }
  if (S.warpArm && S.warpArm.m === S.mapId) glyph(S.warpArm.x, S.warpArm.y, '#67e8f9', 'diamond', true);
  for (const p of S.placedNpcs) if (p.map === S.mapId) glyph(p.x, p.y, '#00e676', 'rect', true);
}

let _walkCvs = null;
function walkerSprite() {
  if (!_walkCvs) { _walkCvs = document.createElement('canvas'); _walkCvs.width = 30; _walkCvs.height = 26; }
  const g = _walkCvs.getContext('2d');
  g.clearRect(0, 0, 30, 26);
  const accent = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();
  const x = 15, y = 24;
  g.fillStyle = 'rgba(0,0,0,0.35)';
  g.beginPath(); g.ellipse(x, y, 7, 2.5, 0, 0, Math.PI * 2); g.fill();
  g.fillStyle = accent; g.strokeStyle = '#17181a'; g.lineWidth = 1;
  g.beginPath(); g.moveTo(x - 6, y);
  g.quadraticCurveTo(x - 6, y - 11, x, y - 11);
  g.quadraticCurveTo(x + 6, y - 11, x + 6, y);
  g.closePath(); g.fill(); g.stroke();
  g.fillStyle = '#e8d5b5';
  g.beginPath(); g.arc(x, y - 14, 4.4, 0, Math.PI * 2); g.fill(); g.stroke();
  return _walkCvs;
}
// The 5.33 client shows an occluded character as a smooth ~50% translucent blend through
// the object (verified against a native client screenshot on the Buya north gate) — not the
// 4.x-era checkerboard dither. The full sprite is drawn interleaved by row (so objects
// south of it overdraw it), then this alpha pass on top restores it as a ghost wherever
// something covered it; over open ground the two passes land on the same pixels.
function drawWalker(ctx, camX, camY, ghost) {
  const dx = S.walker.x * CELL - camX + CELL / 2 - 15;
  const dy = (S.walker.y + 1) * CELL - camY - 26;
  if (ghost) {
    ctx.save(); ctx.globalAlpha = 0.5;
    ctx.drawImage(walkerSprite(), dx, dy);
    ctx.restore();
  } else ctx.drawImage(walkerSprite(), dx, dy);
}

const normRect = (a, b) => ({ x0: Math.min(a.x, b.x), y0: Math.min(a.y, b.y), x1: Math.max(a.x, b.x), y1: Math.max(a.y, b.y) });

// --------------------------------------------------------------------------- input
function cellFromEvent(e) {
  const r = $('view').getBoundingClientRect(), dpr = devicePixelRatio || 1, s = viewScale();
  return {
    x: Math.floor(((e.clientX - r.left) * dpr / s + S.cam.x) / CELL),
    y: Math.floor(((e.clientY - r.top) * dpr / s + S.cam.y) / CELL),
  };
}
const inMap = c => c.x >= 0 && c.y >= 0 && c.x < S.xs && c.y < S.ys;

function bindCanvas() {
  const cvs = $('view');
  cvs.addEventListener('mousedown', e => {
    if (!S.cells) return;
    if (e.button === 1 || spaceHeld) { S.drag = { kind: 'pan', sx: e.clientX, sy: e.clientY, cx: S.cam.x, cy: S.cam.y }; e.preventDefault(); return; }
    if (e.button !== 0) return;
    const c = cellFromEvent(e);
    if (!inMap(c)) return;
    switch (S.tool) {
      case 'brush': case 'erase': case 'pass':
        beginStroke(); applyAt(c.x, c.y); S.drag = { kind: 'paint', last: c }; break;
      case 'fill': beginStroke(); flood(c.x, c.y); endStroke(); break;
      case 'rect': S.drag = { kind: 'rect', start: c, cur: c }; break;
      case 'marquee': S.selection = null; S.drag = { kind: 'marquee', start: c, cur: c }; break;
      case 'stamp': pasteAt(c.x, c.y); break;
      case 'picker': pick(c.x, c.y); break;
      case 'select': updateStatus(c); break;
      case 'walk':
        if (S.walker.x === c.x && S.walker.y === c.y) { S.walker = { x: -1, y: -1 }; S.bump = ''; }
        else if (blockedWord(gAt(idx(c.x, c.y)))) flashHint('that cell is blocked — pick a passable one');
        else { S.walker = { x: c.x, y: c.y }; S.bump = ''; }
        break;
      case 'spawn': placeSpawnAt(c.x, c.y); break;
      case 'warp': placeWarpAt(c.x, c.y); break;
      case 'npc': placeNpcAt(c.x, c.y); break;
    }
    invalidate();
  });
  window.addEventListener('mousemove', e => {
    if (!S.cells) return;
    const c = cellFromEvent(e);
    if (c.x !== S.hover.x || c.y !== S.hover.y) { S.hover = c; invalidate(); updateStatus(); }
    if (!S.drag) return;
    if (S.drag.kind === 'pan') {
      const k = (devicePixelRatio || 1) / viewScale();
      S.cam.x = S.drag.cx - (e.clientX - S.drag.sx) * k;
      S.cam.y = S.drag.cy - (e.clientY - S.drag.sy) * k;
      clampCam(); invalidate();
    } else if (S.drag.kind === 'paint') {
      if (inMap(c) && (c.x !== S.drag.last.x || c.y !== S.drag.last.y)) {
        lineCells(S.drag.last, c, p => applyAt(p.x, p.y));
        S.drag.last = c;
      }
    } else if (S.drag.kind === 'rect' || S.drag.kind === 'marquee') {
      S.drag.cur = { x: Math.max(0, Math.min(S.xs - 1, c.x)), y: Math.max(0, Math.min(S.ys - 1, c.y)) };
      invalidate();
    }
  });
  window.addEventListener('mouseup', () => {
    if (!S.drag) return;
    const d = S.drag; S.drag = null;
    if (d.kind === 'paint') endStroke();
    else if (d.kind === 'rect') {
      const r = normRect(d.start, d.cur);
      beginStroke();
      if (S.palBlock && S.tab !== 'pass') {
        const b = S.palBlock;
        for (let y = r.y0; y <= r.y1; y++) for (let x = r.x0; x <= r.x1; x++) {
          const ent = b.cells[((y - r.y0) % b.h) * b.w + ((x - r.x0) % b.w)];
          if (!ent) continue;
          const i = idx(x, y);
          if (ent.obj !== undefined) setCell(i, gAt(i), ent.obj);
          else setCell(i, ent.word, oAt(i));
        }
      } else {
        for (let y = r.y0; y <= r.y1; y++) for (let x = r.x0; x <= r.x1; x++) {
          const t = S.tool; S.tool = 'brush'; applyAt(x, y); S.tool = t;
        }
      }
      endStroke();
    } else if (d.kind === 'marquee') {
      S.selection = normRect(d.start, d.cur);
      copySelection();
    }
    invalidate();
  });
  cvs.addEventListener('wheel', e => {
    e.preventDefault();
    if (e.ctrlKey) { stepScale(e.deltaY < 0 ? 1 : -1); return; }
    if (e.shiftKey) S.cam.x += e.deltaY; else { S.cam.x += e.deltaX; S.cam.y += e.deltaY; }
    clampCam(); invalidate();
  }, { passive: false });
  cvs.addEventListener('contextmenu', e => e.preventDefault());
  cvs.addEventListener('mouseleave', () => { S.hover = { x: -1, y: -1 }; invalidate(); });
}

let spaceHeld = false;
function bindKeys() {
  window.addEventListener('keydown', e => {
    if (e.target.tagName === 'INPUT') return;
    if (e.code === 'Space') { spaceHeld = true; e.preventDefault(); return; }
    if ((e.ctrlKey || e.metaKey) && e.key === 'z') { e.preventDefault(); undo(); return; }
    if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); saveMap(); return; }
    if (e.key === 'Escape') {
      S.selection = null;
      if (S.tool === 'walk' && S.walker.x >= 0) { S.walker = { x: -1, y: -1 }; S.bump = ''; updateStatus(); }
      if (S.warpArm) { S.warpArm = null; updateWarpBox(); updateStatus(); }
      invalidate(); return;
    }
    if (e.key === '+' || e.key === '=') { stepScale(1); return; }
    if (e.key === '-' || e.key === '_') { stepScale(-1); return; }
    const dirs = { ArrowUp: [0, -1], ArrowDown: [0, 1], ArrowLeft: [-1, 0], ArrowRight: [1, 0] };
    const d = dirs[e.key];
    if (!d) return;
    e.preventDefault();
    if (S.tool === 'walk' && S.walker.x >= 0) moveWalker(d[0], d[1]);
    else { S.cam.x += d[0] * CELL * 4; S.cam.y += d[1] * CELL * 4; clampCam(); invalidate(); }
  });
  window.addEventListener('keyup', e => { if (e.code === 'Space') spaceHeld = false; });
}

// --------------------------------------------------------------------------- palette
let usedCache = { tag: '', ground: [], obj: [] };
function usedLists() {
  const tag = S.mapId + ':' + S.undoStack.length + ':' + (S.modified ? 1 : 0);
  if (usedCache.tag !== tag && S.cells) {
    const gset = new Set(), oset = new Set();
    for (let i = 0; i < S.xs * S.ys; i++) {
      const g = S.cells[i * 2], o = S.cells[i * 2 + 1];
      if (g) gset.add(g);
      if (o) oset.add(o);
    }
    usedCache = { tag, ground: [...gset].sort((a, b) => a - b), obj: [...oset].sort((a, b) => a - b) };
  }
  return usedCache;
}
// Shift+click range in the palette grid -> a rectangular block of tiles the brush
// stamps as one unit (and the rectangle tool tiles as a repeating pattern).
function buildPalBlock(a, b) {
  const base = S.palPage * PAL_PAGE;
  if (a < base || a >= base + PAL_PAGE) { S.palAnchorIdx = b; return; }
  const ents = palEntries();
  const ac = (a - base) % PAL_COLS, ar = Math.floor((a - base) / PAL_COLS);
  const bc = (b - base) % PAL_COLS, br = Math.floor((b - base) / PAL_COLS);
  const c0 = Math.min(ac, bc), c1 = Math.max(ac, bc), r0 = Math.min(ar, br), r1 = Math.max(ar, br);
  const w = c1 - c0 + 1, h = r1 - r0 + 1;
  const cells = [], gis = new Set();
  for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) {
    const gi = base + r * PAL_COLS + c;
    if (gi < ents.count) { cells.push(ents.get(gi)); gis.add(gi); }
    else cells.push(null);
  }
  S.palBlock = { w, h, tab: S.tab, cells, gis };
  const first = cells.find(e => e);
  if (first) { if (first.obj !== undefined) S.selObj = first.obj; else S.selWord = first.word; }
}
function paintBlockAt(x, y) {
  const b = S.palBlock;
  for (let dy = 0; dy < b.h; dy++) for (let dx = 0; dx < b.w; dx++) {
    const ent = b.cells[dy * b.w + dx];
    if (!ent) continue;
    const tx = x + dx, ty = y + dy;
    if (tx >= S.xs || ty >= S.ys) continue;
    const i = idx(tx, ty);
    if (ent.obj !== undefined) setCell(i, gAt(i), ent.obj);
    else setCell(i, ent.word, oAt(i));
  }
}
function saveFavs() { try { localStorage.setItem('mapeditor.favs', JSON.stringify(S.favs)); } catch {} }
function toggleFav(ent) {
  const list = ent.obj !== undefined ? S.favs.obj : S.favs.ground;
  const v = ent.obj !== undefined ? ent.obj : ent.word;
  const i = list.indexOf(v);
  if (i >= 0) list.splice(i, 1); else list.push(v);
  saveFavs(); drawPalette();
  flashHint(i >= 0 ? 'removed from favorites' : 'added to favorites ★');
}
function isFav(ent) {
  return ent.obj !== undefined ? S.favs.obj.includes(ent.obj) : S.favs.ground.includes(ent.word);
}
function palEntries() {
  if (S.tab === 'pass') return { count: 0, get: () => ({}) };
  if (S.palMode === 'used') {
    const u = usedLists();
    if (S.tab === 'obj') return { count: u.obj.length, get: i => ({ obj: u.obj[i] }) };
    return { count: u.ground.length, get: i => ({ word: u.ground[i] }) };
  }
  if (S.palMode === 'fav') {
    const list = S.tab === 'obj' ? S.favs.obj : S.favs.ground;
    if (S.tab === 'obj') return { count: list.length, get: i => ({ obj: list[i] }) };
    return { count: list.length, get: i => ({ word: list[i] }) };
  }
  if (S.tab === 'obj') return { count: S.meta.objs.length - 1, get: i => ({ obj: i + 1 }) };
  if (S.tab === 'ground' && S.sheet === 2)
    return { count: S.sheet2Legacy.length, get: i => ({ word: S2BASE + S.sheet2Legacy[i] }) };
  return { count: S.meta.groundCount - 1, get: i => ({ word: i + 1 }) };
}

function drawPalette() {
  if ($('palHover')) hidePalHover();
  const cvs = $('palCanvas'), ctx = cvs.getContext('2d');
  const slot = 31, dpr = devicePixelRatio || 1;
  cvs.width = PAL_COLS * slot * dpr; cvs.height = PAL_ROWS * slot * dpr;
  cvs.style.width = PAL_COLS * slot + 'px';
  cvs.style.height = PAL_ROWS * slot + 'px';
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.imageSmoothingEnabled = false;
  ctx.fillStyle = '#101112'; ctx.fillRect(0, 0, PAL_COLS * slot, PAL_ROWS * slot);
  if (S.tab === 'pass') {
    for (const [i, [color, label]] of [['#3fa877', 'walk'], ['#be3c2d', 'block']].entries()) {
      ctx.fillStyle = color; ctx.fillRect(8 + i * 70, 8, 56, 32);
      ctx.fillStyle = '#e8eaec'; ctx.font = '11px IBM Plex Mono';
      ctx.fillText(label, 8 + i * 70 + 12, 56);
      if ((S.selPass === 3) === (i === 1)) { ctx.strokeStyle = '#e8eaec'; ctx.lineWidth = 2; ctx.strokeRect(7 + i * 70, 7, 58, 34); }
    }
    $('palPage').textContent = ''; $('palSel').textContent = S.selPass === 3 ? 'pass 3 · blocked' : 'pass 0 · walkable';
    return;
  }
  const ents = palEntries(), AC = S.meta.atlasCols;
  const pages = Math.max(1, Math.ceil(ents.count / PAL_PAGE));
  S.palPage = Math.max(0, Math.min(S.palPage, pages - 1));
  for (let k = 0; k < PAL_PAGE; k++) {
    const gi = S.palPage * PAL_PAGE + k;
    if (gi >= ents.count) break;
    const ent = ents.get(gi);
    const px = k % PAL_COLS * slot + 3, py = Math.floor(k / PAL_COLS) * slot + 3;
    let fr = 0, img = S.groundImg, selected = false;
    if (ent.obj !== undefined) {
      const fids = S.meta.objs[ent.obj];
      fr = fids && fids.length ? fids[0] : 0; img = S.tilecImg;
      selected = S.tab === 'obj' && S.selObj === ent.obj;
    } else {
      fr = groundFrame(ent.word);
      selected = S.tab === 'ground' && S.selWord === ent.word;
    }
    if (S.palBlock && S.palBlock.gis.has(gi)) selected = true;
    if (fr > 0) ctx.drawImage(img, fr % AC * CELL, Math.floor(fr / AC) * CELL, CELL, CELL, px, py, CELL, CELL);
    if (selected) {
      ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();
      ctx.lineWidth = 2; ctx.strokeRect(px - 2, py - 2, CELL + 4, CELL + 4);
    }
    if (isFav(ent)) {
      ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();
      ctx.beginPath(); ctx.arc(px + CELL - 2, py + 2, 2.5, 0, Math.PI * 2); ctx.fill();
    }
  }
  if (ents.count === 0 && S.palMode === 'fav') {
    ctx.fillStyle = '#8b9096'; ctx.font = '11px IBM Plex Sans';
    ctx.fillText('right-click any swatch', 60, 100);
    ctx.fillText('to star it', 90, 116);
  }
  $('palPage').textContent = `p ${S.palPage + 1}/${pages}`;
  $('palSel').textContent = (S.palBlock ? `block ${S.palBlock.w}×${S.palBlock.h} · ` : '')
    + (S.tab === 'obj' ? `SObj ${S.selObj}`
    : S.selWord >= S2BASE ? `g 0x${S.selWord.toString(16).toUpperCase()} · sheet2`
    : `g ${S.selWord}`);
}

function bindPalette() {
  $('palCanvas').addEventListener('mousedown', e => {
    const r = $('palCanvas').getBoundingClientRect();
    if (S.tab === 'pass') {
      S.selPass = e.clientX - r.left > 78 ? 3 : 0;
      drawPalette(); return;
    }
    const slot = 31;
    const k = Math.floor((e.clientY - r.top) / slot) * PAL_COLS + Math.floor((e.clientX - r.left) / slot);
    const ents = palEntries(), gi = S.palPage * PAL_PAGE + k;
    if (gi >= ents.count) return;
    if (e.shiftKey && S.palAnchorIdx !== null && S.tab !== 'pass') {
      buildPalBlock(S.palAnchorIdx, gi);
    } else {
      S.palAnchorIdx = gi;
      S.palBlock = null;
      const ent = ents.get(gi);
      if (ent.obj !== undefined) S.selObj = ent.obj; else S.selWord = ent.word;
    }
    if (S.tool !== 'brush' && S.tool !== 'fill' && S.tool !== 'rect') setTool('brush');
    drawPalette(); updateStatus();
  });
  $('palCanvas').addEventListener('mousemove', e => {
    if (S.tab === 'pass') { hidePalHover(); return; }
    const r = $('palCanvas').getBoundingClientRect();
    const slot = 31;
    const k = Math.floor((e.clientY - r.top) / slot) * PAL_COLS + Math.floor((e.clientX - r.left) / slot);
    const ents = palEntries(), gi = S.palPage * PAL_PAGE + k;
    if (k < 0 || gi >= ents.count) { hidePalHover(); return; }
    showPalHover(ents.get(gi), e.clientX, e.clientY);
  });
  $('palCanvas').addEventListener('mouseleave', hidePalHover);
  $('palCanvas').addEventListener('contextmenu', e => {
    e.preventDefault();
    if (S.tab === 'pass') return;
    const r = $('palCanvas').getBoundingClientRect();
    const slot = 31;
    const k = Math.floor((e.clientY - r.top) / slot) * PAL_COLS + Math.floor((e.clientX - r.left) / slot);
    const ents = palEntries(), gi = S.palPage * PAL_PAGE + k;
    if (gi < ents.count) toggleFav(ents.get(gi));
  });
  $('palPrev').onclick = () => { S.palPage--; drawPalette(); };
  $('palNext').onclick = () => { S.palPage++; drawPalette(); };
  $('sheet1').onclick = () => { S.sheet = 1; S.palPage = 0; updateSegs(); drawPalette(); };
  $('sheet2').onclick = () => { S.sheet = 2; S.palPage = 0; updateSegs(); drawPalette(); };
  for (const [id, mode] of [['pmAll', 'all'], ['pmUsed', 'used'], ['pmFav', 'fav']])
    $(id).onclick = () => { S.palMode = mode; S.palPage = 0; updateSegs(); drawPalette(); };
  $('palSearch').addEventListener('keydown', e => {
    if (e.key !== 'Enter') return;
    const v = e.target.value.trim();
    if (!v) return;
    const n = v.toLowerCase().startsWith('0x') ? parseInt(v, 16) : parseInt(v, 10);
    if (!Number.isFinite(n)) { flashHint('not a number: ' + v); return; }
    jumpToEntry(n);
  });
}

function hidePalHover() { $('palHover').hidden = true; }
// Magnified preview of the hovered swatch: ground tile at 5x, objects as the whole
// composited column scaled to fit.
function showPalHover(ent, cx, cy) {
  const hc = $('palHover'), AC = S.meta.atlasCols;
  const label = ent.obj !== undefined ? 'SObj ' + ent.obj
    : ent.word >= S2BASE ? 'g 0x' + ent.word.toString(16).toUpperCase() : 'g ' + ent.word;
  let w, h, paint;
  if (ent.obj !== undefined) {
    const fids = S.meta.objs[ent.obj] || [];
    const n = Math.max(1, fids.length);
    const z = Math.min(5, Math.max(1, Math.floor(240 / (n * CELL))));
    w = CELL * z; h = n * CELL * z;
    paint = g => {
      for (let k = 0; k < fids.length; k++) {
        const fid = fids[k];
        if (!fid) continue;
        g.drawImage(S.tilecImg, fid % AC * CELL, Math.floor(fid / AC) * CELL, CELL, CELL,
          0, (n - 1 - k) * CELL * z, CELL * z, CELL * z);
      }
    };
  } else {
    const fr = groundFrame(ent.word), z = 5;
    w = CELL * z; h = CELL * z;
    paint = g => {
      if (fr > 0) g.drawImage(S.groundImg, fr % AC * CELL, Math.floor(fr / AC) * CELL, CELL, CELL, 0, 0, CELL * z, CELL * z);
    };
  }
  const pad = 6, strip = 16;
  hc.width = w + pad * 2; hc.height = h + pad * 2 + strip;
  const g = hc.getContext('2d');
  g.imageSmoothingEnabled = false;
  g.fillStyle = '#101112'; g.fillRect(0, 0, hc.width, hc.height);
  g.save(); g.translate(pad, pad); paint(g); g.restore();
  g.fillStyle = '#9ba0a6'; g.font = '11px IBM Plex Mono, monospace';
  g.fillText(label, pad, hc.height - 5);
  hc.style.width = hc.width + 'px'; hc.style.height = hc.height + 'px';
  let left = cx - hc.width - 18;
  if (left < 4) left = cx + 18;
  hc.style.left = left + 'px';
  hc.style.top = Math.max(4, Math.min(innerHeight - hc.height - 4, cy - hc.height / 2)) + 'px';
  hc.hidden = false;
}

// Jump the palette to a tile/object id: selects it and pages to it in All mode.
function jumpToEntry(n) {
  S.palMode = 'all';
  if (S.tab === 'obj') {
    if (n < 1 || n >= S.meta.objs.length) { flashHint('SObj id out of range'); return; }
    S.selObj = n;
    S.palPage = Math.floor((n - 1) / PAL_PAGE);
  } else {
    S.tab = 'ground';
    if (n >= S2BASE) {
      const li = S.sheet2Legacy.indexOf(n - S2BASE);
      if (li < 0) { flashHint('not a mapped sheet-2 word'); return; }
      S.sheet = 2; S.selWord = n; S.palPage = Math.floor(li / PAL_PAGE);
    } else {
      if (n < 1 || n >= S.meta.groundCount) { flashHint('ground id out of range'); return; }
      S.sheet = 1; S.selWord = n; S.palPage = Math.floor((n - 1) / PAL_PAGE);
    }
  }
  updateSegs(); drawPalette(); updateStatus();
}

// --------------------------------------------------------------------------- ui chrome
function buildRail() {
  const rail = $('rail');
  rail.innerHTML = '';
  for (const t of TOOLS) {
    if (!t) { const s = document.createElement('div'); s.className = 'tsep'; rail.appendChild(s); continue; }
    const [id, tip, svg] = t;
    const b = document.createElement('button');
    b.className = 'tool'; b.id = 'tool-' + id; b.title = `${id} — ${tip}`;
    b.innerHTML = `<svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${svg}</svg>`;
    b.onclick = () => setTool(id);
    rail.appendChild(b);
  }
}

function setTool(id) {
  S.tool = id;
  if (id !== 'marquee') S.drag = null;
  document.querySelectorAll('.tool').forEach(b => b.classList.toggle('on', b.id === 'tool-' + id));
  $('tool-stamp').classList.toggle('dimmed', !S.clipboard);
  $('dpad').hidden = id !== 'walk';
  $('spawnBox').hidden = id !== 'spawn';
  $('warpBox').hidden = id !== 'warp';
  $('npcBox').hidden = id !== 'npc';
  if (id === 'warp') updateWarpBox();
  if (id === 'npc') updateNpcBox();
  updateStatus(); invalidate();
}

function setTab(tab) {
  S.tab = tab; S.palPage = 0;
  S.palBlock = null; S.palAnchorIdx = null;
  updateSegs(); drawPalette(); updateStatus();
}

function setMode(m) {
  S.mode = m;
  const accent = m === '4x' ? '#d9962e' : '#3fa877';
  document.documentElement.style.setProperty('--accent', accent);
  $('btnExport').textContent = m === '4x' ? 'Export .map' : 'Export .cmp';
  $('palSource').textContent = m === '4x' ? 'tile.dat · via 5.33 art' : 'TILE.EPF · 5.33';
  updateSegs(); updateStatus(); drawPalette(); invalidate();
}

function updateSegs() {
  $('mode4x').classList.toggle('on', S.mode === '4x');
  $('mode533').classList.toggle('on', S.mode === '533');
  $('tabGround').classList.toggle('on', S.tab === 'ground');
  $('tabObj').classList.toggle('on', S.tab === 'obj');
  $('tabPass').classList.toggle('on', S.tab === 'pass');
  $('sheet1').classList.toggle('on', S.sheet === 1);
  $('sheet2').classList.toggle('on', S.sheet === 2);
  $('pmAll').classList.toggle('on', S.palMode === 'all');
  $('pmUsed').classList.toggle('on', S.palMode === 'used');
  $('pmFav').classList.toggle('on', S.palMode === 'fav');
  $('palSheets').style.display = (S.tab === 'ground' && S.palMode === 'all') ? '' : 'none';
  $('palModes').style.display = S.tab === 'pass' ? 'none' : '';
}

function buildMapList() {
  const list = $('mapList');
  const filter = ($('mapFilter').value || '').toLowerCase();
  list.innerHTML = '';
  for (const m of S.meta.maps) {
    if (!m.file) continue;
    if (filter && !(`${m.id} ${m.name}`.toLowerCase().includes(filter))) continue;
    const row = document.createElement('div');
    row.className = 'mrow' + (m.id === S.mapId ? ' on' : '');
    row.innerHTML = `<span class="name">${m.name || '(unnamed)'}</span>${m.draft ? `<span class="draftdot" title="${m.custom ? 'new map — exists only in the tool\'s saved folder' : 'has a draft save'}">●</span>` : ''}<span class="mono dim">${m.custom ? 'new · ' : ''}TK${m.id} · ${m.xs}×${m.ys}</span>`;
    row.onclick = () => loadMap(m.id);
    list.appendChild(row);
  }
}

function updateButtons() {
  $('btnSave').disabled = !S.modified;
  $('btnDraft').hidden = !S.isDraft;
  $('btnUndo').disabled = S.undoStack.length === 0;
  $('btnUndo').textContent = S.undoStack.length ? `Undo · ${S.undoStack.length}` : 'Undo';
  $('tool-stamp')?.classList.toggle('dimmed', !S.clipboard);
}

let hintTimer = null, flashText = '';
function flashHint(msg) {
  flashText = msg;
  clearTimeout(hintTimer);
  hintTimer = setTimeout(() => { flashText = ''; updateStatus(); }, 3000);
  updateStatus();
}

function updateStatus(pin) {
  $('stMap').textContent = S.mapId !== null
    ? `TK${S.mapId} · ${S.mapName} · ${S.xs}×${S.ys}${S.isDraft ? ' · draft' : ''}` : 'no map';
  const c = pin || S.hover;
  if (S.cells && c.x >= 0 && c.x < S.xs && c.y >= 0 && c.y < S.ys) {
    const i = idx(c.x, c.y), g = gAt(i), o = oAt(i);
    let marks = S.markers ? (S.markers.byCell.get(i) || []).join(' · ') : '';
    if (S.markers)
      for (const a of S.markers.areas)
        if ((a.x0 || a.y0 || a.x1 || a.y1) && c.x >= a.x0 && c.x <= a.x1 && c.y >= a.y0 && c.y <= a.y1)
          marks += (marks ? ' · ' : '') + `area: ${a.name || 'mob ' + a.mob} ×${a.count}`;
    const pl = S.placed.find(p => p.x === c.x && p.y === c.y);
    if (pl) marks += (marks ? ' · ' : '') + `pending spawn: ${pl.name || 'mob ' + pl.mob}`;
    for (const w of S.placedWarps) {
      if (w.sm === S.mapId && w.sx === c.x && w.sy === c.y)
        marks += (marks ? ' · ' : '') + `pending warp → ${w.dname || 'TK' + w.dm} (${w.dx},${w.dy})`;
      if (w.dm === S.mapId && w.dx === c.x && w.dy === c.y)
        marks += (marks ? ' · ' : '') + `pending arrival ← ${w.sname || 'TK' + w.sm}`;
    }
    const pn = S.placedNpcs.find(p => p.map === S.mapId && p.x === c.x && p.y === c.y);
    if (pn) marks += (marks ? ' · ' : '') + `pending npc: ${pn.description || pn.identifier || 'template ' + pn.template}`;
    $('stCell').textContent =
      `cell (${c.x}, ${c.y}) · g 0x${g.toString(16).toUpperCase().padStart(4, '0')} · pass ${g >> 14 & 3} · obj ${o}`
      + (marks ? '  ·  ' + marks : '');
  } else $('stCell').textContent = '';
  const hints = {
    marquee: 'drag to select — copies on release',
    stamp: S.clipboard ? `stamp ${S.clipboard.w}×${S.clipboard.h} — click to paste` : 'copy a region with the marquee first',
    walk: S.walker.x < 0 ? 'click a passable cell to drop the test character'
      : `walk (${S.walker.x},${S.walker.y})` + (oAt(idx(S.walker.x, S.walker.y)) ? ' · on object' : '') + (S.bump ? ` · blocked ${S.bump}` : '') + ' · click him or Esc to remove',
    pass: 'click / drag to toggle the 2-bit pass flag (flips the sheet tag)',
    erase: S.tab === 'obj' ? 'erase: removes objects' : S.tab === 'ground' ? 'erase: ground → void' : 'erase: pass → walkable',
    spawn: S.selMob
      ? `place ${S.selMob.name} — click to place, click a yellow point to remove · ${S.placed.length} pending`
      : 'pick a mob in the spawn box, then click cells to place spawn points',
    warp: S.warpArm
      ? `destination for ${S.warpArm.name || 'TK' + S.warpArm.m} (${S.warpArm.x},${S.warpArm.y}) — click a cell on any map, Esc cancels`
      : `click a source cell to start a warp pair · ${S.placedWarps.length} pending`,
    npc: S.selNpc
      ? `place a copy of ${S.selNpc.name || S.selNpc.ident} — click to place, click a green square to remove · ${S.placedNpcs.length} pending`
      : 'pick a template NPC in the box, then click cells to place copies',
  };
  $('stHint').textContent = flashText || hints[S.tool] || '';
  $('stEdits').textContent = S.modified ? `${S.undoStack.length - S.savedMark} unsaved stroke${S.undoStack.length - S.savedMark === 1 ? '' : 's'}` : 'saved';
  $('stEdits').style.color = S.modified ? 'var(--accent)' : 'var(--dim)';
  $('stFmt').textContent = S.mode === '4x' ? 'TK<id>.map · headerless LE · 4 B/cell' : 'TK######.cmp · CMAP + zlib · 6 B/cell';
}

// "Export corrections": the live buffer (saved or not) diffed server-side against the
// shipped baseline (.orig once a save exists, else the file on disk) into sparse
// MapCells.csv override rows — a small fix stays reviewable in git instead of becoming
// a rewritten binary .map.
async function exportCorrections() {
  if (S.mapId === null) return;
  const r = await fetch(`/api/map/${S.mapId}/mapcells.csv`, { method: 'POST', body: S.cells.buffer });
  if (!r.ok) { flashHint('corrections: ' + await r.text()); return; }
  const n = +r.headers.get('X-Cell-Count');
  if (!n) { flashHint('no differences vs the shipped map'); return; }
  flashHint(`${n} changed cell${n === 1 ? '' : 's'} → ${r.headers.get('X-Saved')}`);
}

function lineCells(a, b, fn) {
  const dx = Math.abs(b.x - a.x), dy = Math.abs(b.y - a.y);
  const n = Math.max(dx, dy);
  for (let k = 1; k <= n; k++)
    fn({ x: Math.round(a.x + (b.x - a.x) * k / n), y: Math.round(a.y + (b.y - a.y) * k / n) });
}

function bindUI() {
  bindCanvas(); bindKeys(); bindPalette(); bindMini();
  $('btnLint').onclick = runChecks;
  $('mode4x').onclick = () => setMode('4x');
  $('mode533').onclick = () => setMode('533');
  $('tabGround').onclick = () => setTab('ground');
  $('tabObj').onclick = () => setTab('obj');
  $('tabPass').onclick = () => setTab('pass');
  $('btnUndo').onclick = undo;
  $('btnSave').onclick = saveMap;
  $('btnDraft').onclick = discardDraft;
  $('btnExport').onclick = () => {
    if (S.mapId === null) return;
    if (S.modified) { flashHint('save first — export reads the file on disk'); return; }
    location.href = `/api/map/${S.mapId}/export.${S.mode === '4x' ? 'map' : 'cmp'}`;
  };
  $('btnCsv').onclick = exportCorrections;
  $('sbSearch').addEventListener('input', buildMobList);
  $('sbExport').onclick = exportSpawns;
  $('sbClear').onclick = () => {
    if (!S.placed.length || !confirm(`Remove all ${S.placed.length} pending spawn point${S.placed.length === 1 ? '' : 's'} on this map?`)) return;
    S.placed = [];
    savePlaced(); updateSpawnBox(); invalidate(); updateStatus();
  };
  buildMobList();
  $('nbSearch').addEventListener('input', buildNpcList);
  $('nbExport').onclick = exportNpcs;
  $('nbClear').onclick = () => {
    if (!S.placedNpcs.length || !confirm(`Remove all ${S.placedNpcs.length} pending NPC${S.placedNpcs.length === 1 ? '' : 's'}?`)) return;
    S.placedNpcs = [];
    saveNpcs(); updateNpcBox(); invalidate(); updateStatus();
  };
  buildNpcList();
  $('wbExport').onclick = exportWarps;
  $('wbClear').onclick = () => {
    if (!S.placedWarps.length || !confirm(`Remove all ${S.placedWarps.length} pending warp pair${S.placedWarps.length === 1 ? '' : 's'}?`)) return;
    S.placedWarps = []; S.warpArm = null;
    saveWarps(); updateWarpBox(); invalidate(); updateStatus();
  };
  $('btnImport').onclick = () => $('fileImport').click();
  $('fileImport').onchange = e => { if (e.target.files[0]) importFile(e.target.files[0]); e.target.value = ''; };
  $('mapFilter').oninput = buildMapList;
  $('btnNewMap').onclick = newMap;
  $('stZoom').style.cursor = 'pointer';
  $('stZoom').title = 'click to cycle zoom';
  $('stZoom').onclick = () => stepScale(viewScale() >= maxScale() ? -99 : 1);
  $('zoomIn').onclick = () => stepScale(1);
  $('zoomOut').onclick = () => stepScale(-1);
  const zl = $('zoomLbl');
  const applyTypedZoom = () => {
    const pct = parseFloat(zl.value.replace('%', '').trim());
    if (Number.isFinite(pct) && pct > 0) {
      const dpr = devicePixelRatio || 1;
      const raw = Math.max(10, Math.min(500, pct)) / 100 * dpr;
      setScale(raw >= 1 ? Math.round(raw) : raw);   // crisp integer steps above 100%, smooth below
    }
    zl.blur();
    invalidate();
  };
  zl.addEventListener('focus', () => zl.select());
  zl.addEventListener('keydown', e => { if (e.key === 'Enter') applyTypedZoom(); e.stopPropagation(); });
  zl.addEventListener('blur', () => invalidate());
  for (const key of ['Ground', 'Obj', 'Pass', 'Warp', 'Spawn', 'Npc', 'Override', 'Grid'])
    $('ly' + key).onchange = e => { S.layers[key.toLowerCase()] = e.target.checked; invalidate(); };
  document.querySelectorAll('#dpad button[data-d]').forEach(b => {
    const [dx, dy] = b.dataset.d.split(',').map(Number);
    b.onclick = () => moveWalker(dx, dy);
  });
  $('dpadRemove').onclick = () => { S.walker = { x: -1, y: -1 }; S.bump = ''; invalidate(); updateStatus(); };
  const dragH = $('panelDrag');
  let panelDrag = null;
  dragH.addEventListener('pointerdown', e => {
    panelDrag = { x: e.clientX, w: $('panel').getBoundingClientRect().width };
    dragH.setPointerCapture(e.pointerId); e.preventDefault();
  });
  dragH.addEventListener('pointermove', e => { if (panelDrag) setPanelWidth(panelDrag.w + (panelDrag.x - e.clientX)); });
  dragH.addEventListener('pointerup', () => { panelDrag = null; });
  let savedW = 284;
  try { savedW = parseInt(localStorage.getItem('mapeditor.panelw')) || 284; } catch {}
  setPanelWidth(savedW);
  new ResizeObserver(() => { clampCam(); invalidate(); }).observe($('canvasWrap'));
}

boot().catch(e => { $('loading').hidden = false; $('loading').textContent = 'failed to load: ' + (e && e.stack || e); console.error(e); });
window.addEventListener('error', e => { const el = $('loading'); if (el && !el.hidden) el.textContent = 'error: ' + e.message + ' @ ' + e.filename + ':' + e.lineno; });
