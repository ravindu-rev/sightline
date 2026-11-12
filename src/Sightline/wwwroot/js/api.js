// Talks to the Sightline server. Every call carries the token the server put in the page.

const token = document.querySelector('meta[name="sightline-token"]')?.content ?? '';
const auth = { 'X-Sightline-Token': token };
const json = { ...auth, 'Content-Type': 'application/json' };

const check = (r) => {
  if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
  return r;
};

export const api = {
  graph: () => fetch('/api/graph', { headers: auth }).then(check).then((r) => r.json()),

  source: (file) => fetch('/api/source?file=' + encodeURIComponent(file), { headers: auth }).then(check).then((r) => r.json()),

  /** Sends unsaved editor text so the map updates while typing. Nothing is written to disk. */
  overlay: (file, text) => fetch('/api/overlay', { method: 'POST', headers: json, body: JSON.stringify({ file, text }) }).then(check),

  discard: (file) => fetch('/api/overlay?file=' + encodeURIComponent(file), { method: 'DELETE', headers: auth }).then(check),

  save: (file, text) => fetch('/api/source', { method: 'PUT', headers: json, body: JSON.stringify({ file, text }) }).then(check),

  reload: () => fetch('/api/reload', { method: 'POST', headers: auth }).then(check),

  /** Live updates. The browser reconnects by itself when the connection drops. */
  events(handlers) {
    const source = new EventSource('/api/events?token=' + encodeURIComponent(token));
    for (const name of ['hello', 'status', 'patch', 'reload'])
      source.addEventListener(name, (e) => handlers[name]?.(JSON.parse(e.data)));
    source.onerror = () => handlers.offline?.();
    return source;
  },
};
