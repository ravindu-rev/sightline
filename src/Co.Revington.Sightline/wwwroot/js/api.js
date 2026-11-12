// Talks to the Sightline server. Every call carries the token the server put in the page.

const token = document.querySelector('meta[name="sightline-token"]')?.content ?? '';
const auth = { 'X-Sightline-Token': token };
const json = { ...auth, 'Content-Type': 'application/json' };

const check = (r) => {
  if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
  return r;
};

const send = (method, url, body) =>
  fetch(url, { method, headers: body === undefined ? auth : json, body: body === undefined ? undefined : JSON.stringify(body) }).then(check);

export const api = {
  mode: 'live',
  readOnly: false,

  graph: () => fetch('/api/graph', { headers: auth }).then(check).then((r) => r.json()),

  source: (file) => fetch('/api/source?file=' + encodeURIComponent(file), { headers: auth }).then(check).then((r) => r.json()),

  /** Sends unsaved editor text so the map updates while typing. Nothing is written to disk. */
  overlay: (file, text) => send('POST', '/api/overlay', { file, text }),

  discard: (file) => send('DELETE', '/api/overlay?file=' + encodeURIComponent(file)),

  save: (file, text) => send('PUT', '/api/source', { file, text }),

  reload: () => send('POST', '/api/reload'),

  /** 204 while the server runs, 401 when it restarted with a new token, 0 when it does not answer. */
  ping: () => fetch('/api/ping', { headers: auth, cache: 'no-store' }).then((r) => r.status, () => 0),

  /** Opens a file at a line in the developer's editor. editor: vscode, cursor, rider, visualstudio or empty for the default. */
  open: (file, line, editor) => send('POST', '/api/open', { file, line, editor }).then((r) => r.json()),

  /** Guided tours kept in the repository. */
  tours: () => fetch('/api/tours', { headers: auth }).then(check).then((r) => r.json()),
  saveTour: (tour, id) => send('PUT', '/api/tours' + (id ? '?id=' + encodeURIComponent(id) : ''), tour).then((r) => r.json()),
  deleteTour: (id) => send('DELETE', '/api/tours?id=' + encodeURIComponent(id)),

  /** Apps running with Co.Revington.Sightline.Hosting, with their addresses, services and endpoints. */
  apps: () => fetch('/api/runtime', { headers: auth }).then(check).then((r) => r.json()),

  /** Sends a request to a running app through Sightline (the page itself may not call other ports). */
  tryRequest: (request) => send('POST', '/api/try', request).then((r) => r.json()),

  /** Recent requests the running apps reported, newest first. */
  traces: () => fetch('/api/traces', { headers: auth }).then(check).then((r) => r.json()),

  /** Live updates. The browser reconnects by itself when the connection drops. */
  events(handlers) {
    const source = new EventSource('/api/events?token=' + encodeURIComponent(token));
    for (const name of ['hello', 'status', 'patch', 'reload', 'runtime', 'trace', 'reveal'])
      source.addEventListener(name, (e) => handlers[name]?.(JSON.parse(e.data)));
    source.onerror = () => handlers.offline?.();
    return source;
  },
};
