// The terminal page: xterm.js in a WebView2, talking to the app through web messages. Every message is one JSON object with a
// string "t"; bytes are standard base64 in "d" (the protocol is fixed in Berth.Presentation.TerminalProtocol). Output is written
// as bytes, never decoded to text here, and acknowledged once xterm has written it, which is what bounds a flood.
(function () {
  'use strict';

  var host = window.chrome && window.chrome.webview;
  if (!host) {
    return;
  }

  var MIN_COLUMNS = 2, MAX_COLUMNS = 1000, MIN_ROWS = 1, MAX_ROWS = 500;
  var FIT_INTERVAL_MS = 100;

  var term = new Terminal({
    fontFamily: '"Cascadia Mono", Consolas, monospace',
    fontSize: 13,
    cursorBlink: true,
    scrollback: 10000,
    minimumContrastRatio: 4.5,
    // Typing is off until the app says a shell takes it.
    disableStdin: true,
    // A link a program prints (OSC 8) is never followed: no dialog, no navigation, no window. Opening links is not offered yet.
    linkHandler: {
      activate: function () {},
      allowNonHttpProtocols: false,
    },
  });
  // The addon's UMD build puts its module, not the class, on the global object.
  var fit = new FitAddon.FitAddon();
  term.loadAddon(fit);

  var container = document.getElementById('terminal');
  var encoder = new TextEncoder();
  var ready = false;
  var sentColumns = 0, sentRows = 0;

  function post(message) {
    host.postMessage(JSON.stringify(message));
  }

  function toBase64(bytes) {
    if (typeof bytes.toBase64 === 'function') {
      return bytes.toBase64();
    }
    var text = '';
    for (var i = 0; i < bytes.length; i += 0x8000) {
      text += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }
    return btoa(text);
  }

  function fromBase64(text) {
    if (typeof Uint8Array.fromBase64 === 'function') {
      return Uint8Array.fromBase64(text);
    }
    var raw = atob(text);
    var bytes = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) {
      bytes[i] = raw.charCodeAt(i);
    }
    return bytes;
  }

  function validSize(columns, rows) {
    return columns >= MIN_COLUMNS && columns <= MAX_COLUMNS && rows >= MIN_ROWS && rows <= MAX_ROWS;
  }

  // Fits the terminal to the page. False while the page has no size (minimized, tab hidden): nothing is fitted or sent then.
  function fitNow() {
    if (container.clientWidth === 0 || container.clientHeight === 0) {
      return false;
    }
    var size = fit.proposeDimensions();
    if (!size || !validSize(size.cols, size.rows)) {
      return false;
    }
    if (size.cols !== term.cols || size.rows !== term.rows) {
      term.resize(size.cols, size.rows);
    }
    return true;
  }

  // The size goes to the app once the page said ready, and again only when the columns or rows changed.
  function report() {
    if (!ready) {
      ready = true;
      sentColumns = term.cols;
      sentRows = term.rows;
      post({ t: 'ready', cols: term.cols, rows: term.rows });
    } else if (term.cols !== sentColumns || term.rows !== sentRows) {
      sentColumns = term.cols;
      sentRows = term.rows;
      post({ t: 'resize', cols: term.cols, rows: term.rows });
    }
  }

  // At most one fit per 100 ms while the page is resized, the last one always made (trailing).
  var fitTimer = 0, lastFit = 0;
  function scheduleFit() {
    if (fitTimer) {
      return;
    }
    var wait = Math.max(0, FIT_INTERVAL_MS - (performance.now() - lastFit));
    fitTimer = setTimeout(function () {
      fitTimer = 0;
      lastFit = performance.now();
      if (fitNow()) {
        report();
      }
    }, wait);
  }

  term.open(container);
  if (fitNow()) {
    report();
  }
  new ResizeObserver(function () {
    // The first fit, when the page had no size at open, is made at once so ready is not held back.
    if (!ready) {
      if (fitNow()) {
        report();
      }
      return;
    }
    scheduleFit();
  }).observe(container);

  term.onData(function (data) {
    post({ t: 'input', d: toBase64(encoder.encode(data)) });
  });
  term.onBinary(function (data) {
    var bytes = new Uint8Array(data.length);
    for (var i = 0; i < data.length; i++) {
      bytes[i] = data.charCodeAt(i) & 0xff;
    }
    post({ t: 'input', d: toBase64(bytes) });
  });

  function copySelection() {
    if (term.hasSelection()) {
      post({ t: 'copy', d: term.getSelection() });
    }
  }

  // The keys the page keeps for itself: copy, paste and F6. Every other key goes to xterm, and with it to the shell. A handled key
  // has its default prevented as well, so the browser does not paste or copy a second time. Keys are matched on their position
  // (e.code), so copy and paste work on any keyboard layout (on a Cyrillic one e.key is the local letter).
  term.attachCustomKeyEventHandler(function (e) {
    var code = e.code;
    var ctrl = e.ctrlKey && !e.altKey && !e.metaKey;
    var action = null;
    if ((ctrl && e.shiftKey && code === 'KeyC') || (ctrl && !e.shiftKey && code === 'Insert')) {
      action = copySelection;
    } else if (ctrl && !e.shiftKey && code === 'KeyC' && term.hasSelection()) {
      action = function () {
        copySelection();
        term.clearSelection();
      };
    } else if ((ctrl && code === 'KeyV') || (e.shiftKey && !e.ctrlKey && !e.altKey && code === 'Insert')) {
      action = function () {
        post({ t: 'paste' });
      };
    } else if (code === 'F6' && !e.ctrlKey && !e.altKey) {
      action = function () {
        post({ t: 'leave' });
      };
    }
    if (!action) {
      return true;
    }
    e.preventDefault();
    if (e.type === 'keydown') {
      action();
    }
    return false;
  });

  container.addEventListener('contextmenu', function (e) {
    e.preventDefault();
    if (term.hasSelection()) {
      copySelection();
    } else {
      post({ t: 'paste' });
    }
  });

  var THEME_KEYS = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white',
    'brightBlack', 'brightRed', 'brightGreen', 'brightYellow', 'brightBlue', 'brightMagenta', 'brightCyan', 'brightWhite'];

  function applyTheme(m) {
    var theme = {
      background: m.background,
      foreground: m.foreground,
      cursor: m.cursor,
      cursorAccent: m.background,
      selectionBackground: m.selection,
    };
    if (Array.isArray(m.ansi) && m.ansi.length === THEME_KEYS.length) {
      for (var i = 0; i < THEME_KEYS.length; i++) {
        theme[THEME_KEYS[i]] = m.ansi[i];
      }
    }
    term.options.theme = theme;
    if (typeof m.contrast === 'number') {
      term.options.minimumContrastRatio = m.contrast;
    }
    document.body.style.background = m.background;
  }

  host.addEventListener('message', function (event) {
    var m;
    try {
      m = JSON.parse(event.data);
    } catch (_) {
      return;
    }
    if (!m || typeof m.t !== 'string') {
      return;
    }
    switch (m.t) {
      case 'output':
        if (typeof m.d === 'string') {
          var bytes = fromBase64(m.d);
          term.write(bytes, function () {
            post({ t: 'ack', n: bytes.length });
          });
        }
        break;
      case 'paste':
        if (typeof m.d === 'string') {
          term.paste(m.d);
        }
        break;
      case 'reset':
        term.reset();
        break;
      case 'enabled':
        term.options.disableStdin = m.on !== true;
        break;
      case 'focus':
        term.focus();
        break;
      case 'theme':
        applyTheme(m);
        break;
    }
  });

  // For the app's checks, in Debug builds only (the host sets berthDebug there before the page loads): the terminal object.
  if (window.berthDebug === true) {
    window.berthTerminal = term;
  }
})();
