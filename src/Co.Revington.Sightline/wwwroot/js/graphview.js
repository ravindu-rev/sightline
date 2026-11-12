// Draws a view model: HTML boxes for nodes, SVG lines for edges, plus pan and zoom.
//
// A view model is {
//   key, direction,
//   nodes: [{ id, parent, group, kind, zone, cls, html, title }],
//   edges: [{ id, source, target, kind, label, step, title }],   always drawn; ELK routes them between top-level nodes
//   links: [{ id, source, target, kind, title }],                drawn only for the hovered or selected node
// }
// Groups (open projects, namespaces) are packed here as grids; ELK only arranges the top level.
// Node html is built by models.js, which escapes all text taken from the analyzed code.

const SVG_NS = 'http://www.w3.org/2000/svg';
const elk = new ELK();

const LAYOUT = {
  'elk.algorithm': 'layered',
  'elk.edgeRouting': 'ORTHOGONAL',
  'elk.layered.spacing.nodeNodeBetweenLayers': '70',
  'elk.layered.spacing.edgeNodeBetweenLayers': '24',
  'elk.layered.spacing.edgeEdgeBetweenLayers': '12',
  'elk.spacing.nodeNode': '16',
  'elk.spacing.edgeNode': '16',
  'elk.spacing.edgeEdge': '12',
  'elk.spacing.componentComponent': '30',
  'elk.layered.considerModelOrder.strategy': 'NODES_AND_EDGES',
  'elk.layered.nodePlacement.strategy': 'NETWORK_SIMPLEX',
  'elk.padding': '[top=20,left=20,bottom=20,right=20]',
};

// Room inside a group for its title and border.
const PADDING = {
  project: { top: 44, side: 16, bottom: 16 },
  ns: { top: 30, side: 12, bottom: 12 },
};

// Edge kind -> arrow head style.
const MARKERS = {
  projectRef: 'ink', unused: 'ink3', injects: 'signal', resolves: 'signal', inherits: 'hollow', implements: 'hollow',
  dispatches: 'send', db: 'data', http: 'net', creates: 'ink3', references: 'ink3', packageRef: 'ink3',
};

const STATE_CLASSES = ['enter', 'sel', 'hl', 'pulse', 'hit'];

export class GraphView {
  constructor(host, handlers) {
    this.host = host;
    this.handlers = handlers;

    this.stage = div('stage');
    this.groupLayer = div('layer');
    this.nodeLayer = div('layer');
    this.svg = svg('svg', { class: 'edges' });
    this.svg.innerHTML = markerDefs();
    this.edgeLayer = svg('g');
    this.svg.append(this.edgeLayer);
    this.linkSvg = svg('svg', { class: 'edges links' });      // on-demand lines, above the nodes
    this.stage.append(this.groupLayer, this.svg, this.nodeLayer, this.linkSvg);
    host.prepend(this.stage);

    this.els = new Map();          // node id -> element
    this.edgeEls = new Map();      // edge id -> { path, parts: [] }
    this.pos = new Map();          // node id -> { x, y, w, h } from the last layout
    this.choices = new Map();      // view key -> arrangement picked last time (for 'AUTO')
    this.entered = [];
    this.model = { key: '', nodes: [], edges: [], links: [] };
    this.view = { x: 40, y: 70, k: 1 };
    this.bounds = { w: 0, h: 0 };
    this.seq = 0;
    this.selected = null;
    this.selectedEdge = null;
    this.hovered = null;

    this.#bind();
    this.#applyView();
  }

  // ---------------------------------------------------------------- render

  async render(model, { fit = false } = {}) {
    const seq = ++this.seq;
    const fresh = model.key !== this.model.key;
    this.model = model;

    // Remembered across renders: a newer render may replace this one before its layout is ready,
    // and the request to fit or to start fresh must not get lost with it.
    this.wantFit ||= fit || fresh;
    this.wantFresh ||= fresh;

    this.#reconcile(model);
    const sizes = this.#measure(model);

    let layout;
    try {
      layout = await this.#layout(model, sizes);
    } catch (err) {
      console.error('Sightline layout failed', err);
      return;
    }
    if (seq !== this.seq) return;            // a newer render took over while ELK was working

    const jump = this.wantFresh;
    this.wantFresh = false;
    this.pos = layout.pos;
    this.#place(model, layout, jump);
    this.#drawEdges(model, layout, jump);
    this.bounds = { w: layout.width, h: layout.height };
    if (this.wantFit) {
      this.wantFit = false;
      this.fit();
    }
    if (this.hovered && !this.els.has(this.hovered)) this.hovered = null;
    this.#markSelection();
  }

  /** Creates, updates and removes DOM elements so they match the model. */
  #reconcile(model) {
    const keep = new Set();
    this.entered = [];                        // ids of nodes that are new in this render

    for (const n of model.nodes) {
      keep.add(n.id);
      const isGroup = Boolean(n.group);
      let el = this.els.get(n.id);

      if (el && el.__group !== isGroup) {     // a node that became a group (opened) or the reverse
        el.remove();
        el = null;
      }
      if (!el) {
        this.entered.push(n.id);
        el = document.createElement('div');
        el.__group = isGroup;
        el.dataset.id = n.id;
        el.classList.add('enter');
        if (!isGroup) {
          el.tabIndex = 0;
          el.setAttribute('role', 'button');
        }
        (isGroup ? this.groupLayer : this.nodeLayer).append(el);
        this.els.set(n.id, el);
      }

      const cls = `${isGroup ? 'g g-' : 'n n-'}${n.kind} ${n.zone ?? ''} ${n.cls ?? ''}`;
      const html = isGroup ? `<header><span class="gh">${n.html}</span></header>` : n.html;

      if (el.__cls !== cls || el.__html !== html) {
        const state = STATE_CLASSES.filter((c) => el.classList.contains(c));
        el.className = cls;
        el.classList.add(...state);
        if (el.__html !== html) el.innerHTML = html;
        el.__cls = cls;
        el.__html = html;
        el.__size = null;                      // content changed: measure again
        if (!isGroup) el.style.width = '';
      }
      el.title = n.title ?? '';
    }

    for (const [id, el] of this.els) {
      if (keep.has(id)) continue;
      el.remove();
      this.els.delete(id);
    }
  }

  #measure(model) {
    const sizes = new Map();
    for (const n of model.nodes) {
      const el = this.els.get(n.id);
      if (!el.__size) {
        el.__size = n.group
          ? { w: (el.querySelector('.gh')?.offsetWidth ?? 80) + 36, h: 0 }     // room the title needs
          : { w: Math.ceil(el.offsetWidth) + 1, h: el.offsetHeight };
      }
      sizes.set(n.id, el.__size);
    }
    return sizes;
  }

  /** direction 'AUTO' tries a few arrangements and keeps the one that shows the most at a readable size. */
  async #layout(model, sizes) {
    if (model.direction !== 'AUTO') return this.#arrange(model, sizes, model.direction ?? 'RIGHT', 1.8);

    const box = this.host.getBoundingClientRect();
    // The shape of a group only matters when there are groups to pack.
    const options = model.nodes.some((n) => n.group)
      ? [['RIGHT', 0.7], ['RIGHT', 1.8], ['DOWN', 1.8], ['DOWN', 3.4]]
      : [['RIGHT', 1.8], ['DOWN', 1.8]];
    const previous = this.choices.get(model.key);

    let best = null;
    for (const [direction, aspect] of options) {
      const layout = await this.#arrange(model, sizes, direction, aspect);
      const choice = direction + aspect;
      // Bigger than life size is no gain: arrangements that both fit count as equal, and the first one wins.
      const scale = Math.min(1.1, (box.width - 56) / layout.width, (box.height - 120) / layout.height);
      // Stay with this view's previous arrangement unless another is clearly better, so it does not flip around.
      const score = scale * (choice === previous ? 1.15 : 1);
      if (!best || score > best.score) best = { layout, choice, score };
    }
    this.choices.set(model.key, best.choice);
    return best.layout;
  }

  /** aspect: how much wider than tall the inside of a group should be. */
  async #arrange(model, sizes, direction, aspect) {
    const byParent = new Map();
    for (const n of model.nodes) push(byParent, n.parent ?? '', n);

    // 1. Pack each group from the inside out: loose nodes as a grid, child groups as shelves.
    const local = new Map();                   // node id -> box relative to its parent group
    const pack = (group) => {
      const kids = byParent.get(group.id) ?? [];
      const leaves = kids.filter((k) => !k.group);
      const blocks = [];

      if (leaves.length) {
        const grid = packGrid(leaves, sizes, aspect);
        blocks.push({ w: grid.w, h: grid.h, put: (x, y) => grid.cells.forEach((c) => local.set(c.id, { ...c, x: x + c.x, y: y + c.y })) });
      }
      for (const child of kids.filter((k) => k.group)) {
        const box = pack(child);
        blocks.push({ w: box.w, h: box.h, put: (x, y) => local.set(child.id, { x, y, w: box.w, h: box.h }) });
      }

      const pad = PADDING[group.kind] ?? PADDING.ns;
      const shelves = packShelves(blocks, 12, aspect);
      shelves.at.forEach((p, i) => blocks[i].put(pad.side + p.x, pad.top + p.y));
      return {
        w: Math.max(shelves.w + pad.side * 2, sizes.get(group.id).w),
        h: Math.max(shelves.h + pad.top + pad.bottom, 46),
      };
    };

    // 2. ELK arranges the top level. Nodes without lines are packed into one grid block,
    //    because ELK would stack them in a single tall column.
    const connected = new Set();
    for (const e of model.edges) {
      connected.add(e.source);
      connected.add(e.target);
    }

    const children = [];
    const loose = [];
    for (const n of byParent.get('') ?? []) {
      if (n.group) {
        const box = pack(n);
        children.push({ id: n.id, width: box.w, height: box.h });
      } else if (connected.has(n.id)) {
        children.push({ id: n.id, width: sizes.get(n.id).w, height: sizes.get(n.id).h });
      } else {
        loose.push(n);
      }
    }

    let grid = null;
    if (loose.length) {
      grid = packGrid(loose, sizes, aspect);
      children.push({ id: '__grid', width: grid.w, height: grid.h });
    }

    const layoutOptions = { ...LAYOUT, 'elk.direction': direction };
    if (model.layerGap) layoutOptions['elk.layered.spacing.nodeNodeBetweenLayers'] = String(model.layerGap);

    const result = await elk.layout({
      id: 'root',
      layoutOptions,
      children,
      edges: model.edges.map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })),
    });

    // 3. Collect absolute positions.
    const pos = new Map();
    for (const c of result.children ?? []) pos.set(c.id, { x: c.x, y: c.y, w: c.width, h: c.height });

    if (grid) {
      const base = pos.get('__grid');
      for (const c of grid.cells) pos.set(c.id, { ...c, x: base.x + c.x, y: base.y + c.y });
      pos.delete('__grid');
    }

    const descend = (group) => {
      const base = pos.get(group.id);
      const kids = (byParent.get(group.id) ?? []).filter((k) => local.has(k.id));
      if (!base) return;

      // A single column of items is stretched to the full width, so it reads as one clean list.
      const side = (PADDING[group.kind] ?? PADDING.ns).side;
      const oneColumn = kids.every((k) => local.get(k.id).x === side);

      for (const k of kids) {
        const box = local.get(k.id);
        pos.set(k.id, { ...box, x: base.x + box.x, y: base.y + box.y, w: oneColumn ? base.w - side * 2 : box.w });
        if (k.group) descend(k);
      }
    };
    for (const n of byParent.get('') ?? []) if (n.group) descend(n);

    const routes = new Map();
    for (const e of result.edges ?? []) {
      const points = [];
      for (const s of e.sections ?? []) points.push(s.startPoint, ...(s.bendPoints ?? []), s.endPoint);
      if (points.length > 1) routes.set(e.id, points);
    }

    return { width: result.width ?? 0, height: result.height ?? 0, pos, routes };
  }

  #place(model, layout, fresh) {
    const settle = [];

    for (const n of model.nodes) {
      const el = this.els.get(n.id);
      const p = layout.pos.get(n.id);
      if (!el || !p) continue;

      const jump = fresh || el.classList.contains('enter');
      if (jump) {
        el.style.transition = 'none';         // new nodes appear in place instead of flying in from the corner
        settle.push(el);
      }
      el.style.transform = `translate(${p.x}px, ${p.y}px)`;
      el.style.width = p.w + 'px';
      if (n.group) el.style.height = p.h + 'px';
    }

    if (settle.length) {
      void this.stage.offsetWidth;            // commit the jump before transitions are switched back on
      for (const el of settle) {
        el.style.transition = '';
        el.classList.remove('enter');
      }
    }
  }

  #drawEdges(model, layout, fresh) {
    this.edgeLayer.textContent = '';
    this.edgeEls.clear();
    const frag = document.createDocumentFragment();
    const plates = [];

    for (const e of model.edges) {
      const points = layout.routes.get(e.id);
      if (!points) continue;

      const d = roundedPath(points, 9);
      const path = svg('path', { class: `e k-${e.kind}`, d, 'marker-end': `url(#m-${MARKERS[e.kind] ?? 'ink2'})` });
      frag.append(path, hitPath(d, e));

      const parts = [];
      // Step numbers sit near the arrow head: targets are spread out there, so plates do not pile up.
      if (e.step) parts.push(plate(pointAlong([...points].reverse(), 26), e.step, 'el step'));
      if (e.label) parts.push(plate(midOfLongest(points), e.label, 'el'));
      plates.push(...parts);
      frag.append(...parts);
      this.edgeEls.set(e.id, { path, parts });
    }

    this.edgeLayer.append(frag);

    // Size each label plate to its text (needs the text in the document first).
    for (const g of plates) {
      const w = Math.max(18, Math.ceil(g.lastChild.getComputedTextLength()) + 12);
      g.firstChild.setAttribute('x', -w / 2);
      g.firstChild.setAttribute('width', w);
    }

    // Lines are redrawn at once while stations glide; hide them until the stations arrive.
    clearTimeout(this.settleTimer);
    if (fresh) {
      this.svg.classList.remove('settling');
      this.linkSvg.classList.remove('settling');
    } else {
      this.svg.classList.add('settling');
      this.linkSvg.classList.add('settling');
      this.settleTimer = setTimeout(() => {
        this.svg.classList.remove('settling');
        this.linkSvg.classList.remove('settling');
      }, 260);
    }
  }

  /** Lines of the hovered node, or of the selected one when nothing is hovered. */
  #drawLinks() {
    this.linkSvg.textContent = '';
    const active = this.hovered ?? this.selected;
    if (!active) return;

    const frag = document.createDocumentFragment();
    for (const l of this.model.links ?? []) {
      if (l.source !== active && l.target !== active) continue;
      const a = this.pos.get(l.source), b = this.pos.get(l.target);
      if (!a || !b) continue;

      const d = curveBetween(a, b);
      frag.append(svg('path', { class: `e k-${l.kind}`, d, 'marker-end': `url(#m-${MARKERS[l.kind] ?? 'ink2'})` }), hitPath(d, l));
    }
    this.linkSvg.append(frag);
  }

  // ---------------------------------------------------------------- selection and highlights

  select(id) {
    this.selected = id;
    this.selectedEdge = null;
    this.#markSelection();
  }

  selectEdge(id) {
    this.selectedEdge = id;
    this.#markSelection();
  }

  #markSelection() {
    for (const el of this.stage.querySelectorAll('.sel')) el.classList.remove('sel');
    if (this.selected) this.els.get(this.selected)?.classList.add('sel');
    if (this.selectedEdge) this.edgeEls.get(this.selectedEdge)?.path.classList.add('sel');
    this.#drawLinks();
  }

  /** Lights the steps a real request reached. */
  markHits(ids) {
    const lit = new Set(ids);
    for (const [id, el] of this.els) el.classList.toggle('hit', lit.has(id));
  }

  /** Flashes nodes that just changed. */
  pulse(ids) {
    for (const id of ids) {
      const el = this.els.get(id);
      if (!el) continue;
      el.classList.remove('pulse');
      void el.offsetWidth;
      el.classList.add('pulse');
      el.addEventListener('animationend', () => el.classList.remove('pulse'), { once: true });
    }
  }

  #hover(id) {
    this.hovered = id;
    for (const el of this.stage.querySelectorAll('.hl')) el.classList.remove('hl');
    this.stage.classList.toggle('dim', Boolean(id));
    this.#drawLinks();
    if (!id) return;

    const byId = new Map(this.model.nodes.map((n) => [n.id, n]));
    const mark = (nodeId) => {
      // keep the enclosing zones readable too
      for (let n = byId.get(nodeId); n; n = byId.get(n.parent)) this.els.get(n.id)?.classList.add('hl');
    };

    mark(id);
    for (const e of [...this.model.edges, ...(this.model.links ?? [])]) {
      if (e.source !== id && e.target !== id) continue;
      mark(e.source);
      mark(e.target);
      const drawn = this.edgeEls.get(e.id);
      drawn?.path.classList.add('hl');
      drawn?.parts.forEach((p) => p.classList.add('hl'));
    }
    for (const path of this.linkSvg.querySelectorAll('path.e')) path.classList.add('hl');
  }

  // ---------------------------------------------------------------- viewport

  fit() {
    const { w, h } = this.bounds;
    const box = this.host.getBoundingClientRect();
    if (!w || !h || !box.width) return;

    const padX = 28, padTop = 64, padBottom = 56;
    const k = Math.min(1.1, (box.width - padX * 2) / w, (box.height - padTop - padBottom) / h);
    this.view.k = Math.max(0.15, k);
    this.view.x = (box.width - w * this.view.k) / 2;
    this.view.y = Math.max(padTop, (box.height - h * this.view.k) / 2);
    this.#applyView();
  }

  zoomBy(factor, clientX, clientY) {
    const box = this.host.getBoundingClientRect();
    const px = (clientX ?? box.left + box.width / 2) - box.left;
    const py = (clientY ?? box.top + box.height / 2) - box.top;
    const k = Math.min(2.5, Math.max(0.15, this.view.k * factor));
    const f = k / this.view.k;
    this.view.x = px - (px - this.view.x) * f;
    this.view.y = py - (py - this.view.y) * f;
    this.view.k = k;
    this.#applyView();
  }

  /** After a live update: if something that changed or just appeared is off screen, zoom to show everything. */
  showAll(ids) {
    const box = this.host.getBoundingClientRect();
    const { x, y, k } = this.view;
    const hidden = [...ids, ...this.entered].some((id) => {
      const p = this.pos.get(id);      // final position, not the one mid-animation
      if (!p) return false;
      const left = p.x * k + x, top = p.y * k + y;
      return left < 0 || top < 56 || left + p.w * k > box.width || top + p.h * k > box.height - 44;
    });
    if (hidden) this.fit();
  }

  /** Scrolls a node into the middle of the canvas if it is off screen. */
  reveal(id) {
    const el = this.els.get(id);
    if (!el) return;
    const box = this.host.getBoundingClientRect();
    const r = el.getBoundingClientRect();
    const inside = r.left >= box.left + 20 && r.right <= box.right - 20 && r.top >= box.top + 60 && r.bottom <= box.bottom - 50;
    if (inside) return;
    this.view.x += box.left + box.width / 2 - (r.left + r.width / 2);
    this.view.y += box.top + box.height / 2 - (r.top + r.height / 2);
    this.#applyView();
  }

  #applyView() {
    const { x, y, k } = this.view;
    this.stage.style.transform = `translate(${x}px, ${y}px) scale(${k})`;

    // The background grid moves and scales with the map.
    let cell = 32 * k;
    while (cell < 18) cell *= 2;
    this.host.style.setProperty('--gs', cell + 'px');
    this.host.style.setProperty('--gx', x + 'px');
    this.host.style.setProperty('--gy', y + 'px');
  }

  // ---------------------------------------------------------------- input

  #bind() {
    const host = this.host;
    const nodeOf = (target) => target.closest?.('.n, .gh')?.closest('[data-id]');
    const isChrome = (target) => Boolean(target.closest?.('.canvas-tools, .zoom, .legend'));

    host.addEventListener('wheel', (e) => {
      if (isChrome(e.target)) return;
      e.preventDefault();
      // Mouse wheels send big whole steps; trackpads send small ones and use ctrl for pinch.
      const wheel = e.deltaMode !== 0 || (e.deltaX === 0 && Math.abs(e.deltaY) >= 50);
      if (e.ctrlKey || wheel) {
        this.zoomBy(Math.exp(-e.deltaY * (e.ctrlKey ? 0.012 : 0.0016)), e.clientX, e.clientY);
      } else {
        this.view.x -= e.deltaX;
        this.view.y -= e.deltaY;
        this.#applyView();
      }
    }, { passive: false });

    let drag = null;
    host.addEventListener('pointerdown', (e) => {
      if (e.button !== 0 || isChrome(e.target) || nodeOf(e.target) || e.target.closest?.('path.hit')) return;
      drag = { x: e.clientX, y: e.clientY, vx: this.view.x, vy: this.view.y, moved: false };
      host.setPointerCapture(e.pointerId);
    });
    host.addEventListener('pointermove', (e) => {
      if (!drag) return;
      const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      if (!drag.moved && Math.hypot(dx, dy) < 4) return;
      drag.moved = true;
      host.classList.add('panning');
      this.view.x = drag.vx + dx;
      this.view.y = drag.vy + dy;
      this.#applyView();
    });
    const end = (e) => {
      if (!drag) return;
      host.classList.remove('panning');
      if (host.hasPointerCapture(e.pointerId)) host.releasePointerCapture(e.pointerId);
      if (!drag.moved && e.type === 'pointerup') this.handlers.select?.(null);    // click on empty map clears the selection
      drag = null;
    };
    host.addEventListener('pointerup', end);
    host.addEventListener('pointercancel', end);

    host.addEventListener('click', (e) => {
      const edge = e.target.closest?.('path.hit');
      if (edge) return this.handlers.edge?.(edge.dataset.edge);
      const el = nodeOf(e.target);
      if (el) this.handlers.select?.(el.dataset.id);
    });

    host.addEventListener('dblclick', (e) => {
      const el = nodeOf(e.target);
      if (el) this.handlers.open?.(el.dataset.id);
    });

    host.addEventListener('keydown', (e) => {
      const el = e.target.closest?.('.n');
      if (!el || (e.key !== 'Enter' && e.key !== ' ')) return;
      e.preventDefault();
      if (e.shiftKey) this.handlers.open?.(el.dataset.id);
      else this.handlers.select?.(el.dataset.id);
    });

    host.addEventListener('mouseover', (e) => {
      const el = e.target.closest?.('.n');
      if (el && el.dataset.id !== this.hovered) this.#hover(el.dataset.id);
    });
    host.addEventListener('mouseout', (e) => {
      const to = e.relatedTarget;
      // Moving onto one of the node's own lines keeps it active, so the line can be clicked.
      if (e.target.closest?.('.n, svg.links') && !to?.closest?.('.n, svg.links')) this.#hover(null);
    });
  }
}

// -------------------------------------------------------------------- helpers

function div(cls) {
  const el = document.createElement('div');
  el.className = cls;
  return el;
}

function svg(tag, attrs = {}) {
  const el = document.createElementNS(SVG_NS, tag);
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  return el;
}

function push(map, key, value) {
  const list = map.get(key);
  if (list) list.push(value);
  else map.set(key, [value]);
}

/** Static arrow heads; no data is interpolated here. */
function markerDefs() {
  const solid = (id) =>
    `<marker id="m-${id}" markerWidth="10" markerHeight="10" refX="8.5" refY="5" orient="auto" markerUnits="userSpaceOnUse"><path class="m-${id}" d="M1,1.2 L9,5 L1,8.8 z"/></marker>`;
  return `<defs>${['ink2', 'ink', 'ink3', 'signal', 'send', 'data', 'net'].map(solid).join('')}` +
    '<marker id="m-hollow" markerWidth="14" markerHeight="13" refX="12.5" refY="6.5" orient="auto" markerUnits="userSpaceOnUse"><path class="m-hollow" d="M1.5,1.5 L12.5,6.5 L1.5,11.5 z"/></marker></defs>';
}

/** Wide invisible twin of a line, so it is easy to hover and click. */
function hitPath(d, edge) {
  const hit = svg('path', { class: 'hit', d });
  hit.dataset.edge = edge.id;
  if (edge.title) {
    const title = svg('title');
    title.textContent = edge.title;
    hit.append(title);
  }
  return hit;
}

/** Lays nodes out like a multi-column list: read down each column, each column as wide as its widest item. */
function packGrid(nodes, sizes, aspect) {
  const gap = 10;
  const count = nodes.length;
  const cellH = Math.max(...nodes.map((n) => sizes.get(n.id).h));
  const meanW = nodes.reduce((sum, n) => sum + sizes.get(n.id).w, 0) / count;
  const ideal = Math.round(Math.sqrt((count * (cellH + gap) * aspect) / (meanW + gap)));
  const rows = Math.ceil(count / Math.max(1, Math.min(count, ideal)));
  const cols = Math.ceil(count / rows);

  const cells = [];
  let x = 0;
  for (let c = 0; c < cols; c++) {
    const column = nodes.slice(c * rows, (c + 1) * rows);
    const w = Math.max(...column.map((n) => sizes.get(n.id).w));
    column.forEach((n, r) => cells.push({ id: n.id, x, y: r * (cellH + gap), w, h: sizes.get(n.id).h }));
    x += w + gap;
  }

  return { w: x - gap, h: rows * cellH + (rows - 1) * gap, cells };
}

/** Places boxes left to right in rows, aiming for the given width-to-height ratio. */
function packShelves(blocks, gap, aspect) {
  const area = blocks.reduce((sum, b) => sum + (b.w + gap) * (b.h + gap), 0);
  const limit = Math.max(Math.sqrt(area * aspect), ...blocks.map((b) => b.w));
  const at = [];
  let x = 0, y = 0, rowH = 0, w = 0;

  for (const b of blocks) {
    if (x > 0 && x + b.w > limit) {
      x = 0;
      y += rowH + gap;
      rowH = 0;
    }
    at.push({ x, y });
    x += b.w + gap;
    rowH = Math.max(rowH, b.h);
    w = Math.max(w, x - gap);
  }
  return { w, h: y + rowH, at };
}

/** Polyline with rounded corners, like a transit line. */
function roundedPath(points, radius) {
  let d = `M${points[0].x},${points[0].y}`;
  for (let i = 1; i < points.length - 1; i++) {
    const a = points[i - 1], b = points[i], c = points[i + 1];
    const ab = Math.hypot(b.x - a.x, b.y - a.y), bc = Math.hypot(c.x - b.x, c.y - b.y);
    if (!ab || !bc) continue;
    const r = Math.min(radius, ab / 2, bc / 2);
    const p = { x: b.x + ((a.x - b.x) * r) / ab, y: b.y + ((a.y - b.y) * r) / ab };
    const q = { x: b.x + ((c.x - b.x) * r) / bc, y: b.y + ((c.y - b.y) * r) / bc };
    d += ` L${p.x},${p.y} Q${b.x},${b.y} ${q.x},${q.y}`;
  }
  const last = points[points.length - 1];
  return `${d} L${last.x},${last.y}`;
}

/** Smooth curve from box a to box b, leaving and entering at the sides that face each other. */
function curveBetween(a, b) {
  const ay = a.y + a.h / 2, by = b.y + b.h / 2;
  const ax = a.x + a.w / 2, bx = b.x + b.w / 2;

  if (a.x + a.w <= b.x || b.x + b.w <= a.x) {        // side by side: go out sideways
    const right = a.x + a.w <= b.x;
    const p = { x: right ? a.x + a.w : a.x, y: ay };
    const q = { x: right ? b.x : b.x + b.w, y: by };
    const k = Math.max(30, Math.abs(q.x - p.x) / 2) * (right ? 1 : -1);
    return `M${p.x},${p.y} C${p.x + k},${p.y} ${q.x - k},${q.y} ${q.x},${q.y}`;
  }

  const down = ay <= by;                              // stacked: go out the top or bottom
  const p = { x: ax, y: down ? a.y + a.h : a.y };
  const q = { x: bx, y: down ? b.y : b.y + b.h };
  const k = Math.max(24, Math.abs(q.y - p.y) / 2) * (down ? 1 : -1);
  return `M${p.x},${p.y} C${p.x},${p.y + k} ${q.x},${q.y - k} ${q.x},${q.y}`;
}

function midOfLongest(points) {
  let best = 0, at = points[0];
  for (let i = 1; i < points.length; i++) {
    const len = Math.hypot(points[i].x - points[i - 1].x, points[i].y - points[i - 1].y);
    if (len > best) {
      best = len;
      at = { x: (points[i].x + points[i - 1].x) / 2, y: (points[i].y + points[i - 1].y) / 2 };
    }
  }
  return at;
}

function pointAlong(points, distance) {
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1], b = points[i];
    const len = Math.hypot(b.x - a.x, b.y - a.y);
    if (distance <= len && len > 0) return { x: a.x + ((b.x - a.x) * distance) / len, y: a.y + ((b.y - a.y) * distance) / len };
    distance -= len;
  }
  return points[points.length - 1];
}

/** Small label on a line. Text goes in through textContent, never as markup. */
function plate(at, text, cls) {
  const g = svg('g', { class: cls, transform: `translate(${at.x},${at.y})` });
  const rect = svg('rect', { y: -9, height: 18 });
  const label = svg('text');
  label.textContent = text;
  g.append(rect, label);
  return g;
}
