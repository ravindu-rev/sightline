// Small DOM helpers shared by all UI modules.

export const esc = (s) =>
  String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/** Creates an element: h('button', { class: 'x', onclick: fn }, 'text', child) */
export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'html') el.innerHTML = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    el.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return el;
}

const ICONS = {
  search: '<circle cx="7" cy="7" r="4.5"/><path d="M10.5 10.5 14 14"/>',
  plus: '<path d="M8 3v10M3 8h10"/>',
  minus: '<path d="M3 8h10"/>',
  fit: '<path d="M2 6V2h4M10 2h4v4M14 10v4h-4M6 14H2v-4"/>',
  open: '<path d="M9 2h5v5M14 2 7.5 8.5M12 9.5V14H2V4h4.5"/>',
  db: '<ellipse cx="8" cy="4" rx="5" ry="2"/><path d="M3 4v8c0 1.1 2.2 2 5 2s5-.9 5-2V4M3 8c0 1.1 2.2 2 5 2s5-.9 5-2"/>',
  globe: '<circle cx="8" cy="8" r="6"/><path d="M2 8h12M8 2c2.2 2 2.2 10 0 12M8 2c-2.2 2-2.2 10 0 12"/>',
  flow: '<circle cx="3" cy="8" r="1.5"/><circle cx="13" cy="4" r="1.5"/><circle cx="13" cy="12" r="1.5"/><path d="M4.5 8H8m0 0V4h3.5M8 8v4h3.5"/>',
  callers: '<circle cx="13" cy="8" r="1.5"/><circle cx="3" cy="4" r="1.5"/><circle cx="3" cy="12" r="1.5"/><path d="M11.5 8H8m0 0V4H4.5M8 8v4H4.5"/>',
  focus: '<circle cx="8" cy="8" r="5.5"/><circle cx="8" cy="8" r="1.5"/>',
  save: '<path d="M3 2h8l3 3v9H3zM5.5 2v3.5h4V2M5.5 14v-4h5v4"/>',
  undo: '<path d="M6 4 2.5 7.5 6 11M3 7.5h6.5a3.5 3.5 0 0 1 0 7H8"/>',
  warn: '<path d="M8 2l6.5 11.5h-13zM8 6.5v3.5M8 12v.4"/>',
  lock: '<rect x="3.5" y="7" width="9" height="6.5" rx="1"/><path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2"/>',
  theme: '<circle cx="8" cy="8" r="5.5"/><path d="M8 2.5v11a5.5 5.5 0 0 0 0-11z" fill="currentColor"/>',
  back: '<path d="M13 8H3m4-4L3 8l4 4"/>',
  expand: '<path d="M3 6l5 5 5-5"/>',
  collapse: '<path d="M3 10l5-5 5 5"/>',
  reload: '<path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 2v3h-3"/>',
  check: '<path d="M3 8.5l3.2 3L13 4.5"/>',
  play: '<path d="M5 3.5v9l7-4.5z"/>',
  send: '<path d="M2.5 8h9M8.5 4.5 12 8l-3.5 3.5"/>',
  link: '<path d="M6.5 9.5l3-3M7 4.5l1.2-1.2a2.8 2.8 0 0 1 4 4L11 8.5M9 11.5l-1.2 1.2a2.8 2.8 0 0 1-4-4L5 7.5"/>',
  tour: '<circle cx="3.5" cy="12" r="1.5"/><circle cx="12.5" cy="4" r="1.5"/><path d="M5 12h3.5a2 2 0 0 0 0-4h-1a2 2 0 0 1 0-4H11"/>',
  pulse: '<path d="M1.5 8h3l2-4.5 3 9 2-4.5h3"/>',
  close: '<path d="M4 4l8 8M12 4l-8 8"/>',
};

export const icon = (name, size = 14) =>
  `<svg class="ic" width="${size}" height="${size}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name] ?? ''}</svg>`;

export const TYPE_CODES = { class: 'C', interface: 'I', record: 'R', 'record struct': 'R', struct: 'S', enum: 'E' };

/** Letter badge for a type kind, like a station code. */
export const typeCode = (kind) => `<span class="code c-${esc(String(kind).split(' ')[0])}">${TYPE_CODES[kind] ?? 'C'}</span>`;

export const verbChip = (verb) => `<span class="verb v-${esc(String(verb).split(',')[0])}">${esc(verb)}</span>`;

const TAG_LABELS = {
  controller: 'controller', dbcontext: 'db context', hosted: 'background', hub: 'hub', middleware: 'middleware',
  handler: 'handler', abstract: 'abstract', static: 'static', entrypoint: 'entry point', exception: 'exception',
};

export const tagLabel = (tag) => TAG_LABELS[tag] ?? tag;

export const fileName = (path) => String(path ?? '').split(/[\\/]/).pop();

export const debounce = (fn, ms) => {
  let t;
  return (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
};

export const plural = (n, word, many = word + 's') => `${n} ${n === 1 ? word : many}`;

/** A short message at the bottom of the page that goes away by itself. */
export function toast(text, ms = 4000) {
  let el = document.querySelector('.toast');
  if (!el) {
    el = h('div', { class: 'toast', role: 'status' });
    document.body.append(el);
  }
  el.textContent = text;
  el.classList.add('on');
  clearTimeout(el.__timer);
  el.__timer = setTimeout(() => el.classList.remove('on'), ms);
}

/** Browser storage can be blocked; never let that break the page. */
export const remember = {
  get(key, fallback) {
    try { return JSON.parse(localStorage.getItem('sightline.' + key)) ?? fallback; } catch { return fallback; }
  },
  set(key, value) {
    try { localStorage.setItem('sightline.' + key, JSON.stringify(value)); } catch { /* ignore */ }
  },
};
