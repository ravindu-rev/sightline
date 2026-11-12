// Landing page behaviour: copy buttons, and the embedded demo (sizing, activation, guided actions).

// ---------------------------------------------------------------- copy buttons

for (const button of document.querySelectorAll('[data-copy]')) {
  button.addEventListener('click', async () => {
    const text = document.getElementById(button.dataset.copy).textContent.trim();
    try {
      await navigator.clipboard.writeText(text);
      button.textContent = 'Copied';
    } catch {
      button.textContent = 'Press Ctrl+C';          // clipboard blocked: select the text instead
      const range = document.createRange();
      range.selectNodeContents(document.getElementById(button.dataset.copy));
      getSelection().removeAllRanges();
      getSelection().addRange(range);
    }
    setTimeout(() => { button.textContent = 'Copy'; }, 1800);
  });
}

// ---------------------------------------------------------------- demo frame

const frame = document.getElementById('frame');
const iframe = frame.querySelector('iframe');
const guard = frame.querySelector('.guard');
const status = document.getElementById('demo-status');
const asks = [...document.querySelectorAll('[data-ask]')];

// The tool is a desktop layout. Below this width the frame shows it scaled down instead of squeezed.
const MIN_WIDTH = 1240;
const HEIGHT = 760;

function fit() {
  const available = frame.clientWidth;
  const width = Math.max(available, MIN_WIDTH);
  const scale = available / width;
  iframe.style.width = width + 'px';
  iframe.style.height = HEIGHT + 'px';
  iframe.style.transform = scale < 1 ? `scale(${scale})` : '';
  frame.style.height = Math.round(HEIGHT * scale) + 'px';
}

new ResizeObserver(fit).observe(frame);
fit();
iframe.src = iframe.dataset.src;                      // set here so the page paints before the demo loads

// The demo only takes clicks and the mouse wheel after a click on it, so scrolling past it stays smooth.
guard.addEventListener('click', () => {
  frame.classList.add('live');
  iframe.focus();
});
frame.addEventListener('mouseleave', () => frame.classList.remove('live'));
document.addEventListener('pointerdown', (e) => {
  if (!frame.contains(e.target)) frame.classList.remove('live');
});

// What to say under the demo once a guided action has finished.
const AFTER = {
  flow: 'Each numbered step is a call, in the order the code makes it. Click a step to see its code.',
  focus: 'Everything that uses the type is on one side, everything it uses on the other.',
  map: 'Every project is a zone. Click the map, then hover a type to see its lines.',
  edit: 'The typed line added a step to the flow, and the strip at the bottom logged it. "Discard changes" takes it back.',
};

// The demo says when it has drawn its first map (the buttons work from then on) and when an action is done.
window.addEventListener('message', (e) => {
  if (e.origin !== location.origin || e.source !== iframe.contentWindow) return;
  if (e.data?.source !== 'sightline-demo') return;

  if (e.data.ready) {
    for (const ask of asks) ask.disabled = false;
    status.textContent = 'Ready. Click the map to explore it, or try one of the buttons above.';
  } else if (AFTER[e.data.done]) {
    status.textContent = AFTER[e.data.done];
  }
});

for (const ask of asks) {
  ask.addEventListener('click', () => {
    iframe.contentWindow.postMessage({ source: 'sightline-site', ...JSON.parse(ask.dataset.ask) }, location.origin);
    const box = frame.getBoundingClientRect();
    if (box.top < 0 || box.bottom > innerHeight) frame.scrollIntoView({ block: 'center' });
    status.textContent = 'Working in the demo.';
  });
}
