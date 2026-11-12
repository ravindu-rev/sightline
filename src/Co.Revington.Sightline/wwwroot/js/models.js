// Builds what each view shows from the graph in the store.
// All text that comes from the analyzed code passes through esc() before it becomes markup.

import { esc, icon, typeCode, verbChip, tagLabel, plural } from './ui.js';
import { byName } from './store.js';

/** Edge kind -> filter group shown as a toggle in the toolbar. */
export const KIND_GROUP = {
  injects: 'inject', calls: 'call', dispatches: 'call', handles: 'call', db: 'call', http: 'call',
  inherits: 'inherit', implements: 'inherit', registers: 'inherit', resolves: 'inherit',
  creates: 'create', references: 'ref',
};

export const KIND_GROUPS = [['inject', 'Injects'], ['call', 'Calls'], ['inherit', 'Inherits'], ['create', 'Creates'], ['ref', 'References']];

export const KIND_WORDS = {
  projectRef: 'project reference', unused: 'project reference, no usage found', packageRef: 'package reference',
  injects: 'injects', calls: 'calls', dispatches: 'sends a message to', handles: 'handled by', db: 'database access',
  http: 'outbound HTTP call', inherits: 'inherits', implements: 'implements', resolves: 'resolves to',
  creates: 'creates', references: 'uses type',
};

// When several kinds connect the same two nodes, the first one listed here picks the line style.
const RANK = ['projectRef', 'unused', 'inherits', 'implements', 'resolves', 'injects', 'dispatches', 'handles', 'calls', 'db', 'http', 'creates', 'references', 'packageRef'];

const SHOWN_TAGS = new Set(['controller', 'dbcontext', 'hosted', 'hub', 'middleware', 'handler', 'entrypoint']);

const ARROW = '→';

// -------------------------------------------------------------------- node builders

/** A dot in the corner of a station that has problems; its colour says how bad the worst one is. */
export function badge(store, id) {
  const severity = store.severityOf(id);
  if (!severity) return '';
  const list = store.problemsFor(id);
  return `<span class="pb pb-${severity}" title="${esc(list.map((p) => p.title).join('\n'))}"></span>`;
}

/** Marks a station the running app confirmed (it saw the endpoint or the registration). */
function runtimeMark(n) {
  return n.meta?.runtime === 'only' ? '<span class="chip run" title="Seen only in the running app">runtime</span>' : '';
}

function typeNode(store, t, extra = {}) {
  // extra.plain: name only. extra.compact: name and role, no DI lifetime. Both keep nodes narrow.
  const tag = extra.plain ? null : (t.meta.tags ?? []).find((x) => SHOWN_TAGS.has(x));
  return {
    id: t.id, kind: 'type', zone: store.zoneClass(t.id), ref: t.id,
    html: typeCode(t.meta.typeKind) + `<span class="t">${esc(t.name)}</span>` +
      (tag ? `<span class="chip">${esc(tagLabel(tag))}</span>` : '') +
      (t.meta.lifetime && !extra.plain && !extra.compact ? `<span class="chip life">${esc(t.meta.lifetime)}</span>` : '') +
      badge(store, t.id),
    title: `${t.meta.typeKind} ${t.ns ? t.ns + '.' : ''}${t.name}`,
    ...extra,
  };
}

function memberNode(store, m, extra = {}) {
  const owner = store.node(m.parent);
  const callable = m.meta.memberKind === 'method' || m.meta.memberKind === 'ctor';
  const name = (m.meta.memberKind === 'ctor' ? 'new ' : '') + m.name + (callable ? '()' : '');
  // extra.note: how the flow got here ("via IOrderRepository"); extra.more: steps hidden by the depth limit
  return {
    id: m.id, kind: 'member', zone: store.zoneClass(m.id), ref: m.id,
    html: `<span class="col"><span class="s">${esc(owner?.name ?? '')}${extra.note ? ` <i>${esc(extra.note)}</i>` : ''}</span>` +
      `<span class="t">${esc(name)}</span></span>${extra.more ? `<span class="more">+${extra.more}</span>` : ''}${badge(store, m.id)}`,
    title: `${owner?.name ?? ''}.${m.meta.sig}`,
    ...extra,
  };
}

function endpointNode(store, ep, extra = {}) {
  return {
    id: ep.id, kind: 'endpoint', zone: store.zoneClass(ep.id), ref: ep.id,
    html: verbChip(ep.meta.verb) + `<span class="route">${esc(ep.meta.route)}</span>` +
      (ep.meta.auth ? `<span class="chip lock">auth</span>` : '') + runtimeMark(ep) + badge(store, ep.id),
    title: ep.name,
    ...extra,
  };
}

function projectNode(store, p, typeCount, endpointCount) {
  const counts = plural(typeCount, 'type') + (endpointCount ? ', ' + plural(endpointCount, 'endpoint') : '');
  return {
    id: p.id, kind: 'project', zone: store.zoneClass(p.id), ref: p.id,
    html: `<span class="col"><span class="t">${esc(p.name)}</span><span class="meta">` +
      `<span class="chip">${esc(p.meta.projectKind)}</span>` +
      (p.meta.tfm ? `<span class="chip">${esc(p.meta.tfm)}</span>` : '') +
      `<span class="s">${esc(counts)}</span></span></span>${badge(store, p.id)}`,
    title: 'Double-click to open this project',
  };
}

function terminalNode(id, kind, name, detail, ref) {
  return {
    id, kind: 'terminal', cls: 't-' + kind, ref,
    html: icon(kind === 'db' ? 'db' : 'globe', 17) +
      `<span class="col"><span class="t">${esc(name)}</span>${detail ? `<span class="s">${esc(detail)}</span>` : ''}</span>`,
    title: kind === 'db' ? 'Database access' : 'Outbound HTTP call',
  };
}

function nodeFor(store, n, extra) {
  if (n.kind === 'member') return memberNode(store, n, extra);
  if (n.kind === 'endpoint') return endpointNode(store, n, extra);
  if (n.kind === 'type') return typeNode(store, n, extra);
  if (n.kind === 'external') return terminalNode(n.id, n.meta.externalKind === 'db' ? 'db' : 'http', n.name, '', n.id);
  return { id: n.id, kind: n.kind, ref: n.id, html: `<span class="t">${esc(n.name)}</span>`, ...extra };
}

export function describeKinds(kinds) {
  return [...kinds]
    .sort((a, b) => RANK.indexOf(a[0]) - RANK.indexOf(b[0]))
    .map(([k, n]) => `${KIND_WORDS[k] ?? k}${n > 1 ? ' (' + n + ')' : ''}`)
    .join(', ');
}

function push(map, key, value) {
  const list = map.get(key);
  if (list) list.push(value);
  else map.set(key, [value]);
}

/** "Shop.Application.Orders" inside project "Shop.Application" reads as "Orders". */
function shortNamespace(ns, projectName) {
  if (!ns) return '(global)';
  if (ns === projectName) return ns;
  return ns.startsWith(projectName + '.') ? ns.slice(projectName.length + 1) : ns;
}

// -------------------------------------------------------------------- map: projects, namespaces, types

// Projects are always connected by their reference lines. Inside an open project, types sit in
// tidy grids per namespace, and the lines of a type only appear when it is hovered or selected.
// Drawing every type-level line at once turns any real solution into a hairball.
export function mapModel(store, st) {
  const nodes = [];
  const rep = new Map();              // graph id -> id of the visible node that stands for it
  const collapsed = new Set();

  for (const p of store.projects) {
    const zone = store.zoneClass(p.id);
    const all = store.childrenOf(p.id, 'type');
    const types = all.filter((t) => !t.meta.container).sort(byName);
    const endpoints = store.childrenOf(p.id, 'endpoint');

    if (!st.expanded.has(p.id)) {
      collapsed.add(p.id);
      nodes.push(projectNode(store, p, types.length, endpoints.length));
      rep.set(p.id, p.id);
      for (const t of all) rep.set(t.id, p.id);
      for (const ep of endpoints) rep.set(ep.id, p.id);
      continue;
    }

    nodes.push({
      id: p.id, group: true, kind: 'project', zone, ref: p.id,
      html: `${esc(p.name)}<span class="chip">${esc(p.meta.projectKind)}</span>`,
      title: 'Double-click the name to close this project',
    });

    const byNs = new Map();
    for (const t of types) push(byNs, t.ns ?? '', t);
    const split = byNs.size > 1;
    const crowded = split && types.length > 80;          // very big projects open one namespace at a time

    for (const [ns, list] of [...byNs].sort((a, b) => a[0].localeCompare(b[0]))) {
      const nsId = `ns:${p.id}:${ns}`;
      const label = shortNamespace(ns, p.name);

      if (crowded && !st.expanded.has(nsId)) {
        nodes.push({
          id: nsId, parent: p.id, kind: 'ns', zone,
          html: `<span class="t">${esc(label)}</span><span class="s">${esc(plural(list.length, 'type'))}</span>`,
          title: 'Double-click to open this namespace',
        });
        for (const t of list) rep.set(t.id, nsId);
        continue;
      }

      if (split) nodes.push({ id: nsId, parent: p.id, group: true, kind: 'ns', zone, html: esc(label), title: ns });
      for (const t of list) {
        nodes.push(typeNode(store, t, { parent: split ? nsId : p.id, compact: true }));
        rep.set(t.id, t.id);
      }
    }

    // Nested types and endpoints are drawn as part of the type that holds them.
    for (const t of all) {
      if (!t.meta.container) continue;
      let outer = t;
      while (outer.meta.container && store.node(outer.meta.container)) outer = store.node(outer.meta.container);
      rep.set(t.id, rep.get(outer.id));
    }
    for (const ep of endpoints) {
      const owner = store.typeOf(ep.meta.handler ?? ep.meta.declaredIn);
      if (owner) rep.set(ep.id, rep.get(owner.id));
    }
  }

  if (st.packages) {
    for (const k of [...store.nodes.values()].filter((n) => n.kind === 'package').sort(byName)) {
      nodes.push({
        id: k.id, kind: 'package', ref: k.id,
        html: `<span class="t">${esc(k.name)}</span><span class="s">${esc((k.meta.versions ?? []).join(', '))}</span>`,
        title: 'NuGet package',
      });
      rep.set(k.id, k.id);
    }
  }

  const repOf = (id) => {
    const n = store.node(id);
    if (!n) return null;
    return rep.get(n.kind === 'member' ? n.parent : id) ?? null;
  };

  /** The project (or shown package) a graph node belongs to. */
  const projectOf = (id) => {
    const n = store.node(id);
    if (!n) return null;
    if (n.kind === 'project') return n.id;
    if (n.kind === 'package') return st.packages ? n.id : null;
    return n.project ?? null;
  };

  // Many graph edges land on the same pair of visible nodes; merge them into one line.
  const merge = (map, s, t, e) => {
    const key = s + ARROW + t;
    let m = map.get(key);
    if (!m) map.set(key, (m = { id: key, source: s, target: t, kinds: new Map(), refs: [] }));
    m.kinds.set(e.kind, (m.kinds.get(e.kind) ?? 0) + (e.count ?? 1));
    m.refs.push(e.id);
  };

  // Lines between projects follow the references written in the project files. Code that uses a
  // project it only reaches through another one adds to no line of its own; it shows when hovered.
  // Drawing every such use as a line turns a big solution into a mesh.
  const references = new Set();
  for (const e of store.edges.values())
    if (e.kind === 'projectRef' || e.kind === 'packageRef') references.add(e.source + ARROW + e.target);

  const between = new Map();          // project -> project: always drawn
  const within = new Map();           // drawn for the hovered or selected node
  for (const e of store.edges.values()) {
    const isRef = e.kind === 'projectRef' || e.kind === 'packageRef';
    if (e.kind === 'registers' || (!isRef && !(e.kind in KIND_GROUP))) continue;

    const ps = projectOf(e.source), pt = projectOf(e.target);
    const direct = ps && pt && ps !== pt && references.has(ps + ARROW + pt);
    if (direct) merge(between, ps, pt, e);
    if (isRef || !st.kinds.has(KIND_GROUP[e.kind])) continue;

    const s = repOf(e.source), t = repOf(e.target);
    if (!s || !t || s === t) continue;
    if (direct && collapsed.has(s) && collapsed.has(t)) continue;       // the project line already says this
    merge(within, s, t, e);
  }

  const sorted = (map) => [...map.values()].sort((a, b) => a.id.localeCompare(b.id));

  const edges = sorted(between).map((m) => {
    const uses = [...m.kinds].filter(([k]) => k !== 'projectRef' && k !== 'packageRef').reduce((sum, [, c]) => sum + c, 0);
    let kind = RANK.find((k) => m.kinds.has(k));
    let label = '';
    if (kind === 'projectRef' && uses === 0) [kind, label] = ['unused', 'no usage found'];
    else if (kind !== 'packageRef') label = plural(uses, 'use');
    return { ...m, kind, label, title: describeKinds(m.kinds) };
  });

  const links = sorted(within).map((m) => ({ ...m, kind: RANK.find((k) => m.kinds.has(k)), title: describeKinds(m.kinds) }));

  return { key: 'map', direction: 'AUTO', nodes, edges, links, rep: repOf };
}

// -------------------------------------------------------------------- flow: what happens after an endpoint or method

const STEP_KINDS = new Set(['handles', 'calls', 'dispatches', 'db', 'http']);
const CALLER_KINDS = new Set(['handles', 'calls', 'dispatches']);

// A flow stops growing at this many stations; the rest shows as "+N" on the stations where it stops.
// Past this, a picture is unreadable anyway, and laying it out takes seconds.
const FLOW_BUDGET = 160;

export function flowModel(store, st) {
  const root = store.node(st.root);
  const nodes = new Map();
  const edges = new Map();
  if (!root) return { key: 'flow:', direction: 'RIGHT', nodes: [], edges: [], rep: () => null };

  let truncated = 0;
  const full = () => nodes.size >= FLOW_BUDGET;
  const notes = new Map();            // node id -> how the flow reaches it, shown inside the node
  const add = (id, spec) => { if (!nodes.has(id)) nodes.set(id, spec); };
  const link = (source, target, kind, extra = {}) => {
    const id = `${source}${ARROW}${target}${ARROW}${kind}`;
    if (!edges.has(id)) edges.set(id, { id, source, target, kind, title: KIND_WORDS[kind], refs: [], ...extra });
    return edges.get(id);
  };
  const note = (id, text) => {
    if (!text) return;
    if (!notes.has(id)) notes.set(id, new Set());
    notes.get(id).add(text);
  };

  const down = (id, depth) => {
    if (nodes.has(id)) return;
    const n = store.node(id);
    const unresolved = n.kind === 'member' && n.meta.abstract;
    add(id, nodeFor(store, n, { cls: (id === root.id ? 'root ' : '') + (unresolved ? 'unresolved' : '') }));

    const steps = store.outOf(id, STEP_KINDS).slice().sort((a, b) => (a.line ?? 0) - (b.line ?? 0));
    if (!steps.length) return;
    if (depth >= st.depth || full()) {
      truncated++;
      nodes.get(id).more = steps.length;
      return;
    }

    steps.forEach((e, i) => {
      const step = steps.length > 1 ? String(i + 1) : '';
      const target = store.node(e.target);
      if (!target) return;

      // Database and HTTP ends are drawn once per caller so lines stay short.
      if (e.kind === 'db' || e.kind === 'http') {
        const tid = `${id}>${e.kind}`;
        add(tid, terminalNode(tid, e.kind, e.kind === 'db' ? target.name : 'HTTP call', e.label ?? '', e.target));
        link(id, tid, e.kind, { step }).refs.push(e.id);
        return;
      }

      // A call on an interface continues in the class that implements it.
      if (target.kind === 'member' && target.meta.abstract) {
        const impls = store.implementations(target.id);
        if (impls.length) {
          for (const impl of impls) {
            link(id, impl.id, e.kind, { step }).refs.push(e.id);
            note(impl.id, 'via ' + (store.node(target.parent)?.name ?? 'interface'));
            down(impl.id, depth + 1);
          }
          return;
        }
      }

      link(id, target.id, e.kind, { step }).refs.push(e.id);
      if (e.kind === 'dispatches' && e.label) note(target.id, 'handles ' + e.label);
      if (target.kind === 'member' || target.kind === 'endpoint') down(target.id, depth + 1);
      else add(target.id, nodeFor(store, target));
    });
  };

  const up = (id, depth) => {
    if (nodes.has(id)) return;
    const n = store.node(id);
    add(id, nodeFor(store, n, { cls: id === root.id ? 'root' : '' }));

    // Direct callers, plus callers that go through an interface this method implements.
    const callers = store.intoOf(id, CALLER_KINDS).map((e) => ({ e, via: '' }));
    for (const a of store.abstractions(id))
      for (const e of store.intoOf(a.id, CALLER_KINDS)) callers.push({ e, via: store.node(a.parent)?.name ?? '' });

    if (!callers.length) return;
    if (depth >= st.depth || full()) {
      truncated++;
      nodes.get(id).more = callers.length;
      return;
    }

    for (const { e, via } of callers) {
      const source = store.node(e.source);
      if (!source) continue;
      up(source.id, depth + 1);
      link(source.id, id, e.kind).refs.push(e.id);
      note(id, via ? 'via ' + via : e.kind === 'dispatches' && e.label ? 'handles ' + e.label : '');
    }
  };

  if (st.dir === 'up') up(root.id, 0);
  else down(root.id, 0);

  // Method nodes are rebuilt now that their notes and hidden-step counts are known.
  const finished = [...nodes.values()].map((spec) => {
    const n = store.node(spec.ref);
    if (spec.kind !== 'member' || !n) return spec;
    return memberNode(store, n, { cls: spec.cls, more: spec.more, note: [...(notes.get(spec.id) ?? [])].join(', ') });
  });

  return {
    key: `flow:${root.id}:${st.dir}`,
    direction: 'AUTO',                 // left to right, or top to bottom when that keeps the text larger
    layerGap: 48,
    nodes: finished,
    edges: [...edges.values()],
    truncated,
    rep: (id) => (nodes.has(id) ? id : null),
  };
}

// -------------------------------------------------------------------- focus: one type and its neighbours

export function focusModel(store, st) {
  const center = store.typeOf(st.id);
  if (!center) return { key: 'focus:', direction: 'RIGHT', nodes: [], edges: [], rep: () => null };

  const nodes = new Map();
  const merged = new Map();
  nodes.set(center.id, typeNode(store, center, { cls: 'center' }));

  // Members and nested things collapse to their type; endpoints and externals stay as they are.
  const repOf = (id) => {
    const n = store.node(id);
    if (!n) return null;
    if (n.kind === 'member') return n.parent;
    return n.kind === 'type' || n.kind === 'endpoint' || n.kind === 'external' ? n.id : null;
  };

  const add = (source, target, kind, e, usersOnly = false) => {
    if (!source || !target || source === target) return;
    const resolves = kind === 'implements' || kind === 'registers';
    if (resolves && usersOnly) return;
    // Show "interface resolves to class" left to right, instead of an arrow pointing backwards.
    if (kind === 'implements') [source, target, kind] = [target, source, 'resolves'];
    else if (kind === 'registers') kind = 'resolves';
    else if (!st.kinds.has(KIND_GROUP[kind])) return;

    for (const id of [source, target]) {
      if (nodes.has(id)) continue;
      const n = store.node(id);
      if (n) nodes.set(id, nodeFor(store, n, { plain: true }));
    }
    const key = source + ARROW + target;
    let m = merged.get(key);
    if (!m) merged.set(key, (m = { id: key, source, target, kinds: new Map(), refs: [] }));
    m.kinds.set(kind, (m.kinds.get(kind) ?? 0) + (e.count ?? 1));
    m.refs.push(e.id);
  };

  const collect = (typeId, usersOnly) => {
    const own = [typeId, ...store.childrenOf(typeId, 'member').map((m) => m.id)];
    for (const id of own) {
      if (!usersOnly)
        for (const e of store.outOf(id)) if (e.kind in KIND_GROUP) add(typeId, repOf(e.target), e.kind, e);
      for (const e of store.intoOf(id)) if (e.kind in KIND_GROUP) add(repOf(e.source), typeId, e.kind, e, usersOnly);
    }
  };

  collect(center.id, false);

  // A class is usually used through its interface, so also show who uses that interface
  // (but not the interface's other implementations, which are a different story).
  if (center.meta.typeKind !== 'interface') {
    const interfaces = [...merged.values()].filter((m) => m.target === center.id && m.kinds.has('resolves')).map((m) => m.source);
    for (const id of interfaces) collect(id, true);
  }

  // No text on these lines: the line style says how two types relate, hover or click gives the rest.
  const edges = [...merged.values()].sort((a, b) => a.id.localeCompare(b.id))
    .map((m) => ({ ...m, kind: RANK.find((k) => m.kinds.has(k)), title: describeKinds(m.kinds) }));

  return {
    key: 'focus:' + center.id,
    direction: 'AUTO',                 // users before the type and what it uses after it, across or down
    layerGap: 56,
    nodes: [...nodes.values()],
    edges,
    rep: (id) => {
      const r = repOf(id);
      return r && nodes.has(r) ? r : null;
    },
  };
}
