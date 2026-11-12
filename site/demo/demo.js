// Extras for the website demo: a notice bar, messages, and the guided actions
// that the buttons on the landing page start. Everything here drives the real UI
// the way a visitor would: by clicking its lists and typing in its editor.

import { demo } from './js/api.js';

const embedded = window.self !== window.top;
document.documentElement.classList.toggle('embedded', embedded);

const $ = (selector) => document.querySelector(selector);
const all = (selector) => [...document.querySelectorAll(selector)];
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Polls until find() returns something, or gives up after a while. */
async function until(find, ms = 8000) {
  const end = Date.now() + ms;
  for (;;) {
    const found = find();
    if (found) return found;
    if (Date.now() > end) return null;
    await wait(50);
  }
}

// ---------------------------------------------------------------- notice bar and messages

if (!embedded) {
  const bar = document.createElement('div');
  bar.className = 'demo-bar';
  const text = document.createElement('span');
  text.textContent = 'Demo: a recording of the sample shop solution. Clicking and searching are real. New edits are not analyzed here.';
  const link = document.createElement('a');
  link.href = '../';
  link.textContent = 'About Sightline';
  bar.append(text, link);
  document.body.prepend(bar);
}

const toast = document.createElement('div');
toast.className = 'demo-toast';
toast.setAttribute('role', 'status');
document.body.append(toast);

let toastTimer;
demo.onNotice((text) => {
  toast.textContent = text;
  toast.classList.add('on');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove('on'), 8000);
});

// ---------------------------------------------------------------- guided actions

const tab = (name) => $(`.tabs [data-tab="${name}"]`)?.click();

const endpointRow = (verb, route) =>
  all('.list .row').find((r) => r.querySelector('.verb')?.textContent === verb && r.querySelector('.route')?.textContent === route);

const typeRow = (name) =>
  all('.list .row').find((r) => r.querySelector('.code') && r.querySelector('.grow')?.textContent === name);

const editor = () => {
  const el = $('.CodeMirror');
  return el && !el.hidden ? el.CodeMirror : null;
};

const actions = {
  /** The whole solution: every project opened. */
  async map() {
    $('.seg [data-view="map"]')?.click();
    await wait(100);
    all('.canvas-tools .tool').find((b) => b.textContent === 'Open all')?.click();
  },

  /** Every step behind one endpoint. Done when the starting station is drawn and selected. */
  async flow({ verb, route }) {
    tab('endpoints');
    (await until(() => endpointRow(verb, route)))?.click();
    await until(() => all('.n-endpoint.root.sel').find((n) => n.querySelector('.route')?.textContent === route));
  },

  /** One type with what it uses and what uses it. Done when that type sits selected in the middle. */
  async focus({ type }) {
    tab('types');
    (await until(() => typeRow(type)))?.click();
    await until(() => all('.n.center.sel').find((n) => n.querySelector('.t')?.textContent === type));
  },

  /** Types the recorded line into the editor; the map then shows what the real tool answered. */
  async edit() {
    const { endpoint, station, anchor, text, marker } = demo.edit;

    await actions.flow(endpoint);
    (await until(() => all('.n-member').find((n) => n.textContent.includes(station))))?.click();
    let cm = await until(() => (editor()?.getValue().includes(anchor) ? editor() : null));
    if (!cm) return;

    // Start from the file as recorded, even if the visitor already tried this edit.
    for (let attempt = 0; attempt < 2 && cm.getValue().includes(marker); attempt++) {
      const discard = $('#ed-discard');
      if (attempt === 0 && !discard.disabled) discard.click();
      else demo.reset();
      await until(() => !editor()?.getValue().includes(marker), 3000);
      await wait(400);
      cm = editor();
    }

    const line = [...Array(cm.lineCount()).keys()].find((i) => cm.getLine(i).includes(anchor));
    cm.replaceRange('\n', { line, ch: cm.getLine(line).length }, undefined, '+input');
    cm.scrollIntoView({ line: line + 1, ch: 0 }, 90);
    for (const character of text) {
      cm.replaceRange(character, { line: line + 1, ch: cm.getLine(line + 1).length }, undefined, '+input');
      await wait(24);
    }
    await wait(900);                           // let the map redraw before reporting back
  },
};

// ---------------------------------------------------------------- talking to the landing page

const tell = (message) => {
  if (embedded) window.parent.postMessage({ source: 'sightline-demo', ...message }, location.origin);
};

let queue = Promise.resolve();
window.addEventListener('message', (e) => {
  if (e.origin !== location.origin || e.data?.source !== 'sightline-site') return;
  const action = actions[e.data.action];
  if (!action) return;
  queue = queue
    .then(() => action(e.data))
    .then(() => tell({ done: e.data.action }))
    .catch((err) => console.error('Sightline demo action failed', err));
});

// Ready once the first map is on screen.
until(() => $('.n'), 60000).then((drawn) => {
  if (drawn) tell({ ready: true });
});
