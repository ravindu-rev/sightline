// The running app, as the map sees it: apps that report to Sightline (Co.Revington.Sightline.Hosting),
// the requests they handle, and "Try it" on an endpoint. Requests go through the Sightline server,
// because a page may not read answers from another port. Everything shown from the app goes through
// esc() or textContent.

import { api } from './api.js';
import { esc, h, icon, plural, toast, verbChip } from './ui.js';

const BODYLESS = new Set(['GET', 'HEAD', 'DELETE', 'OPTIONS']);
const MAX_TRACES = 300;

export class Live {
  /** actions: { select(id), traceFlow(endpointId, trace), show(sel), refreshDetail() } */
  constructor(store, actions) {
    this.store = store;
    this.actions = actions;
    this.apps = [];
    this.traces = [];
    this.tries = new Map();          // endpoint id -> { state, el }
    this.waiting = new Map();        // trace id -> endpoint id, for answers sent from "Try it"
    this.enabled = api.mode === 'live';
  }

  async start() {
    if (!this.enabled) return;
    try {
      [this.apps, this.traces] = await Promise.all([api.apps(), api.traces()]);
    } catch {
      // an older server, or none; the panel still works with an address typed by hand
    }
    this.renderButton();
  }

  // ---------------------------------------------------------------- events from the server

  onRuntime(apps) {
    const restarted = apps.some((a) => !this.apps.some((b) => b.pid === a.pid));
    this.apps = apps;
    this.renderButton();
    for (const [id, entry] of this.tries) {
      if (!entry.state.base) entry.state.base = this.#defaultBase(this.store.node(id));
      this.#paint(id);
      // An app that restarted after a save is ready now: send the waiting request.
      if (restarted && entry.state.rerun) this.#send(id);
    }
  }

  onTrace(trace) {
    this.traces.unshift(trace);
    this.traces.length = Math.min(this.traces.length, MAX_TRACES);
    this.renderButton();

    const endpoint = this.waiting.get(trace.id);
    if (endpoint) {
      this.waiting.delete(trace.id);
      const entry = this.tries.get(endpoint);
      if (entry) {
        entry.state.trace = trace;
        this.#paint(endpoint);
      }
    }
    this.actions.refreshList?.();
  }

  /** The code was saved: requests marked "send again" go out once the app has picked the change up. */
  onSaved() {
    for (const [id, entry] of this.tries) {
      if (!entry.state.auto || !entry.state.response) continue;
      entry.state.rerun = true;
      clearTimeout(entry.timer);
      // Hot reload is quick; a restart reports itself through onRuntime.
      entry.timer = setTimeout(() => this.#send(id), 1600);
    }
  }

  // ---------------------------------------------------------------- header button and request list

  renderButton() {
    const button = document.querySelector('#requests');
    if (!button) return;
    const running = this.apps.filter((a) => a.running);
    button.hidden = !this.enabled || (this.apps.length === 0 && this.traces.length === 0);
    button.replaceChildren();
    button.insertAdjacentHTML('afterbegin', icon('pulse'));
    button.append(this.traces.length ? plural(this.traces.length, 'request') : running.length ? 'App running' : 'App stopped');
    button.classList.toggle('live-on', running.length > 0);
    button.title = running.length
      ? `Running: ${running.map((a) => a.app).join(', ')}. Requests it handled show here.`
      : 'The app is not running. Requests it handled before are kept.';
  }

  /** The requests the apps reported, newest first. */
  listHtml() {
    const apps = this.apps.map((a) =>
      `<div class="app-row"><span class="dot ${a.running ? 'on' : ''}"></span><b>${esc(a.app)}</b>` +
      `<span class="dim">${esc((a.addresses ?? []).join(', ') || 'no address')}</span>` +
      `<span class="dim">${esc(plural(a.endpoints, 'endpoint'))}, ${esc(plural(a.services, 'registration'))}</span></div>`).join('');

    const rows = this.traces.slice(0, 150).map((t) => this.#traceRow(t)).join('');
    return `<div class="sign"><div><h2>Requests</h2><p>What the running app handled, newest first. ` +
      `Click one to see its database commands and outbound calls, and the path it took on the flow.</p></div></div>` +
      (apps ? `<h4>Apps</h4><div class="apps">${apps}</div>` : '') +
      `<h4>Recent requests<small>${this.traces.length}</small></h4>` +
      (rows ? `<div class="rows">${rows}</div>` : '<p class="dim">No requests yet. Use the app, or "Try it" on an endpoint.</p>');
  }

  #traceRow(t) {
    const status = `<span class="st st-${String(t.status)[0]}">${esc(t.status || '?')}</span>`;
    const time = new Date(t.start).toLocaleTimeString([], { hour12: false });
    const extra = [t.sql?.length ? plural(t.sql.length, 'query', 'queries') : '', t.http?.length ? plural(t.http.length, 'call') : '']
      .filter(Boolean).join(', ');
    return `<button class="row" data-act="trace" data-id="${esc(t.id)}" title="${esc(t.method + ' ' + t.path + (t.query ?? ''))}">` +
      `${verbChip(t.method)}<span class="route grow">${esc(t.path)}</span>${status}` +
      `<span class="dim">${esc(Math.round(t.ms))} ms${extra ? ', ' + esc(extra) : ''}</span><time class="dim">${esc(time)}</time></button>`;
  }

  trace(id) {
    return this.traces.find((t) => t.id === id);
  }

  /** One request in detail. */
  traceHtml(t) {
    const s = this.store;
    const ep = t.endpoint ? s.node(t.endpoint) : null;
    const sql = (t.sql ?? []).map((q, i) =>
      `<div class="q"><div class="qh"><span class="n">${i + 1}</span><span class="dim">${esc(q.ms)} ms</span>${q.error ? `<span class="bad">${esc(q.error)}</span>` : ''}</div>` +
      `<pre class="sig">${esc(q.text)}</pre></div>`).join('');
    const http = (t.http ?? []).map((c) =>
      `<div class="row">${verbChip(c.method)}<span class="grow mono">${esc(c.url)}</span>` +
      `<span class="st st-${String(c.status ?? 0)[0]}">${esc(c.status ?? 'failed')}</span><span class="dim">${esc(c.ms)} ms</span></div>`).join('');

    return `<div class="sign">${verbChip(t.method)}<div><h2 class="mono">${esc(t.path)}${esc(t.query ?? '')}</h2>` +
      `<p>${esc(t.app)}: ${esc(t.status)} in ${esc(t.ms)} ms, ${esc(new Date(t.start).toLocaleTimeString([], { hour12: false }))}</p></div></div>` +
      (t.error ? `<div class="problem pr-error"><div class="ph"><span class="sev">Error</span><b>${esc(t.error)}</b></div></div>` : '') +
      `<div class="acts">${ep ? `<button class="tool" data-act="traceFlow" data-id="${esc(t.id)}">${icon('flow')}Show the path it took</button>` : ''}` +
      `<button class="tool" data-act="requests" data-id="">${icon('back')}All requests</button></div>` +
      (ep ? `<h4>Endpoint</h4><div class="chips"><button class="link" data-go="${esc(ep.id)}">${verbChip(ep.meta.verb)}<span class="grow route">${esc(ep.meta.route)}</span></button></div>` : '') +
      (sql ? `<h4>Database<small>${esc(plural(t.sql.length, 'command'))}</small></h4>${sql}` : '') +
      (http ? `<h4>Outbound HTTP<small>${esc(plural(t.http.length, 'call'))}</small></h4><div class="rows">${http}</div>` : '') +
      (!sql && !http && !t.error ? '<p class="dim">No database commands or outbound calls were seen for this request.</p>' : '');
  }

  // ---------------------------------------------------------------- "Try it" on an endpoint

  /** Puts the request panel of an endpoint into the inspector. Its element is kept, so typed text survives redraws. */
  mount(slot, ep) {
    if (!this.enabled || !ep || ep.kind !== 'endpoint' || ['hub', 'grpc'].includes(ep.meta.style)) return;
    let entry = this.tries.get(ep.id);
    if (!entry) {
      entry = { state: this.#initial(ep), el: h('section', { class: 'try' }) };
      this.tries.set(ep.id, entry);
      this.#build(ep.id, entry);
    }
    slot.append(entry.el);
    this.#paint(ep.id);

    const recent = this.traces.filter((t) => t.endpoint === ep.id).slice(0, 5);
    if (recent.length) {
      const list = h('div', { class: 'rows' });
      list.innerHTML = recent.map((t) => this.#traceRow(t)).join('');
      slot.append(h('h4', null, 'Recent requests'), list);
    }
  }

  #initial(ep) {
    const shape = ep.meta.request ?? { params: [] };
    const unquote = (sample) => {
      try {
        const v = JSON.parse(sample);
        return typeof v === 'object' ? JSON.stringify(v) : String(v);
      } catch {
        return String(sample ?? '');
      }
    };
    const values = new Map((shape.params ?? []).map((p) => [p.name.toLowerCase(), unquote(p.sample)]));
    const path = ep.meta.route.replace(/\{\*{0,2}([^}:=?]+)[^}]*\}/g, (_, name) => encodeURIComponent(values.get(name.toLowerCase()) ?? name));
    const query = (shape.params ?? []).filter((p) => p.in === 'query').map((p) => `${encodeURIComponent(p.name)}=${encodeURIComponent(unquote(p.sample))}`).join('&');
    const headers = (shape.params ?? []).filter((p) => p.in === 'header').map((p) => `${p.name}: ${unquote(p.sample)}`);
    if (shape.bodySample) headers.unshift('Content-Type: application/json');

    const verb = String(ep.meta.verb).split(',')[0];
    return {
      method: verb === 'ANY' ? 'GET' : verb,
      base: this.#defaultBase(ep),
      target: path + (query ? '?' + query : ''),
      headers: headers.join('\n'),
      body: shape.bodySample ?? '',
      auto: false,
      response: null,
      trace: null,
      sending: false,
    };
  }

  /** The address of the app that serves this endpoint: an app with the project's name, http before https. */
  #defaultBase(ep) {
    const project = ep ? this.store.node(ep.project)?.name : null;
    const apps = [...this.apps].sort((a, b) => Number(b.running) - Number(a.running) || Number(b.app === project) - Number(a.app === project));
    const addresses = apps.flatMap((a) => a.addresses ?? []);
    return addresses.find((a) => a.startsWith('http:')) ?? addresses[0] ?? '';
  }

  #build(id, entry) {
    const st = entry.state;
    const el = entry.el;
    const methods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'];

    const method = h('select', { class: 'method', 'aria-label': 'HTTP method', onchange: (e) => { st.method = e.target.value; this.#paint(id); } },
      ...methods.map((m) => h('option', { value: m, selected: m === st.method }, m)));
    const base = h('input', { class: 'base', list: `apps-${id}`, placeholder: 'http://localhost:5000', 'aria-label': 'App address', spellcheck: 'false',
      oninput: (e) => { st.base = e.target.value.trim(); } });
    const datalist = h('datalist', { id: `apps-${id}` });
    const target = h('input', { class: 'target', 'aria-label': 'Path and query', spellcheck: 'false', oninput: (e) => { st.target = e.target.value; } });
    const send = h('button', { class: 'tool primary', onclick: () => this.#send(id) });
    send.insertAdjacentHTML('afterbegin', icon('send'));
    send.append('Send');
    target.addEventListener('keydown', (e) => { if (e.key === 'Enter') this.#send(id); });

    const headers = h('textarea', { class: 'headers', rows: 2, spellcheck: 'false', placeholder: 'Name: value, one per line', oninput: (e) => { st.headers = e.target.value; } });
    const body = h('textarea', { class: 'body', rows: 6, spellcheck: 'false', placeholder: 'Request body (JSON)', oninput: (e) => { st.body = e.target.value; } });
    const auto = h('input', { type: 'checkbox', onchange: (e) => { st.auto = e.target.checked; } });
    const result = h('div', { class: 'try-result' });
    const output = h('pre', { class: 'try-out', hidden: true });

    el.append(
      h('h4', null, 'Try it', h('small', null, 'a real request to the running app')),
      h('div', { class: 'try-line' }, method, base, datalist, send),
      target,
      h('details', { class: 'try-headers' }, h('summary', null, 'Headers'), headers),
      body,
      h('label', { class: 'try-auto' }, auto, 'Send again whenever the code is saved'),
      result, output);

    entry.parts = { method, base, datalist, target, headers, body, auto, result, output, send };
    base.value = st.base;
    target.value = st.target;
    headers.value = st.headers;
    body.value = st.body;
  }

  /** Brings the panel up to date with its state. */
  #paint(id) {
    const entry = this.tries.get(id);
    if (!entry?.parts) return;
    const st = entry.state;
    const p = entry.parts;

    p.method.value = st.method;
    if (document.activeElement !== p.base) p.base.value = st.base;
    p.datalist.replaceChildren(...this.apps.flatMap((a) => (a.addresses ?? []).map((address) => h('option', { value: address }, a.app))));
    p.body.hidden = BODYLESS.has(st.method) && !st.body;
    p.auto.checked = st.auto;
    p.send.disabled = st.sending;

    p.result.replaceChildren();
    p.output.hidden = true;
    if (st.sending) {
      p.result.append(h('span', { class: 'dim' }, 'Sending…'));
      return;
    }
    const r = st.response;
    if (!r) {
      if (!this.apps.some((a) => a.running))
        p.result.append(h('span', { class: 'dim' }, 'Run the app with Co.Revington.Sightline.Hosting and its address fills in by itself, or type it above.'));
      return;
    }
    if (r.error) {
      p.result.append(h('span', { class: 'bad' }, r.error));
      return;
    }

    p.result.append(
      h('span', { class: `st st-${String(r.status)[0]}` }, `${r.status} ${r.reason ?? ''}`.trim()),
      h('span', { class: 'dim' }, `${r.ms} ms`),
      r.truncated ? h('span', { class: 'dim' }, 'body cut at 512 KB') : '');
    const trace = st.trace ?? this.traces.find((t) => t.id === r.traceId);
    if (trace) {
      const button = h('button', { class: 'tool', onclick: () => this.actions.traceFlow(trace) });
      button.insertAdjacentHTML('afterbegin', icon('flow'));
      button.append(`What it did${trace.sql?.length ? ': ' + plural(trace.sql.length, 'query', 'queries') : ''}`);
      p.result.append(button);
    }
    p.output.hidden = false;
    p.output.textContent = pretty(r.body, r.contentType);
  }

  async #send(id) {
    const entry = this.tries.get(id);
    if (!entry) return;
    const st = entry.state;
    clearTimeout(entry.timer);
    st.rerun = false;
    if (!st.base) {
      toast('Type the address of the running app first, such as http://localhost:5000.');
      entry.parts?.base.focus();
      return;
    }

    let url;
    try {
      url = new URL(st.target.startsWith('/') ? st.target : '/' + st.target, st.base.endsWith('/') ? st.base : st.base + '/').toString();
    } catch {
      toast('The address or path is not valid.');
      return;
    }

    const headers = st.headers.split('\n').map((line) => line.trim()).filter(Boolean).map((line) => {
      const colon = line.indexOf(':');
      return colon < 0 ? null : { name: line.slice(0, colon).trim(), value: line.slice(colon + 1).trim() };
    }).filter(Boolean);

    st.sending = true;
    this.#paint(id);
    try {
      st.response = await api.tryRequest({ url, method: st.method, headers, body: BODYLESS.has(st.method) ? null : st.body });
      st.trace = null;
      if (st.response.traceId) this.waiting.set(st.response.traceId, id);
    } catch (err) {
      st.response = { error: String(err.message ?? err) };
    }
    st.sending = false;
    this.#paint(id);
  }
}

/** Response text, indented when it is JSON. */
function pretty(body, contentType) {
  if (!body) return '(empty body)';
  if (String(contentType).includes('json')) {
    try {
      return JSON.stringify(JSON.parse(body), null, 2);
    } catch {
      // not valid JSON after all
    }
  }
  return body;
}
