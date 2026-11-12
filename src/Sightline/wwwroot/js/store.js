// Holds the graph in the browser and applies live patches from the server.

const IMPL = new Set(['impl']);

export class Store extends EventTarget {
  constructor() {
    super();
    this.status = { state: 'loading', message: 'Starting' };
    this.reset({ version: 0, nodes: [], edges: [], services: [], info: {} }, true);
  }

  reset(graph, silent = false) {
    this.version = graph.version;
    this.info = graph.info ?? {};
    if (graph.status) this.status = graph.status;
    this.nodes = new Map(graph.nodes.map((n) => [n.id, n]));
    this.edges = new Map(graph.edges.map((e) => [e.id, e]));
    this.services = graph.services ?? [];
    this.#index();
    if (!silent) this.dispatchEvent(new CustomEvent('reset'));
  }

  /** Applies one patch. Returns false when a patch was missed and the graph must be fetched again. */
  apply(p) {
    if (p.base !== this.version) return false;

    const change = {
      files: p.files, ms: p.ms, reason: p.reason,
      added: [], removed: [], changed: [], edgesAdded: [], edgesRemoved: [],
      touched: new Set(),
    };

    for (const id of p.nodes.removed) {
      const n = this.nodes.get(id);
      if (n) { change.removed.push(n); this.nodes.delete(id); }
    }
    for (const n of p.nodes.added) { this.nodes.set(n.id, n); change.added.push(n); change.touched.add(n.id); }
    for (const n of p.nodes.changed) { this.nodes.set(n.id, n); change.changed.push(n); change.touched.add(n.id); }
    for (const n of p.nodes.moved) this.nodes.set(n.id, n);

    for (const id of p.edges.removed) {
      const e = this.edges.get(id);
      if (e) { change.edgesRemoved.push(e); this.edges.delete(id); }
    }
    for (const e of p.edges.added) { this.edges.set(e.id, e); change.edgesAdded.push(e); }
    for (const e of p.edges.changed) { this.edges.set(e.id, e); change.touched.add(e.source); change.touched.add(e.target); }
    for (const e of p.edges.moved) this.edges.set(e.id, e);

    for (const e of [...change.edgesAdded, ...change.edgesRemoved]) { change.touched.add(e.source); change.touched.add(e.target); }

    const servicesChanged = Boolean(p.services);
    if (p.services) this.services = p.services;
    if (p.info) this.info = p.info;
    this.version = p.version;

    // Line-only shifts do not change the picture; views can skip a re-layout.
    change.structural = servicesChanged || change.touched.size > 0 || change.removed.length > 0 || p.edges.changed.length > 0;

    this.#index();
    this.dispatchEvent(new CustomEvent('patch', { detail: change }));
    return true;
  }

  #index() {
    this.out = new Map();
    this.into = new Map();
    this.kids = new Map();

    for (const e of this.edges.values()) {
      push(this.out, e.source, e);
      push(this.into, e.target, e);
    }
    for (const n of this.nodes.values()) if (n.parent) push(this.kids, n.parent, n);

    this.projects = [...this.nodes.values()].filter((n) => n.kind === 'project').sort(byName);
    this.zones = new Map(this.projects.map((p, i) => [p.id, i % 8]));
  }

  node(id) { return this.nodes.get(id); }

  outOf(id, kinds) {
    const list = this.out.get(id) ?? [];
    return kinds ? list.filter((e) => kinds.has(e.kind)) : list;
  }

  intoOf(id, kinds) {
    const list = this.into.get(id) ?? [];
    return kinds ? list.filter((e) => kinds.has(e.kind)) : list;
  }

  childrenOf(id, kind) {
    const list = this.kids.get(id) ?? [];
    return kind ? list.filter((n) => n.kind === kind) : list;
  }

  /** The type that owns a member, or the type itself. */
  typeOf(id) {
    const n = this.nodes.get(id);
    if (n?.kind === 'member') return this.nodes.get(n.parent);
    return n?.kind === 'type' ? n : undefined;
  }

  /** Colour slot of the project a node belongs to. */
  zone(id) {
    const n = this.nodes.get(id);
    if (!n) return -1;
    return this.zones.get(n.kind === 'project' ? n.id : n.project) ?? -1;
  }

  zoneClass(id) {
    const z = this.zone(id);
    return z < 0 ? '' : 'z' + z;
  }

  label(id) {
    const n = this.nodes.get(id);
    if (!n) return '';
    if (n.kind === 'member') return `${this.nodes.get(n.parent)?.name ?? ''}.${n.name}`;
    return n.name;
  }

  /** Methods that implement an interface or abstract method. Prefers the DI-registered class. */
  implementations(memberId) {
    const impls = this.outOf(memberId, IMPL).map((e) => this.nodes.get(e.target)).filter(Boolean);
    if (impls.length < 2) return impls;

    const iface = this.nodes.get(memberId)?.parent;
    const registered = new Set(this.services.filter((s) => s.serviceId === iface && s.implId).map((s) => s.implId));
    const preferred = impls.filter((m) => registered.has(m.parent));
    return preferred.length ? preferred : impls;
  }

  /** Interface or base methods that this method implements. */
  abstractions(memberId) {
    return this.intoOf(memberId, IMPL).map((e) => this.nodes.get(e.source)).filter(Boolean);
  }

  endpoints() {
    return [...this.nodes.values()].filter((n) => n.kind === 'endpoint')
      .sort((a, b) => a.meta.route.localeCompare(b.meta.route) || a.meta.verb.localeCompare(b.meta.verb));
  }

  types() {
    return [...this.nodes.values()].filter((n) => n.kind === 'type');
  }
}

function push(map, key, value) {
  const list = map.get(key);
  if (list) list.push(value);
  else map.set(key, [value]);
}

export const byName = (a, b) => a.name.localeCompare(b.name);
