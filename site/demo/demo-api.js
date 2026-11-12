// Stands in for the Sightline server on the website.
//
// It serves a recording that site/generate.mjs made from the real tool running on samples/Shop:
// the graph, the source files, and the two live patches the tool sent for one code edit.
// Clicking, searching and the three views are the real UI. New edits cannot be analyzed here,
// because there is no .NET process behind a static page.

const data = await fetch('./demo-data.json').then((r) => r.json());
const original = structuredClone(data.sources);

let version = data.graph.version;
let handlers = {};
let applied = false;                         // the recorded edit is part of the graph right now
const unsaved = new Map();                   // file -> text typed but not saved
const noticeListeners = new Set();

const later = (fn) => setTimeout(fn, 0);     // answer asynchronously, like a server would
const notice = (text) => noticeListeners.forEach((fn) => fn(text));

function replay(patch) {
  const next = structuredClone(patch);
  next.base = version;
  next.version = ++version;
  later(() => handlers.patch?.(next));
}

/** All the "analysis" a static page can do: spot the recorded edit and replay what the tool answered. */
function follow(file, text) {
  if (file === data.edit.file) {
    const has = text.includes(data.edit.marker);
    if (has && !applied) {
      applied = true;
      replay(data.edit.apply);
      return;
    }
    if (!has && applied) {
      applied = false;
      replay(data.edit.revert);
      return;
    }
    if (has) return;
  }
  if (text !== data.sources[file])
    notice('This demo replays a recording, so other edits are not analyzed here. Install Sightline to watch your own code update live.');
}

export const api = {
  mode: 'demo',
  readOnly: false,

  async graph() {
    return { ...structuredClone(data.graph), version };
  },

  /** The sample's files are in the Sightline repository on GitHub. */
  webUrl(file, line) {
    const path = String(file).split('/').map(encodeURIComponent).join('/');
    return `https://github.com/ravindu-rev/sightline/blob/master/samples/${path}${line ? '#L' + line : ''}`;
  },

  async ping() {
    return 204;
  },

  async tours() {
    return data.tours ?? [];
  },

  async apps() {
    return [];
  },

  async traces() {
    return [];
  },

  async tryRequest() {
    throw new Error('The demo has no running app to send requests to. Install Sightline to try your own endpoints.');
  },

  async source(file) {
    if (!(file in data.sources)) throw new Error('404 Not Found');
    return { file, text: unsaved.get(file) ?? data.sources[file], dirty: unsaved.has(file) };
  },

  async overlay(file, text) {
    unsaved.set(file, text);
    follow(file, text);
  },

  async discard(file) {
    unsaved.delete(file);
    follow(file, data.sources[file]);
  },

  async save(file, text) {
    unsaved.delete(file);
    data.sources[file] = text;               // kept for this visit only
    follow(file, text);
    notice('Saved for this visit only. The demo never writes to disk.');
  },

  async reload() {},

  events(given) {
    handlers = given;
    later(() => handlers.hello?.({ version, status: data.graph.status }));
    return { close() {} };
  },
};

/** For demo.js: the recorded edit, notices to show, and a way back to the starting state. */
export const demo = {
  edit: data.edit,

  onNotice(fn) {
    noticeListeners.add(fn);
  },

  reset() {
    unsaved.clear();
    Object.assign(data.sources, structuredClone(original));
    if (applied) {
      applied = false;
      replay(data.edit.revert);
    }
  },
};
