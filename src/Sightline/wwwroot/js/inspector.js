// Right panel: details of the selected item, and its source code in an editor.
// Typing in the editor sends the unsaved text to the server, so the map updates before saving.
// Markup is built from strings; every value from the analyzed code goes through esc().

import { esc, icon, typeCode, verbChip, tagLabel, fileName, plural } from './ui.js';
import { api } from './api.js';
import { KIND_GROUP, describeKinds } from './models.js';

const STYLE_WORDS = {
  minimal: 'Minimal API endpoint', controller: 'Controller action', hub: 'SignalR hub',
  grpc: 'gRPC service', health: 'Health check', fallback: 'Fallback route',
};
const MEMBER_MARK = { method: 'M', ctor: 'N', property: 'P', field: 'F', event: 'E' };
const SHORT = {
  injects: 'injects', calls: 'calls', dispatches: 'sends', handles: 'handles', db: 'queries', http: 'HTTP',
  inherits: 'inherits', implements: 'implements', creates: 'creates', references: 'uses',
  projectRef: 'references project', packageRef: 'references package', registers: 'resolves to',
};
const CALL_OUT = new Set(['calls', 'dispatches', 'db', 'http', 'creates', 'handles']);
const CALL_IN = new Set(['calls', 'dispatches', 'handles']);
const MAX_LINKS = 40;

export class Inspector {
  constructor(root, store, actions) {
    this.store = store;
    this.actions = actions;
    this.sel = null;
    this.file = null;
    this.eol = '\n';
    this.dirty = false;
    this.ticket = 0;
    this.marked = [];

    this.detail = root.querySelector('.detail');
    this.fileEl = root.querySelector('#ed-file');
    this.unsavedEl = root.querySelector('#ed-unsaved');
    this.saveBtn = root.querySelector('#ed-save');
    this.discardBtn = root.querySelector('#ed-discard');
    this.openLink = root.querySelector('#ed-open');
    this.emptyEl = root.querySelector('#ed-empty');

    this.cm = CodeMirror(root.querySelector('#ed-body'), {
      mode: 'text/x-csharp', lineNumbers: true, indentUnit: 4, tabSize: 4, indentWithTabs: false,
      matchBrackets: true, autoCloseBrackets: true, styleActiveLine: true,
      extraKeys: { 'Ctrl-S': () => this.save(), 'Cmd-S': () => this.save() },
    });
    this.cm.getWrapperElement().hidden = true;
    this.cm.on('change', (_, change) => {
      if (change.origin !== 'setValue') this.#edited();
    });
    new ResizeObserver(() => this.cm.refresh()).observe(root.querySelector('#ed-body'));

    this.saveBtn.addEventListener('click', () => this.save());
    this.discardBtn.addEventListener('click', () => this.discard());

    this.detail.addEventListener('click', (e) => {
      const go = e.target.closest('[data-go]');
      if (go) return this.actions.select(go.dataset.go);

      const site = e.target.closest('[data-file]');
      if (site) return this.openFile(site.dataset.file, Number(site.dataset.line), Number(site.dataset.line));

      const act = e.target.closest('[data-act]');
      if (act) this.actions[act.dataset.act]?.(act.dataset.id);
    });
  }

  // ---------------------------------------------------------------- selection

  /** sel is { node: id }, { edge: viewEdge }, { notes: [...] } or null. */
  show(sel) {
    this.sel = sel;
    this.#renderDetail(false);
    const n = sel?.node ? this.store.node(sel.node) : null;
    if (n?.file && n.kind !== 'project') this.openFile(n.file, n.line, n.endLine);
  }

  /** Called after every live update. */
  refresh(change) {
    if (this.sel?.node && !this.store.node(this.sel.node)) return this.actions.select(null);

    this.#renderDetail(true);
    const n = this.sel?.node ? this.store.node(this.sel.node) : null;
    const span = n?.file === this.file ? n : null;
    const changedOnDisk = this.file && !this.dirty && (change.files ?? []).some((f) => this.#isCurrentFile(f));

    if (changedOnDisk) this.openFile(this.file, span?.line, span?.endLine, { force: true, keepScroll: true });
    else if (span) this.#markSpan(span.line, span.endLine, false);
  }

  #isCurrentFile(relative) {
    return this.file.replace(/\\/g, '/').toLowerCase().endsWith('/' + relative.toLowerCase());
  }

  // ---------------------------------------------------------------- editor

  async openFile(file, line, endLine, { force = false, keepScroll = false } = {}) {
    if (file !== this.file || force) {
      const ticket = ++this.ticket;
      let source;
      try {
        source = await api.source(file);
      } catch {
        if (ticket === this.ticket) this.#noFile(`${fileName(file)} is not part of the analyzed source files.`);
        return;
      }
      if (ticket !== this.ticket) return;          // a newer selection won

      const scroll = file === this.file ? this.cm.getScrollInfo() : null;
      const cursor = file === this.file ? this.cm.getCursor() : null;
      const sameFile = file === this.file;

      this.file = file;
      this.eol = source.text.includes('\r\n') ? '\r\n' : '\n';   // keep the file's line endings on save
      this.dirty = source.dirty;
      this.cm.setValue(source.text);
      if (!sameFile) this.cm.clearHistory();
      if (scroll) this.cm.scrollTo(scroll.left, scroll.top);
      if (cursor) this.cm.setCursor(cursor);

      this.emptyEl.hidden = true;
      this.cm.getWrapperElement().hidden = false;
      this.cm.refresh();
      this.#updateBar(line);
    }
    this.#markSpan(line, endLine, !keepScroll);
    this.#updateBar(line);
  }

  #noFile(message) {
    this.file = null;
    this.dirty = false;
    this.cm.getWrapperElement().hidden = true;
    this.emptyEl.hidden = false;
    this.emptyEl.textContent = message;
    this.#updateBar();
  }

  /** Tints the lines of the selected class or method and scrolls them into view. */
  #markSpan(line, endLine, scroll) {
    for (const handle of this.marked) {
      this.cm.removeLineClass(handle, 'background', 'span-line');
      this.cm.removeLineClass(handle, 'background', 'span-first');
    }
    this.marked = [];
    if (!line) return;

    const first = line - 1;
    const last = Math.min((endLine ?? line) - 1, first + 400, this.cm.lineCount() - 1);
    for (let i = first; i <= last; i++) {
      const handle = this.cm.addLineClass(i, 'background', 'span-line');
      if (i === first) this.cm.addLineClass(i, 'background', 'span-first');
      if (handle) this.marked.push(handle);
    }
    if (scroll) this.cm.scrollIntoView({ from: { line: first, ch: 0 }, to: { line: Math.min(last, first + 24), ch: 0 }, }, 50);
  }

  #updateBar(line) {
    this.fileEl.textContent = this.file ? fileName(this.file) : 'No file open';
    this.fileEl.title = this.file ?? '';
    this.unsavedEl.hidden = !this.dirty;
    this.saveBtn.disabled = !this.dirty;
    this.discardBtn.disabled = !this.dirty;
    this.openLink.hidden = !this.file;
    if (this.file) this.openLink.href = editorLink(this.file, line ?? 1);
  }

  #edited() {
    this.dirty = true;
    this.#updateBar();
    clearTimeout(this.typing);
    this.typing = setTimeout(() => {
      if (this.file && this.dirty) api.overlay(this.file, this.cm.getValue(this.eol)).catch(() => {});
    }, 220);
  }

  async save() {
    if (!this.file || !this.dirty) return;
    clearTimeout(this.typing);
    try {
      await api.save(this.file, this.cm.getValue(this.eol));
      this.dirty = false;
      this.#updateBar();
    } catch (err) {
      this.unsavedEl.textContent = 'Could not save: ' + err.message;
    }
  }

  async discard() {
    if (!this.file) return;
    clearTimeout(this.typing);
    await api.discard(this.file).catch(() => {});
    const n = this.sel?.node ? this.store.node(this.sel.node) : null;
    this.unsavedEl.textContent = 'Unsaved';
    await this.openFile(this.file, n?.file === this.file ? n.line : null, n?.endLine, { force: true, keepScroll: true });
  }

  // ---------------------------------------------------------------- detail

  #renderDetail(keepScroll) {
    const top = keepScroll ? this.detail.scrollTop : 0;
    const s = this.sel;
    const n = s?.node ? this.store.node(s.node) : null;

    let html;
    if (n) html = this[`_${n.kind}`]?.(n) ?? this.#sign(n, esc(n.name), '');
    else if (s?.edge) html = this.#edge(s.edge);
    else if (s?.notes) html = this.#notes(s.notes);
    else html = '<div class="hint"><b>Nothing selected</b>Pick an endpoint, service or class on the left, or click anything on the map. ' +
      'Its details show here and its code opens below, ready to edit.</div>';

    this.detail.innerHTML = html;
    this.detail.scrollTop = top;
  }

  #sign(n, title, sub) {
    return `<div class="sign ${this.store.zoneClass(n.id)}"><div><h2>${title}</h2>${sub ? `<p>${sub}</p>` : ''}</div></div>`;
  }

  #link(id, note = '') {
    const s = this.store;
    const n = s.node(id);
    if (!n) return '';
    let inner;
    if (n.kind === 'type') inner = typeCode(n.meta.typeKind) + `<span class="grow">${esc(n.name)}</span>`;
    else if (n.kind === 'endpoint') inner = verbChip(n.meta.verb) + `<span class="grow route">${esc(n.meta.route)}</span>`;
    else inner = `<span class="grow">${esc(s.label(id))}</span>`;
    return `<button class="link ${s.zoneClass(id)}" data-go="${esc(id)}">${inner}${note ? `<em>${esc(note)}</em>` : ''}</button>`;
  }

  #section(title, links, hint = '') {
    if (!links.length) return '';
    const shown = links.slice(0, MAX_LINKS).join('');
    const rest = links.length > MAX_LINKS ? `<span class="chip">${links.length - MAX_LINKS} more</span>` : '';
    return `<h4>${esc(title)}${hint ? `<small>${esc(hint)}</small>` : ''}</h4><div class="chips">${shown}${rest}</div>`;
  }

  #actions(...items) {
    return `<div class="acts">${items.filter(Boolean).join('')}</div>`;
  }

  #act(name, id, iconName, label) {
    return `<button class="tool" data-act="${name}" data-id="${esc(id)}">${icon(iconName)}${esc(label)}</button>`;
  }

  /** Other types this one touches, both ways, with a short note on how. */
  #neighbours(typeId) {
    const s = this.store;
    const own = [typeId, ...s.childrenOf(typeId, 'member').map((m) => m.id)];
    const rep = (id) => {
      const n = s.node(id);
      return n?.kind === 'member' ? n.parent : n?.id;
    };
    const out = new Map(), into = new Map();
    const bump = (map, id, kind, count) => {
      if (!id || id === typeId) return;
      if (!map.has(id)) map.set(id, new Map());
      map.get(id).set(kind, (map.get(id).get(kind) ?? 0) + (count ?? 1));
    };

    for (const id of own) {
      for (const e of s.outOf(id)) if (e.kind in KIND_GROUP && e.kind !== 'registers') bump(out, rep(e.target), e.kind, e.count);
      for (const e of s.intoOf(id)) if (e.kind in KIND_GROUP && e.kind !== 'registers') bump(into, rep(e.source), e.kind, e.count);
    }

    const note = (kinds) => [...kinds].map(([k, c]) => (c > 1 ? `${c} ${SHORT[k] ?? k}` : SHORT[k] ?? k)).join(', ');
    const links = (map) => [...map].sort((a, b) => s.label(a[0]).localeCompare(s.label(b[0]))).map(([id, kinds]) => this.#link(id, note(kinds)));
    return { out: links(out), into: links(into) };
  }

  _type(n) {
    const s = this.store;
    const m = n.meta;
    const where = [m.typeKind, n.ns ? 'in ' + n.ns : ''].filter(Boolean).join(' ');
    const chips = [
      ...(m.tags ?? []).map((t) => `<span class="chip">${esc(tagLabel(t))}</span>`),
      m.lifetime ? `<span class="chip life">${esc(m.lifetime)}${m.registeredAs ? ' as ' + esc(m.registeredAs.join(', ')) : ''}</span>` : '',
      m.base ? `<span class="chip">extends ${esc(m.base)}</span>` : '',
      ...(m.externalInterfaces ?? []).map((i) => `<span class="chip">${esc(i)}</span>`),
    ].join('');

    const { out, into } = this.#neighbours(n.id);
    const members = s.childrenOf(n.id, 'member').sort((a, b) => (a.line ?? 0) - (b.line ?? 0));
    const rows = members.map((x) =>
      `<button class="row" data-go="${esc(x.id)}"><span class="mk">${MEMBER_MARK[x.meta.memberKind] ?? ''}</span>` +
      `<code class="grow">${esc(x.meta.sig)}</code><span class="dim">${esc(x.meta.returns ?? '')}</span></button>`).join('');

    return this.#sign(n, esc(n.name), esc(where)) +
      (chips ? `<div class="chips top">${chips}</div>` : '') +
      this.#actions(this.#act('focus', n.id, 'focus', 'Focus on this type')) +
      this.#section('Depends on', out) +
      this.#section('Used by', into) +
      (rows ? `<h4>Members<small>${members.length}</small></h4><div class="rows">${rows}</div>` : '');
  }

  _member(n) {
    const s = this.store;
    const m = n.meta;
    const owner = s.node(n.parent);
    const flags = [m.access, m.static ? 'static' : '', m.abstract ? 'abstract' : '', m.async ? 'async' : '']
      .filter(Boolean).map((f) => `<span class="chip">${esc(f)}</span>`).join('');

    const calls = s.outOf(n.id, CALL_OUT).slice().sort((a, b) => (a.line ?? 0) - (b.line ?? 0))
      .map((e) => this.#link(e.target, [SHORT[e.kind], e.label].filter(Boolean).join(' ')));
    const callers = s.intoOf(n.id, CALL_IN).map((e) => this.#link(e.source, SHORT[e.kind]));
    for (const a of s.abstractions(n.id))
      for (const e of s.intoOf(a.id, CALL_IN)) callers.push(this.#link(e.source, 'via ' + (s.node(a.parent)?.name ?? '')));

    const callable = m.memberKind === 'method' || m.memberKind === 'ctor';
    return this.#sign(n, esc(`${owner?.name ?? ''}.${n.name}`), esc(`${m.memberKind} in ${owner?.ns ?? ''}`)) +
      `<pre class="sig">${esc(m.sig)}${m.returns ? esc(' : ' + m.returns) : ''}</pre>` +
      `<div class="chips top">${flags}</div>` +
      this.#actions(
        callable && this.#act('flow', n.id, 'flow', 'Trace what it calls'),
        callable && this.#act('callers', n.id, 'callers', 'Trace who calls it'),
        owner && this.#act('focus', owner.id, 'focus', 'Focus on ' + owner.name)) +
      this.#section('Implements', s.abstractions(n.id).map((a) => this.#link(a.id))) +
      this.#section('Implemented by', s.implementations(n.id).map((i) => this.#link(i.id))) +
      this.#section('Calls', calls, 'in source order') +
      this.#section('Called by', callers);
  }

  _endpoint(n) {
    const s = this.store;
    const m = n.meta;
    const uses = s.outOf(n.id).filter((e) => e.kind === 'injects' || e.kind === 'references').map((e) => this.#link(e.target, SHORT[e.kind]));
    const calls = s.outOf(n.id, CALL_OUT).filter((e) => e.kind !== 'handles').sort((a, b) => (a.line ?? 0) - (b.line ?? 0))
      .map((e) => this.#link(e.target, [SHORT[e.kind], e.label].filter(Boolean).join(' ')));

    return `<div class="sign ${s.zoneClass(n.id)}">${verbChip(m.verb)}<div><h2 class="mono">${esc(m.route)}</h2>` +
      `<p>${esc(STYLE_WORDS[m.style] ?? m.style)} in ${esc(s.node(n.project)?.name ?? '')}</p></div></div>` +
      `<div class="chips top">${m.auth ? '<span class="chip lock">requires authorization</span>' : ''}` +
      `${m.routeName ? `<span class="chip">name ${esc(m.routeName)}</span>` : ''}</div>` +
      this.#actions(this.#act('flow', n.id, 'flow', 'Trace this request')) +
      this.#section('Handled by', [m.handler, m.target].filter(Boolean).map((id) => this.#link(id))) +
      this.#section('Mapped in', m.declaredIn && m.declaredIn !== m.handler ? [this.#link(m.declaredIn)] : []) +
      this.#section('Receives', uses) +
      this.#section('Calls', calls, 'in source order');
  }

  _project(n) {
    const s = this.store;
    const m = n.meta;
    const refs = s.outOf(n.id).filter((e) => e.kind === 'projectRef').map((e) => this.#link(e.target));
    const usedBy = s.intoOf(n.id).filter((e) => e.kind === 'projectRef').map((e) => this.#link(e.source));
    const packages = s.outOf(n.id).filter((e) => e.kind === 'packageRef').map((e) => this.#link(e.target, e.label ?? ''));
    const types = s.childrenOf(n.id, 'type').length;
    const endpoints = s.childrenOf(n.id, 'endpoint').length;

    return this.#sign(n, esc(n.name), esc([m.projectKind, m.tfm].filter(Boolean).join(', '))) +
      `<dl class="kv"><dt>Contains</dt><dd>${esc(plural(types, 'type'))}${endpoints ? ', ' + esc(plural(endpoints, 'endpoint')) : ''}</dd>` +
      `<dt>File</dt><dd class="mono">${esc(n.file ?? '')}</dd></dl>` +
      this.#actions(this.#act('toggle', n.id, 'expand', 'Open or close on the map')) +
      this.#section('References', refs) +
      this.#section('Referenced by', usedBy) +
      this.#section('Packages', packages, m.transitive ? `plus ${m.transitive} pulled in by these` : '');
  }

  _package(n) {
    const s = this.store;
    const usedBy = s.intoOf(n.id).filter((e) => e.kind === 'packageRef').map((e) => this.#link(e.source, e.label ?? ''));
    return this.#sign(n, esc(n.name), esc('NuGet package ' + (n.meta.versions ?? []).join(', '))) + this.#section('Used by', usedBy);
  }

  _external(n) {
    const callers = this.store.intoOf(n.id).map((e) => this.#link(e.source, e.label ?? ''));
    return this.#sign(n, esc(n.name), n.meta.externalKind === 'db' ? 'Database access through a context outside this solution' : 'Calls made with HttpClient') +
      this.#section('Used by', callers);
  }

  /** A line on the map: what it stands for and where in the code it comes from. */
  #edge(edge) {
    const s = this.store;
    const labelOf = (viewId, ref) => (ref && s.node(ref) ? s.label(ref) : String(viewId).replace(/^ns:[^:]*:[^:]*:/, ''));
    const sites = (edge.refs ?? []).map((id) => s.edges.get(id)).filter(Boolean);
    const rows = sites.slice(0, 60).map((e) =>
      `<button class="row"${e.file ? ` data-file="${esc(e.file)}" data-line="${e.line ?? 1}"` : ''}>` +
      `<span class="grow">${esc(s.label(e.source))} <span class="dim">${esc(SHORT[e.kind] ?? e.kind)}</span> ${esc(s.label(e.target))}</span>` +
      `<span class="dim">${e.file ? esc(fileName(e.file) + ':' + (e.line ?? '')) : ''}</span></button>`).join('');

    const kinds = edge.kinds ? describeKinds(edge.kinds) : edge.title ?? edge.kind;
    return `<div class="sign"><div><h2>${esc(labelOf(edge.source, edge.sourceRef))} ${esc('→')} ${esc(labelOf(edge.target, edge.targetRef))}</h2>` +
      `<p>${esc(kinds)}</p></div></div>` +
      (rows ? `<h4>Where in the code<small>${sites.length}</small></h4><div class="rows">${rows}</div>` : '');
  }

  #notes(notes) {
    return '<div class="sign"><div><h2>Load warnings</h2><p>MSBuild reported these while opening the solution. Missing packages usually go away after dotnet restore.</p></div></div>' +
      this.#actions(this.#act('reload', '', 'reload', 'Load the solution again')) +
      `<div class="rows">${notes.map((x) => `<pre class="sig">${esc(x)}</pre>`).join('')}</div>`;
  }
}

/** vscode:// link that opens a file at a line. */
function editorLink(file, line) {
  return 'vscode://file/' + encodeURI(file.replace(/\\/g, '/')) + ':' + line;
}
