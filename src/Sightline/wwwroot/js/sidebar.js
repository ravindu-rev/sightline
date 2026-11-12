// Left panel: lists of endpoints, DI services, types and projects, with a filter.
// Rows are built as markup; every value from the analyzed code goes through esc().

import { esc, typeCode, verbChip, plural } from './ui.js';
import { byName } from './store.js';

const TABS = [['endpoints', 'Endpoints'], ['services', 'Services'], ['types', 'Types'], ['projects', 'Projects']];
const MAX_ROWS = 600;

export class Sidebar {
  constructor(root, store, onPick) {
    this.store = store;
    this.onPick = onPick;
    this.tab = 'endpoints';
    this.query = '';
    this.current = null;

    this.tabsEl = root.querySelector('.tabs');
    this.filterEl = root.querySelector('.filter');
    this.listEl = root.querySelector('.list');

    this.tabsEl.addEventListener('click', (e) => {
      const button = e.target.closest('button[data-tab]');
      if (!button) return;
      this.tab = button.dataset.tab;
      this.render();
    });
    this.filterEl.addEventListener('input', () => {
      this.query = this.filterEl.value.trim().toLowerCase();
      this.#renderList();
    });
    this.listEl.addEventListener('click', (e) => {
      const row = e.target.closest('[data-id]');
      if (row?.dataset.id) this.onPick(row.dataset.id, this.tab);
    });
  }

  setCurrent(id) {
    this.current = id;
    for (const row of this.listEl.querySelectorAll('[data-id]'))
      row.setAttribute('aria-current', String(row.dataset.id === id));
  }

  render() {
    const s = this.store;
    const counts = {
      endpoints: s.endpoints().length, services: s.services.length,
      types: s.types().length, projects: s.projects.length,
    };
    this.tabsEl.innerHTML = TABS.map(([id, label]) =>
      `<button role="tab" data-tab="${id}" aria-selected="${id === this.tab}"><b>${counts[id]}</b>${label}</button>`).join('');
    this.filterEl.placeholder = `Filter ${this.tab}`;
    this.#renderList();
  }

  #renderList() {
    const rows = this[`_${this.tab}`]();
    const shown = rows.slice(0, MAX_ROWS);
    let html = shown.join('');
    if (!rows.length) html = `<p class="none">${this.query ? 'Nothing matches this filter.' : EMPTY[this.tab]}</p>`;
    else if (rows.length > MAX_ROWS) html += `<p class="none">${rows.length - MAX_ROWS} more. Type in the filter to narrow the list.</p>`;
    this.listEl.innerHTML = html;
    this.setCurrent(this.current);
  }

  #match(...texts) {
    return !this.query || texts.some((t) => String(t ?? '').toLowerCase().includes(this.query));
  }

  /** Rows grouped under a coloured project heading. */
  #grouped(items, rowOf) {
    const s = this.store;
    const out = [];
    const byProject = new Map();
    for (const item of items) {
      const list = byProject.get(item.project);
      if (list) list.push(item);
      else byProject.set(item.project, [item]);
    }
    for (const p of s.projects) {
      const list = byProject.get(p.id);
      if (!list?.length) continue;
      if (byProject.size > 1) out.push(`<h3 class="${s.zoneClass(p.id)}">${esc(p.name)}</h3>`);
      for (const item of list) out.push(rowOf(item));
    }
    return out;
  }

  _endpoints() {
    const items = this.store.endpoints().filter((ep) => this.#match(ep.name, ep.meta.style));
    return this.#grouped(items, (ep) =>
      `<button class="row" data-id="${esc(ep.id)}" title="${esc(ep.name)}">${verbChip(ep.meta.verb)}` +
      `<span class="route grow">${esc(ep.meta.route)}</span>${ep.meta.auth ? '<span class="chip lock">auth</span>' : ''}</button>`);
  }

  _services() {
    return this.store.services
      .filter((x) => this.#match(x.service, x.impl, x.lifetime))
      .sort((a, b) => a.service.localeCompare(b.service))
      .map((x) => {
        const id = x.implId ?? x.serviceId ?? '';
        const same = !x.impl || x.impl === x.service;
        return `<button class="row" data-id="${esc(id)}" title="${esc(x.method)}"${id ? '' : ' disabled'}>` +
          `<span class="grow">${esc(x.service)}${same ? '' : ` <span class="dim">${esc('→ ' + x.impl)}</span>`}</span>` +
          `<span class="chip life">${esc(x.lifetime)}</span></button>`;
      });
  }

  _types() {
    const items = this.store.types().filter((t) => this.#match(t.name, t.ns)).sort(byName);
    return this.#grouped(items, (t) =>
      `<button class="row ${this.store.zoneClass(t.id)}" data-id="${esc(t.id)}" title="${esc((t.ns ? t.ns + '.' : '') + t.name)}">` +
      `${typeCode(t.meta.typeKind)}<span class="grow">${esc(t.name)}</span><span class="dim">${esc(lastPart(t.ns))}</span></button>`);
  }

  _projects() {
    const s = this.store;
    const out = [];
    for (const p of s.projects) {
      const packages = s.outOf(p.id).filter((e) => e.kind === 'packageRef');
      const hits = packages.filter((e) => this.#match(s.node(e.target)?.name));
      if (!this.#match(p.name) && !hits.length) continue;

      out.push(`<h3 class="${s.zoneClass(p.id)}">${esc(p.name)}</h3>`);
      out.push(`<button class="row" data-id="${esc(p.id)}"><span class="chip">${esc(p.meta.projectKind)}</span>` +
        `<span class="grow dim">${esc(p.meta.tfm ?? '')}</span><span class="dim">${esc(plural(s.childrenOf(p.id, 'type').length, 'type'))}</span></button>`);
      for (const e of this.query ? hits : packages) {
        const k = s.node(e.target);
        if (k) out.push(`<button class="row" data-id="${esc(k.id)}"><span class="grow">${esc(k.name)}</span><span class="dim">${esc(e.label ?? '')}</span></button>`);
      }
    }
    return out;
  }
}

const EMPTY = {
  endpoints: 'No HTTP endpoints found in this solution.',
  services: 'No dependency injection registrations found.',
  types: 'No types found.',
  projects: 'No C# projects loaded.',
};

const lastPart = (ns) => String(ns ?? '').split('.').pop();
