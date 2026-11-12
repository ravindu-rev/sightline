// Guided tours: a short series of views with a note on each, kept in the repository as
// .sightline/tours/<name>.json so a new teammate can be walked through the solution.
// Steps point at nodes by id, with the label as a fallback when the code has been renamed since.

import { api } from './api.js';
import { esc, h, icon, plural, toast } from './ui.js';

export class Tours {
  /** actions: { current() -> { view, id, label, dir }, show(sel), canvas } */
  constructor(store, actions) {
    this.store = store;
    this.actions = actions;
    this.list = [];
    this.playing = null;           // { tour, index }
    this.draft = null;             // tour being written: { id, title, steps }
    this.card = null;
  }

  async load() {
    try {
      this.list = (await api.tours?.()) ?? [];
    } catch {
      this.list = [];
    }
    this.renderButton();
  }

  renderButton() {
    const button = document.querySelector('#tours');
    button.hidden = this.list.length === 0 && api.mode !== 'live';
    button.replaceChildren();
    button.insertAdjacentHTML('afterbegin', icon('tour'));
    button.append(this.list.length ? plural(this.list.length, 'tour') : 'Tours');
  }

  // ---------------------------------------------------------------- list and editor (in the right panel)

  listHtml() {
    const editable = api.mode === 'live';
    const rows = this.list.map((t) =>
      `<div class="tour-row"><div class="grow"><b>${esc(t.title)}</b><span class="dim">${esc(plural(t.steps?.length ?? 0, 'step'))}</span></div>` +
      `<button class="tool" data-act="tourPlay" data-id="${esc(t.id)}">${icon('play')}Start</button>` +
      (editable ? `<button class="tool" data-act="tourEdit" data-id="${esc(t.id)}">Edit</button>` : '') + '</div>').join('');

    return `<div class="sign"><div><h2>Tours</h2><p>A walk through the solution, one view at a time, with a note on each. ` +
      `Tours live in the repository in .sightline/tours, so everyone who clones it gets them.</p></div></div>` +
      (rows || '<p class="dim">No tours yet.</p>') +
      (editable ? `<div class="acts"><button class="tool" data-act="tourNew" data-id="">${icon('plus')}New tour</button></div>` : '');
  }

  editorHtml() {
    const d = this.draft;
    const steps = d.steps.map((s, i) =>
      `<div class="tour-step"><div class="ph"><span class="n">${i + 1}</span><b>${esc(s.label || s.id || s.view)}</b>` +
      `<span class="dim">${esc(s.view)}</span><span class="grow"></span>` +
      `<button class="tool icon-only" data-act="tourUp" data-id="${i}" title="Move up" aria-label="Move up">${icon('collapse')}</button>` +
      `<button class="tool icon-only" data-act="tourDrop" data-id="${i}" title="Remove this step" aria-label="Remove this step">${icon('close')}</button></div>` +
      `<textarea data-step="${i}" rows="3" placeholder="What should someone notice here?">${esc(s.text)}</textarea></div>`).join('');

    return `<div class="sign"><div><h2>${d.id ? 'Edit tour' : 'New tour'}</h2><p>Set up the map the way you want it seen, then add it as a step. ` +
      `Saving writes .sightline/tours/&lt;name&gt;.json; commit it to share the tour.</p></div></div>` +
      `<label class="field">Title<input data-tour-title value="${esc(d.title)}" placeholder="How an order is placed"></label>` +
      steps +
      `<div class="acts"><button class="tool" data-act="tourAdd" data-id="">${icon('plus')}Add the current view as a step</button>` +
      `<button class="tool primary" data-act="tourSave" data-id="">${icon('save')}Save</button>` +
      (d.id ? `<button class="tool" data-act="tourDelete" data-id="${esc(d.id)}">Delete</button>` : '') +
      `<button class="tool" data-act="tours" data-id="">Cancel</button></div>`;
  }

  /** Keeps what is typed in the editor; called on every input in the right panel. */
  onInput(target) {
    if (!this.draft) return;
    if (target.matches('[data-tour-title]')) this.draft.title = target.value;
    if (target.matches('[data-step]')) this.draft.steps[Number(target.dataset.step)].text = target.value;
  }

  newTour() {
    this.draft = { id: null, title: '', steps: [] };
  }

  edit(id) {
    const t = this.list.find((x) => x.id === id);
    if (t) this.draft = structuredClone({ id: t.id, title: t.title, steps: t.steps ?? [] });
  }

  addStep() {
    const c = this.actions.current();
    if (!c.id) return toast('Pick something first: an endpoint, a type or a method.');
    this.draft.steps.push({ view: c.view, id: c.id, label: c.label, dir: c.dir === 'up' ? 'up' : undefined, text: '' });
  }

  moveUp(i) {
    if (i <= 0) return;
    const s = this.draft.steps;
    [s[i - 1], s[i]] = [s[i], s[i - 1]];
  }

  drop(i) {
    this.draft.steps.splice(i, 1);
  }

  async save() {
    const d = this.draft;
    if (!d.title.trim()) return toast('Give the tour a title.');
    if (!d.steps.length) return toast('Add at least one step.');
    try {
      const { id } = await api.saveTour({ title: d.title.trim(), steps: d.steps }, d.id);
      toast(`Saved to .sightline/tours/${id}.json. Commit it to share the tour.`, 6000);
      this.draft = null;
      await this.load();
      return true;
    } catch (err) {
      toast('Could not save the tour: ' + err.message);
      return false;
    }
  }

  async remove(id) {
    if (!confirm('Delete this tour? Its file is removed from .sightline/tours.')) return false;
    await api.deleteTour(id).catch((err) => toast('Could not delete the tour: ' + err.message));
    this.draft = null;
    await this.load();
    return true;
  }

  // ---------------------------------------------------------------- playing

  play(id, index = 0) {
    const tour = this.list.find((t) => t.id === id);
    if (!tour?.steps?.length) return;
    this.playing = { tour, index };
    this.#step();
  }

  #step() {
    const { tour, index } = this.playing;
    const step = tour.steps[index];
    const id = this.store.node(step.id) ? step.id : this.#byLabel(step.label);
    if (id) location.hash = `#view=${step.view}&id=${encodeURIComponent(id)}${step.dir === 'up' ? '&dir=up' : ''}`;
    else toast(`"${step.label}" is not in the solution any more.`);
    this.#renderCard(step, index, tour);
  }

  #byLabel(label) {
    if (!label) return null;
    for (const n of this.store.nodes.values()) if (this.store.label(n.id) === label) return n.id;
    return null;
  }

  #renderCard(step, index, tour) {
    this.card?.remove();
    const last = index === tour.steps.length - 1;
    const back = h('button', { class: 'tool', disabled: index === 0, onclick: () => { this.playing.index--; this.#step(); } }, 'Back');
    const next = h('button', { class: 'tool primary', onclick: () => {
      if (last) return this.stop();
      this.playing.index++;
      this.#step();
    } }, last ? 'Finish' : 'Next');
    const close = h('button', { class: 'tool icon-only', title: 'Close the tour', 'aria-label': 'Close the tour', onclick: () => this.stop() });
    close.insertAdjacentHTML('afterbegin', icon('close'));

    this.card = h('div', { class: 'tour-card', role: 'dialog', 'aria-label': tour.title },
      h('div', { class: 'tour-head' }, h('b', null, tour.title), h('span', { class: 'dim' }, `${index + 1} of ${tour.steps.length}`), close),
      h('p', null, step.text || ''),
      h('div', { class: 'acts' }, back, next));
    this.actions.canvas.append(this.card);
  }

  stop() {
    this.playing = null;
    this.card?.remove();
    this.card = null;
  }
}
