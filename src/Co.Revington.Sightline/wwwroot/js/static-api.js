// Stands in for the Sightline server in a map written by "sightline export".
// The export holds the graph and, unless it was left out, the source of every file. Source can also
// come from an address with {path} in it, such as raw.githubusercontent.com at a fixed commit.
// Nothing can be edited here: there is no .NET process behind a static page.

const data = await fetch('./data.json').then((r) => {
  if (!r.ok) throw new Error('data.json is missing next to this page.');
  return r.json();
});

const fetched = new Map();
const later = (fn) => setTimeout(fn, 0);

document.documentElement.dataset.mode = 'export';

export const api = {
  mode: 'export',
  readOnly: true,
  exportInfo: { title: data.title, version: data.version, exported: data.exported },

  async graph() {
    return structuredClone(data.graph);
  },

  async source(file) {
    if (data.sources && file in data.sources) return { file, text: data.sources[file], dirty: false };
    if (!data.sourceUrl) throw new Error('404 Not Found');
    if (!fetched.has(file)) {
      const path = file.split('/').map(encodeURIComponent).join('/');
      fetched.set(file, fetch(data.sourceUrl.replace('{path}', path)).then((r) => {
        if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
        return r.text();
      }));
    }
    try {
      return { file, text: await fetched.get(file), dirty: false };
    } catch (err) {
      fetched.delete(file);
      throw err;
    }
  },

  /** Where a file can be read on the web: GitHub when the source comes from raw.githubusercontent.com. */
  webUrl(file, line) {
    const m = /^https:\/\/raw\.githubusercontent\.com\/([^/]+)\/([^/]+)\/([^/]+)\/\{path\}$/.exec(data.sourceUrl ?? '');
    if (!m) return null;
    const path = file.split('/').map(encodeURIComponent).join('/');
    return `https://github.com/${m[1]}/${m[2]}/blob/${m[3]}/${path}${line ? '#L' + line : ''}`;
  },

  async overlay() {},
  async discard() {},
  async save() { throw new Error('This is an exported map; it cannot change files.'); },
  async reload() {},
  async ping() { return 204; },
  async tours() { return data.tours ?? []; },

  events(handlers) {
    later(() => handlers.hello?.({ version: data.graph.version, status: data.graph.status }));
    return { close() {}, readyState: 1 };
  },
};
