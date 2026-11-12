// App shell: wires the store, the canvas, the side panels, search and the live change feed.

import { api } from './api.js';
import { Store } from './store.js';
import { GraphView } from './graphview.js';
import { mapModel, flowModel, focusModel, KIND_GROUPS, KIND_WORDS } from './models.js';
import { Sidebar } from './sidebar.js';
import { Inspector } from './inspector.js';
import { esc, h, icon, typeCode, verbChip, fileName, plural, remember } from './ui.js';

const $ = (selector) => document.querySelector(selector);

const store = new Store();

const state = {
  view: 'map',
  selected: null,
  expanded: new Set(),                                   // open projects and namespaces on the map
  kinds: new Set(remember.get('kinds', ['inject', 'call', 'inherit'])),
  packages: false,
  flow: { root: null, dir: 'down', depth: 6 },
  focus: { id: null, trail: [] },
};

let model = { key: '', nodes: [], edges: [], rep: () => null };
let online = false;
let started = false;
let lastMs = null;
const feed = [];

const canvas = $('.canvas');
const tools = $('.canvas-tools');
const legend = $('.legend');
const empty = $('.empty');

// -------------------------------------------------------------------- canvas

const view = new GraphView(canvas, {
  select(viewId) {
    const node = model.nodes.find((n) => n.id === viewId);
    select(node?.ref ?? null);
    if (viewId) view.select(viewId);                    // highlight exactly what was clicked
  },
  open: (viewId) => openNode(viewId),
  edge(edgeId) {
    const edge = model.edges.find((e) => e.id === edgeId) ?? model.links?.find((e) => e.id === edgeId);
    if (!edge) return;
    const refOf = (viewId) => model.nodes.find((n) => n.id === viewId)?.ref;
    state.selected = null;
    sidebar.setCurrent(null);
    view.selectEdge(edgeId);
    inspector.show({ edge: { ...edge, sourceRef: refOf(edge.source), targetRef: refOf(edge.target) } });
  },
});

const sidebar = new Sidebar($('.index'), store, (id, tab) => go(id, tab));

const inspector = new Inspector($('.insp'), store, {
  select: (id) => {
    select(id);
    if (id) view.reveal(model.rep(id));
  },
  flow: (id) => trace(id, 'down'),
  callers: (id) => trace(id, 'up'),
  focus: (id) => { setFocus(id); renderView().then(() => select(state.selected ?? id)); },
  toggle: (id) => { state.view = 'map'; toggle(id); },
  reload: () => api.reload(),
});

function select(id) {
  state.selected = id;
  view.select(id ? model.rep(id) : null);
  sidebar.setCurrent(id);
  inspector.show(id ? { node: id } : null);
}

/** Jump to something picked from a list or from search, switching to the view that suits it. */
function go(id, from) {
  const n = store.node(id);
  if (!n) return;

  if (n.kind === 'endpoint') {
    state.view = 'flow';
    state.flow.root = id;
    state.flow.dir = 'down';
  } else if (n.kind === 'type') {
    setFocus(id);
  } else if (n.kind === 'member' && from === 'find') {
    setFocus(n.parent);
  } else if (n.kind === 'project' || n.kind === 'package') {
    state.view = 'map';
    if (n.kind === 'package') state.packages = true;
  }

  renderView().then(() => {
    select(id);
    view.reveal(model.rep(id));
  });
}

function trace(id, dir) {
  state.view = 'flow';
  state.flow.root = id;
  state.flow.dir = dir;
  renderView().then(() => select(id));
}

function setFocus(id) {
  if (state.focus.id && state.focus.id !== id && state.view === 'focus') state.focus.trail.push(state.focus.id);
  state.focus.id = id;
  state.view = 'focus';
}

function toggle(id) {
  if (state.expanded.has(id)) state.expanded.delete(id);
  else state.expanded.add(id);
  renderView({ fit: true });
}

/** Double-click: open a project, focus a type, or re-root the flow. */
function openNode(viewId) {
  const node = model.nodes.find((n) => n.id === viewId);
  if (!node) return;

  if (state.view === 'map' && (node.kind === 'project' || node.kind === 'ns')) return toggle(viewId);

  const target = store.node(node.ref);
  if (!target) return;
  if (target.kind === 'type') {
    setFocus(target.id);
    renderView().then(() => select(target.id));
  } else if (target.kind === 'member' || target.kind === 'endpoint') {
    trace(target.id, target.kind === 'endpoint' ? 'down' : state.flow.dir);
  }
}

async function renderView({ fit = false } = {}) {
  if (state.view === 'flow') model = flowModel(store, state.flow);
  else if (state.view === 'focus') model = focusModel(store, { id: state.focus.id, kinds: state.kinds });
  else model = mapModel(store, state);

  renderChrome();
  await view.render(model, { fit });
  if (state.selected) view.select(model.rep(state.selected));
}

// -------------------------------------------------------------------- toolbar, legend, empty states

function tool(label, onclick, { pressed, iconName, title } = {}) {
  const button = h('button', { class: 'tool', onclick, title, 'aria-pressed': pressed == null ? null : String(pressed) });
  if (iconName) button.insertAdjacentHTML('afterbegin', icon(iconName));
  button.append(label);
  return button;
}

function kindToggles() {
  return KIND_GROUPS.map(([group, label]) =>
    tool(label, () => {
      if (state.kinds.has(group)) state.kinds.delete(group);
      else state.kinds.add(group);
      remember.set('kinds', [...state.kinds]);
      renderView();
    }, { pressed: state.kinds.has(group), title: `Show or hide "${label.toLowerCase()}" lines` }));
}

function renderChrome() {
  for (const b of document.querySelectorAll('.seg [data-view]')) b.setAttribute('aria-pressed', String(b.dataset.view === state.view));
  $('#solution').textContent = store.info.name ?? '';

  const items = [];
  if (state.view === 'map') {
    items.push(h('span', { class: 'title' }, 'Projects and types'));
    items.push(tool('Open all', () => { for (const p of store.projects) state.expanded.add(p.id); renderView({ fit: true }); }));
    items.push(tool('Close all', () => { state.expanded.clear(); renderView({ fit: true }); }));
    items.push(...kindToggles());
    items.push(tool('Packages', () => { state.packages = !state.packages; renderView({ fit: true }); }, { pressed: state.packages }));
  } else if (state.view === 'flow') {
    const root = store.node(state.flow.root);
    items.push(h('span', { class: 'title' }, root ? store.label(root.id) : 'Flow'));
    if (root) {
      const direction = h('div', { class: 'seg' },
        h('button', { 'aria-pressed': String(state.flow.dir === 'down'), onclick: () => trace(root.id, 'down') }, 'What it calls'),
        h('button', { 'aria-pressed': String(state.flow.dir === 'up'), onclick: () => trace(root.id, 'up') }, 'Who calls it'));
      const deeper = (by) => () => {
        state.flow.depth = Math.min(12, Math.max(1, state.flow.depth + by));
        renderView({ fit: true });
      };
      items.push(direction, tool('', deeper(-1), { iconName: 'minus', title: 'Show fewer steps' }),
        h('span', null, `${state.flow.depth} steps deep`), tool('', deeper(1), { iconName: 'plus', title: 'Show more steps' }));
    }
  } else {
    const center = store.node(state.focus.id);
    if (state.focus.trail.length) {
      items.push(tool('Back', () => {
        state.focus.id = state.focus.trail.pop();
        renderView().then(() => select(state.focus.id));
      }, { iconName: 'back' }));
    }
    items.push(h('span', { class: 'title' }, center ? center.name : 'Focus'));
    if (center) items.push(...kindToggles());
  }
  tools.replaceChildren(...items);

  // Legend: only the line styles this view can show.
  const kinds = [...new Set([...model.edges, ...(model.links ?? [])].map((e) => e.kind))].filter((k) => KIND_WORDS[k]);
  legend.hidden = kinds.length === 0;
  legend.innerHTML = kinds.map((k) =>
    `<span><svg width="30" height="8"><path class="e k-${k}" d="M1,4 L29,4"/></svg>${esc(KIND_WORDS[k])}</span>`).join('');

  renderEmpty();
}

function renderEmpty() {
  let title = '', text = '';
  const status = store.status;

  if (!store.projects.length) {
    if (status.state === 'error') [title, text] = ['Could not open the solution', status.message];
    else if (status.state === 'loading') [title, text] = ['Reading the solution', status.message];
    else [title, text] = ['No C# projects found', 'Start Sightline with the path to a .sln, .slnx or .csproj file.'];
  } else if (!model.nodes.length && state.view === 'flow') {
    [title, text] = ['Pick something to trace', 'Choose an endpoint on the left to see every step it runs, from the route down to the database.'];
  } else if (!model.nodes.length && state.view === 'focus') {
    [title, text] = ['Pick a type to focus on', 'Choose a class or interface on the left to see what it depends on and what uses it.'];
  }

  empty.hidden = !title;
  empty.replaceChildren(h('b', null, title), h('span', null, text));
}

function renderStatus() {
  const el = $('#status');
  const s = store.status;
  const stateName = online ? s.state : 'offline';
  el.dataset.state = stateName;
  el.lastElementChild.textContent =
    stateName === 'offline' ? 'Disconnected, retrying'
    : stateName === 'loading' ? s.message
    : stateName === 'error' ? 'Load failed'
    : lastMs == null ? 'Live' : `Live, last update ${lastMs} ms`;
  el.title = s.message ?? '';

  const notes = store.info.diagnostics ?? [];
  const button = $('#notes');
  button.hidden = notes.length === 0;
  button.textContent = plural(notes.length, 'load warning');
}

// -------------------------------------------------------------------- live updates

/** When an edit renames something, its id changes. Follow it instead of losing the selection. */
function follow(id, change) {
  if (!id || store.node(id)) return id;
  const gone = change.removed.find((n) => n.id === id);
  if (!gone) return null;
  const candidates = change.added.filter((n) => n.kind === gone.kind && n.file === gone.file && n.parent === gone.parent);
  candidates.sort((a, b) => Math.abs((a.line ?? 0) - (gone.line ?? 0)) - Math.abs((b.line ?? 0) - (gone.line ?? 0)));
  return candidates[0]?.id ?? null;
}

async function onPatch(change) {
  lastMs = change.ms;

  const selected = follow(state.selected, change);
  state.flow.root = follow(state.flow.root, change);
  state.focus.id = follow(state.focus.id, change);
  if (selected !== state.selected) {
    state.selected = selected;
    inspector.sel = selected ? { node: selected } : null;
    sidebar.current = selected;
  }

  if (change.structural) {
    sidebar.render();
    await renderView();
    const touched = [...change.touched].map((id) => model.rep(id)).filter(Boolean);
    view.pulse(touched);
    // A flow or focus view is small enough to show whole, so a new step never appears off screen.
    // The map is left alone: zooming out there would lose the place the reader was looking at.
    if (state.view !== 'map') view.showAll(touched);
    pushFeed(change);
    if (!$('#find-results').hidden) runFind();
  }
  inspector.refresh(change);
  renderStatus();
}

function onReset() {
  // Drop state that points at things which no longer exist.
  for (const id of [...state.expanded]) if (!id.startsWith('ns:') && !store.node(id)) state.expanded.delete(id);
  if (state.selected && !store.node(state.selected)) state.selected = null;
  if (state.flow.root && !store.node(state.flow.root)) state.flow.root = null;
  if (state.focus.id && !store.node(state.focus.id)) state.focus.id = null;
  state.focus.trail = state.focus.trail.filter((id) => store.node(id));

  // Start with a readable picture: small solutions open fully, bigger ones start as a project overview.
  const first = !started && store.projects.length > 0;
  if (first) {
    started = true;
    if (store.projects.length === 1 || store.types().length <= 40) for (const p of store.projects) state.expanded.add(p.id);
  }

  sidebar.render();
  renderView({ fit: first });
  inspector.show(state.selected ? { node: state.selected } : null);
  renderStatus();
}

store.addEventListener('reset', onReset);
store.addEventListener('patch', (e) => onPatch(e.detail));

let loading = null;

/** Fetches the whole graph. Calls that arrive while one is running share it. */
function load() {
  loading ??= (async () => {
    try {
      store.reset(await api.graph());
      online = true;
    } catch {
      online = false;
    }
    loading = null;
    renderStatus();
  })();
  return loading;
}

api.events({
  hello(message) {
    online = true;
    if (message.status) store.status = message.status;
    // After a reconnect the graph may have moved on; the first connect just rides on the initial load.
    if (message.version !== store.version) load().then(() => { if (message.version > store.version) load(); });
    else renderStatus();
  },
  status(status) {
    store.status = status;
    renderStatus();
    if (!store.projects.length) renderEmpty();
  },
  patch(patch) {
    if (!store.apply(patch)) load();          // a patch was missed: take the whole graph again
  },
  reload: () => load(),
  offline() {
    online = false;
    renderStatus();
  },
});

// -------------------------------------------------------------------- change feed

const FEED_WORDS = { calls: 'calls', dispatches: 'sends to', handles: 'handled by', db: 'queries', http: 'calls out over', creates: 'creates', injects: 'injects', references: 'uses', inherits: 'inherits', implements: 'implements', registers: 'resolves to' };

function pushFeed(change) {
  const parts = [];
  const list = (items) => items.slice(0, 2).map((n) => (n.kind === 'member' ? store.label(n.id) || n.name : n.name)).join(', ') + (items.length > 2 ? ` and ${items.length - 2} more` : '');

  for (const kind of ['project', 'package', 'endpoint', 'type', 'member']) {
    const added = change.added.filter((n) => n.kind === kind);
    const removed = change.removed.filter((n) => n.kind === kind);
    const changed = change.changed.filter((n) => n.kind === kind);
    if (added.length) parts.push(['+', `${plural(added.length, kind)}: ${list(added)}`]);
    if (removed.length) parts.push(['-', `${plural(removed.length, kind)}: ${removed.slice(0, 2).map((n) => n.name).join(', ')}${removed.length > 2 ? ' and more' : ''}`]);
    if (changed.length) parts.push(['~', `${plural(changed.length, kind)} changed: ${list(changed)}`]);
  }

  // Lines between things that existed before and still exist; the rest is implied by the items above.
  const fresh = new Set(change.added.map((n) => n.id));
  const steady = (e) => FEED_WORDS[e.kind] && store.node(e.source) && store.node(e.target) && !fresh.has(e.source) && !fresh.has(e.target);
  const say = (e) => `${store.label(e.source)} ${FEED_WORDS[e.kind]} ${store.label(e.target)}`;
  const plus = change.edgesAdded.filter(steady), minus = change.edgesRemoved.filter(steady);
  if (plus.length) parts.push(['+', say(plus[0]) + (plus.length > 1 ? ` and ${plus.length - 1} more` : '')]);
  if (minus.length) parts.push(['-', say(minus[0]) + (minus.length > 1 ? ` and ${minus.length - 1} more` : '')]);

  if (!parts.length) return;
  feed.unshift({
    time: new Date().toLocaleTimeString([], { hour12: false }),
    files: (change.files ?? []).map(fileName).join(', ') || 'whole solution',
    parts: parts.slice(0, 4),
    ms: change.ms,
    go: change.added[0]?.id ?? change.changed[0]?.id ?? plus[0]?.source ?? minus[0]?.source,
  });
  feed.length = Math.min(feed.length, 60);
  renderFeed();
}

function feedRow(entry) {
  const what = h('span', { class: 'what grow' }, h('b', null, entry.files), ' ');
  for (const [sign, text] of entry.parts) what.append(h('span', { class: sign === '+' ? 'plus' : sign === '-' ? 'minus' : '' }, ` ${sign} `), text);
  return [h('time', null, entry.time), what, h('span', { class: 'took' }, `${entry.ms} ms`)];
}

function renderFeed() {
  $('#feed-last').replaceChildren(...feedRow(feed[0]));
  $('#feed-log').replaceChildren(...feed.map((entry) =>
    h('button', { class: 'row', onclick: () => { if (entry.go && store.node(entry.go)) { select(entry.go); view.reveal(model.rep(entry.go)); } } }, ...feedRow(entry))));
}

$('#feed-last').addEventListener('click', () => {
  if (feed.length) $('#feed-log').hidden = !$('#feed-log').hidden;
});

// -------------------------------------------------------------------- find

const findInput = $('#find');
const findResults = $('#find-results');
const KIND_ORDER = { endpoint: 0, type: 1, project: 2, member: 3, package: 4 };
let found = [];
let foundIndex = 0;

function runFind() {
  const q = findInput.value.trim().toLowerCase();
  if (!q) {
    findResults.hidden = true;
    return;
  }

  const scored = [];
  for (const n of store.nodes.values()) {
    if (!(n.kind in KIND_ORDER)) continue;
    const name = n.name.toLowerCase();
    let score = name === q ? 0 : name.startsWith(q) ? 1 : name.includes(q) ? 2 : -1;
    if (score < 0 && n.kind === 'member' && store.label(n.id).toLowerCase().includes(q)) score = 3;
    if (score < 0) continue;
    if (n.kind === 'member' && n.meta.memberKind !== 'method') score += 2;
    scored.push({ n, score: score * 10 + KIND_ORDER[n.kind] });
  }
  scored.sort((a, b) => a.score - b.score || a.n.name.length - b.n.name.length || a.n.name.localeCompare(b.n.name));

  found = scored.slice(0, 14).map((x) => x.n);
  foundIndex = 0;
  findResults.hidden = false;
  findResults.innerHTML = found.length
    ? found.map((n, i) => `<button class="row ${store.zoneClass(n.id)}" data-i="${i}" aria-selected="${i === 0}">${findRow(n)}</button>`).join('')
    : '<p class="none">Nothing found. Try part of a class, method or route name.</p>';
}

function findRow(n) {
  if (n.kind === 'endpoint') return verbChip(n.meta.verb) + `<span class="route grow">${esc(n.meta.route)}</span>`;
  if (n.kind === 'type') return typeCode(n.meta.typeKind) + `<span class="grow">${esc(n.name)}</span><span class="dim">${esc(n.ns ?? '')}</span>`;
  if (n.kind === 'member') return `<span class="mk">M</span><span class="grow">${esc(store.label(n.id))}</span><span class="dim">${esc(n.meta.memberKind)}</span>`;
  return `<span class="grow">${esc(n.name)}</span><span class="dim">${esc(n.kind)}</span>`;
}

function pickFound(i) {
  const n = found[i];
  if (!n) return;
  findResults.hidden = true;
  findInput.value = '';
  findInput.blur();
  go(n.id, 'find');
}

findInput.addEventListener('input', runFind);
findInput.addEventListener('focus', runFind);
findInput.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    findResults.hidden = true;
    findInput.blur();
  } else if (e.key === 'Enter') {
    pickFound(foundIndex);
  } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    if (!found.length) return;
    foundIndex = (foundIndex + (e.key === 'ArrowDown' ? 1 : -1) + found.length) % found.length;
    findResults.querySelectorAll('.row').forEach((row, i) => row.setAttribute('aria-selected', String(i === foundIndex)));
  }
});
findResults.addEventListener('click', (e) => {
  const row = e.target.closest('[data-i]');
  if (row) pickFound(Number(row.dataset.i));
});

// -------------------------------------------------------------------- chrome wiring

document.querySelector('.seg[data-views]').addEventListener('click', (e) => {
  const button = e.target.closest('[data-view]');
  if (!button) return;
  state.view = button.dataset.view;

  // Flow and Focus need a subject; use the current selection when it fits.
  const sel = store.node(state.selected);
  if (state.view === 'flow' && !state.flow.root && (sel?.kind === 'endpoint' || sel?.kind === 'member')) state.flow.root = sel.id;
  if (state.view === 'focus' && !state.focus.id && store.typeOf(state.selected)) state.focus.id = store.typeOf(state.selected).id;
  renderView({ fit: true });
});

$('#zoom-in').addEventListener('click', () => view.zoomBy(1.25));
$('#zoom-out').addEventListener('click', () => view.zoomBy(0.8));
$('#zoom-fit').addEventListener('click', () => view.fit());
$('#notes').addEventListener('click', () => inspector.show({ notes: store.info.diagnostics ?? [] }));

$('#theme').addEventListener('click', () => {
  const root = document.documentElement;
  const dark = root.dataset.theme ? root.dataset.theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
  root.dataset.theme = dark ? 'light' : 'dark';
  remember.set('theme', root.dataset.theme);
});
const savedTheme = remember.get('theme', null);
if (savedTheme) document.documentElement.dataset.theme = savedTheme;

document.addEventListener('pointerdown', (e) => {
  if (!e.target.closest('.find')) findResults.hidden = true;
  if (!e.target.closest('.feed')) $('#feed-log').hidden = true;
});

document.addEventListener('keydown', (e) => {
  const typing = e.target.closest?.('input, textarea, .CodeMirror');
  if (e.key === 'Escape') $('#feed-log').hidden = true;
  if (typing || e.ctrlKey || e.metaKey || e.altKey) return;

  if (e.key === '/') {
    e.preventDefault();
    findInput.focus();
    findInput.select();
  } else if (e.key === 'f') {
    view.fit();
  } else if (['1', '2', '3'].includes(e.key)) {
    document.querySelector(`.seg [data-view="${['map', 'flow', 'focus'][Number(e.key) - 1]}"]`).click();
  }
});

/** Drag handles that resize the right panel and the split between details and code. */
function dragResize(handle, onMove) {
  handle.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    handle.setPointerCapture(e.pointerId);
    const stop = () => {
      handle.removeEventListener('pointermove', onMove);
      handle.removeEventListener('pointerup', stop);
    };
    handle.addEventListener('pointermove', onMove);
    handle.addEventListener('pointerup', stop);
  });
}

dragResize($('.grip-x'), (e) => {
  const width = Math.min(window.innerWidth - 420, Math.max(300, window.innerWidth - e.clientX));
  document.documentElement.style.setProperty('--insp-w', width + 'px');
  remember.set('inspectorWidth', width);
});
dragResize($('.grip-y'), (e) => {
  const box = $('.insp').getBoundingClientRect();
  $('.insp').style.setProperty('--detail-h', Math.min(box.height - 120, Math.max(80, e.clientY - box.top)) + 'px');
});
const savedWidth = remember.get('inspectorWidth', null);
if (savedWidth) document.documentElement.style.setProperty('--insp-w', Math.min(savedWidth, window.innerWidth - 420) + 'px');

window.addEventListener('resize', () => {
  if (!state.selected) view.fit();
});

sidebar.render();
renderChrome();
renderStatus();
load();
