// AutoDuck — Spicetify extension
// Lowers Spotify's volume while other apps play audio (data comes from AutoDuck.Helper over a local WebSocket).
// Note: Spicetify loads each extension file separately, so the config logic lives in this file.
(async function AutoDuck() {
  const ready = () => {
    try {
      return !!(
        window.Spicetify && Spicetify.Player && Spicetify.LocalStorage && Spicetify.Menu && Spicetify.PopupModal &&
        Spicetify.React && Spicetify.ReactDOM && Spicetify.ReactJSX && Spicetify.ReactJSX.jsx &&
        Spicetify.Platform && Spicetify.Platform.PlaybackAPI &&
        Number.isFinite(Spicetify.Player.getVolume())
      );
    } catch {
      return false; // PlaybackAPI not ready yet
    }
  };
  while (!ready()) {
    await new Promise((r) => setTimeout(r, 300));
  }

  const TAG = "[AutoDuck]";
  const CFG_KEY = "autoduck:config";
  const PENDING_KEY = "autoduck:pending-restore";

  // ───────────────────────── Config ─────────────────────────
  const DEFAULTS = {
    enabled: true,
    normalVolume: 80, // % — only used when "Always restore to Normal Volume" is on
    duckedVolume: 30, // %
    restoreDelayMs: 1500,
    fadeMs: 300,
    useFixedNormal: false,
    triggerMode: "any", // "any" | "selected"
    triggers: { chrome: true, firefox: true, edge: true, discord: true, other: true },
    customProcesses: [],
    host: "127.0.0.1",
    port: 8765,
    debug: false,
  };
  const GROUPS = { "chrome.exe": "chrome", "firefox.exe": "firefox", "msedge.exe": "edge", "discord.exe": "discord" };

  const clamp = (n, lo, hi, d) => (Number.isFinite(+n) ? Math.min(hi, Math.max(lo, +n)) : d);
  const normProc = (p) => {
    p = String(p).trim().toLowerCase();
    return !p ? "" : p.endsWith(".exe") ? p : p + ".exe";
  };
  function sanitize(raw) {
    const c = { ...DEFAULTS, ...(raw || {}), triggers: { ...DEFAULTS.triggers, ...((raw && raw.triggers) || {}) } };
    c.enabled = !!c.enabled;
    c.useFixedNormal = !!c.useFixedNormal;
    c.debug = !!c.debug;
    c.normalVolume = clamp(c.normalVolume, 0, 100, 80);
    c.duckedVolume = clamp(c.duckedVolume, 0, 100, 30);
    c.restoreDelayMs = clamp(c.restoreDelayMs, 0, 10000, 1500);
    c.fadeMs = clamp(c.fadeMs, 0, 3000, 300);
    c.triggerMode = c.triggerMode === "selected" ? "selected" : "any";
    c.customProcesses = [...new Set((c.customProcesses || []).map(normProc).filter(Boolean))];
    c.port = clamp(c.port, 1024, 65535, 8765);
    c.host = String(c.host || "127.0.0.1");
    return c;
  }
  let cfg;
  try {
    cfg = sanitize(JSON.parse(Spicetify.LocalStorage.get(CFG_KEY) || "{}"));
  } catch {
    cfg = sanitize({});
  }
  const saveCfg = () => Spicetify.LocalStorage.set(CFG_KEY, JSON.stringify(cfg));
  const log = (...a) => console.log(TAG, ...a);
  const dbg = (...a) => cfg.debug && console.debug(TAG, ...a);

  // ───────────────────────── Volume (Spotify only) ─────────────────────────
  const getVol = () => Spicetify.Player.getVolume(); // 0..1
  const setVol = (v) => Spicetify.Player.setVolume(Math.min(1, Math.max(0, v)));

  let ducked = false; // currently ducked (or restoring from ducking)
  let restoring = false; // restore fade in progress
  let manualOverride = false; // user changed the volume while ducked
  let previousVolume = getVol(); // volume before ducking (0..1)
  let lastSet = previousVolume; // last volume we set ourselves
  let fadeTimer = null;
  let verifyTimer = null;
  let lastSetAt = 0; // when we last called setVolume
  let lastRestoreAt = -1e9; // when the last restore finished
  let lastRestoreTarget = 0;
  let restoreTimer = null;
  let watchTimer = null;

  const persist = () => Spicetify.LocalStorage.set(PENDING_KEY, JSON.stringify({ previousVolume }));
  const clearPersist = () => Spicetify.LocalStorage.remove(PENDING_KEY);

  const pct = (v) => Math.round(v * 100);
  function applyVol(v) {
    lastSet = v;
    lastSetAt = performance.now();
    setVol(v);
  }

  // Spotify can drop or reorder rapid setVolume calls, so re-check the final value and retry.
  function verify(target, attempt) {
    clearTimeout(verifyTimer);
    verifyTimer = setTimeout(() => {
      const cur = getVol();
      if (Math.abs(cur - target) > 0.02 && attempt < 3) {
        log(`volume ended at ${pct(cur)}%, expected ${pct(target)}% → retrying`);
        try { applyVol(target); } catch (e) { log("setVolume failed:", e); }
        verify(target, attempt + 1);
      }
    }, 250);
  }

  let fadeGen = 0;
  function fadeTo(target, ms, done) {
    clearInterval(fadeTimer);
    fadeTimer = null;
    const gen = ++fadeGen;
    const finish = () => {
      setVol(target);
      lastSet = target;
      // Re-assert once: rapid setVolume calls can be dropped, leaving the volume slightly off-target.
      setTimeout(() => {
        if (gen === fadeGen) setVol(target);
      }, 250);
      done && done();
    };
    const from = getVol();
    if (ms <= 0 || Math.abs(from - target) < 0.005) return finish();
    const t0 = performance.now();
    fadeTimer = setInterval(() => {
      const k = Math.min(1, (performance.now() - t0) / ms);
      const v = from + (target - from) * k;
      lastSet = v;
      setVol(v);
      if (k >= 1) {
        clearInterval(fadeTimer);
        fadeTimer = null;
        finish();
      }
    }, 25);
  }

  // Manual-change detection: only runs while ducked (not permanent polling).
  let mismatches = 0;
  function startWatch() {
    stopWatch();
    mismatches = 0;
    watchTimer = setInterval(() => {
      if (fadeTimer || !ducked) {
        mismatches = 0;
        return;
      }
      const v = getVol();
      if (Math.abs(v - lastSet) <= 0.05) {
        mismatches = 0;
        return;
      }
      if (++mismatches < 2) return; // must differ on two consecutive checks (~1 s)
      mismatches = 0;
      log(`volume changed manually while ducked (expected ${Math.round(lastSet * 100)}%, got ${Math.round(v * 100)}%) → ${Math.round(v * 100)}% will be restored`);
      previousVolume = v;
      lastSet = v;
      manualOverride = true;
      persist();
      render();
    }, 500);
  }
  function stopWatch() {
    clearInterval(watchTimer);
    watchTimer = null;
  }

  const restoreTarget = () =>
    cfg.useFixedNormal && previousVolume > 0 ? cfg.normalVolume / 100 : previousVolume;

  function duck() {
    clearTimeout(restoreTimer);
    restoreTimer = null;
    if (!ducked) {
      const cur = getVol();
      // Right after our own restore getVolume() can lag behind: trust the value we just set.
      previousVolume =
        performance.now() - lastRestoreAt < 3000 && Math.abs(cur - lastRestoreTarget) > 0.02 ? lastRestoreTarget : cur;
      lastSet = previousVolume;
      ducked = true;
      manualOverride = false;
      persist();
      startWatch();
      log(`ducking started (previous volume ${Math.round(previousVolume * 100)}%)`);
    }
    if (restoring || !manualOverride) {
      restoring = false;
      // min(): if Spotify is already quieter than the ducked volume (or at 0%), never raise it.
      fadeTo(Math.min(previousVolume, cfg.duckedVolume / 100), cfg.fadeMs);
    }
    render();
  }

  function scheduleRestore() {
    if (restoring && !fadeTimer) restoring = false; // watchdog: a restore flag without a running fade is stale
    if (!ducked || restoreTimer || restoring) return;
    restoreTimer = setTimeout(() => {
      restoreTimer = null;
      doRestore();
    }, cfg.restoreDelayMs);
    render();
  }

  function doRestore() {
    restoring = true;
    render();
    const target = restoreTarget();
    fadeTo(target, cfg.fadeMs, () => {
      lastRestoreAt = performance.now();
      lastRestoreTarget = target;
      restoring = false;
      ducked = false;
      manualOverride = false;
      stopWatch();
      clearPersist();
      log(`volume restored to ${Math.round(getVol() * 100)}%`);
      render();
    });
  }

  // ───────────────────────── Trigger logic ─────────────────────────
  let external = []; // all processes currently playing audio (from the helper)
  let triggers = []; // subset that passes the filter

  function isTrigger(proc) {
    const p = proc.toLowerCase();
    if (p === "spotify.exe") return false; // Spotify never triggers ducking
    if (cfg.triggerMode === "any") return true;
    const group = GROUPS[p] || "other";
    return cfg.triggers[group] === true || cfg.customProcesses.includes(p);
  }

  function applyExternal(procs) {
    external = procs;
    triggers = procs.filter(isTrigger);
    if (cfg.enabled) {
      if (triggers.length) duck();
      else scheduleRestore();
    }
    render();
  }

  function setEnabled(on) {
    cfg.enabled = on;
    saveCfg();
    if (!on) {
      clearTimeout(restoreTimer);
      restoreTimer = null;
      if (ducked) doRestore();
    } else {
      applyExternal(external);
    }
    render();
  }

  // ───────────────────────── WebSocket client ─────────────────────────
  let ws = null;
  let connected = false;
  let retry = 0;
  let reconnectTimer = null;
  let hbTimer = null;
  let lastMsgAt = 0;

  function connect() {
    clearTimeout(reconnectTimer);
    if (ws) {
      try {
        ws.onclose = null;
        ws.close();
      } catch {}
    }
    const url = `ws://${cfg.host}:${cfg.port}`;
    dbg("connecting to", url);
    try {
      ws = new WebSocket(url);
    } catch (e) {
      log("failed to create WebSocket:", e);
      return scheduleReconnect();
    }
    ws.onopen = () => {
      connected = true;
      retry = 0;
      lastMsgAt = Date.now();
      log("connected to helper", url);
      ws.send(JSON.stringify({ type: "get_state" }));
      clearInterval(hbTimer);
      hbTimer = setInterval(() => {
        if (Date.now() - lastMsgAt > 35000) return ws.close(); // helper not responding
        try {
          ws.send(JSON.stringify({ type: "ping" }));
        } catch {}
      }, 10000);
      render();
    };
    ws.onmessage = (ev) => {
      lastMsgAt = Date.now();
      let m;
      try {
        m = JSON.parse(ev.data);
      } catch {
        return;
      }
      dbg("message", m);
      if (m.type === "audio_state") {
        let procs = Array.isArray(m.processes) ? m.processes : m.process ? [m.process] : [];
        if (!m.active) procs = [];
        applyExternal(procs.map((p) => String(p).toLowerCase()));
      }
    };
    ws.onclose = () => {
      const was = connected;
      connected = false;
      clearInterval(hbTimer);
      if (was) log("lost connection to helper");
      applyExternal([]); // fail-safe: never stay ducked without a helper
      scheduleReconnect();
    };
    ws.onerror = () => dbg("WebSocket error (onclose will follow)");
  }
  function scheduleReconnect() {
    clearTimeout(reconnectTimer);
    const delay = Math.min(10000, 1000 * 2 ** Math.min(retry++, 4));
    reconnectTimer = setTimeout(connect, delay);
  }

  // ───────────────────────── UI ─────────────────────────
  const pretty = (p) => p.replace(/\.exe$/i, "").replace(/^./, (c) => c.toUpperCase());
  const h = (tag, attrs = {}, ...kids) => {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
      if (k === "class") e.className = v;
      else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
      else if (k === "checked" || k === "value" || k === "disabled") e[k] = v;
      else e.setAttribute(k, v);
    }
    for (const c of kids.flat()) e.append(c instanceof Node ? c : document.createTextNode(String(c)));
    return e;
  };

  const style = h("style", {}, `
    .autoduck{display:flex;flex-direction:column;gap:12px;color:var(--spice-text,#fff);font-size:14px;min-width:380px}
    .autoduck h4{margin:8px 0 0;font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--spice-subtext,#b3b3b3)}
    .autoduck .row{display:flex;align-items:center;justify-content:space-between;gap:12px}
    .autoduck .row input[type=range]{flex:1;accent-color:var(--spice-button,#1db954)}
    .autoduck .val{width:60px;text-align:right;font-variant-numeric:tabular-nums}
    .autoduck .card{background:var(--spice-card,#282828);border-radius:8px;padding:12px 14px;display:flex;flex-direction:column;gap:6px}
    .autoduck .dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:8px}
    .autoduck input[type=checkbox]{accent-color:var(--spice-button,#1db954);width:16px;height:16px}
    .autoduck input[type=text],.autoduck input[type=number]{background:var(--spice-card,#282828);color:inherit;border:1px solid var(--spice-misc,#535353);border-radius:4px;padding:6px 8px}
    .autoduck button{background:var(--spice-button,#1db954);color:var(--spice-main,#000);border:0;border-radius:500px;padding:6px 16px;font-weight:700;cursor:pointer}
    .autoduck .dim{opacity:.4;pointer-events:none}
  `);
  document.head.append(style);

  let ui = null;
  let uiTimer = null;

  function slider(label, key, min, max, step, fmt, onChange) {
    const val = h("span", { class: "val" }, fmt(cfg[key]));
    const input = h("input", {
      type: "range", min, max, step, value: cfg[key],
      oninput: (e) => {
        cfg[key] = +e.target.value;
        val.textContent = fmt(cfg[key]);
        saveCfg();
        onChange && onChange();
      },
    });
    return h("div", { class: "row" }, h("span", {}, label), input, val);
  }
  function check(label, get, set) {
    return h("label", { class: "row" }, h("span", {}, label),
      h("input", { type: "checkbox", checked: get(), onchange: (e) => set(e.target.checked) }));
  }

  function buildUI() {
    const dot = h("span", { class: "dot" });
    const connText = h("span");
    const sVol = h("span"), sExt = h("span"), sState = h("span");
    const reapply = () => cfg.enabled && applyExternal(external);
    const groupBox = h("div", { class: "card" });
    const refreshGroups = () => groupBox.classList.toggle("dim", cfg.triggerMode === "any");

    for (const [key, label] of [["chrome", "Chrome"], ["firefox", "Firefox"], ["edge", "Edge"], ["discord", "Discord"], ["other", "Other Applications"]]) {
      groupBox.append(check(label, () => cfg.triggers[key], (v) => { cfg.triggers[key] = v; saveCfg(); reapply(); }));
    }
    const custom = h("input", {
      type: "text", placeholder: "vlc.exe, game.exe", value: cfg.customProcesses.join(", "), style: "width:100%",
      onchange: (e) => {
        cfg.customProcesses = [...new Set(e.target.value.split(",").map(normProc).filter(Boolean))];
        e.target.value = cfg.customProcesses.join(", ");
        saveCfg(); reapply();
      },
    });
    const modeSel = h("select", {
      style: "background:var(--spice-card,#282828);color:inherit;border:1px solid var(--spice-misc,#535353);border-radius:4px;padding:6px",
      onchange: (e) => { cfg.triggerMode = e.target.value; saveCfg(); refreshGroups(); reapply(); },
    }, h("option", { value: "any" }, "Any application"), h("option", { value: "selected" }, "Selected applications only"));
    modeSel.value = cfg.triggerMode;
    refreshGroups();

    const host = h("input", { type: "text", value: cfg.host, style: "width:130px", onchange: (e) => { cfg.host = e.target.value.trim() || "127.0.0.1"; saveCfg(); } });
    const port = h("input", { type: "number", value: cfg.port, style: "width:80px", onchange: (e) => { cfg.port = clamp(e.target.value, 1024, 65535, 8765); saveCfg(); } });

    const root = h("div", { class: "autoduck" },
      h("div", { class: "card" },
        h("div", { class: "row" }, h("span", {}, "Connection Status"), h("span", {}, dot, connText)),
        h("div", { class: "row" }, h("span", {}, "Spotify"), sVol),
        h("div", { class: "row" }, h("span", {}, "External Audio"), sExt),
        h("div", { class: "row" }, h("span", {}, "Status"), sState)),
      check("Enable AutoDuck", () => cfg.enabled, setEnabled),
      h("h4", {}, "Volume"),
      slider("Normal Volume", "normalVolume", 0, 100, 1, (v) => `${v}%`),
      check("Always restore to Normal Volume (ignore previous volume)", () => cfg.useFixedNormal, (v) => { cfg.useFixedNormal = v; saveCfg(); }),
      slider("Ducked Volume", "duckedVolume", 0, 100, 1, (v) => `${v}%`, reapply),
      slider("Restore Delay", "restoreDelayMs", 0, 5000, 100, (v) => `${(v / 1000).toFixed(1)}s`),
      slider("Fade Duration", "fadeMs", 0, 2000, 50, (v) => `${v}ms`),
      h("h4", {}, "Trigger Applications"),
      h("div", { class: "row" }, h("span", {}, "Trigger Mode"), modeSel),
      groupBox,
      h("div", {}, h("div", { style: "margin-bottom:4px;color:var(--spice-subtext,#b3b3b3)" }, "Extra processes (comma-separated)"), custom),
      h("h4", {}, "Advanced"),
      h("div", { class: "row" }, h("span", {}, "Helper (host : port)"), h("span", {}, host, " : ", port)),
      check("Debug log in DevTools console", () => cfg.debug, (v) => { cfg.debug = v; saveCfg(); }),
      h("div", { class: "row" }, h("span"), h("button", { onclick: () => { retry = 0; connect(); } }, "Reconnect")),
    );
    ui = { root, dot, connText, sVol, sExt, sState };
    return root;
  }

  function render() {
    if (!ui || !document.body.contains(ui.root)) return;
    ui.dot.style.background = connected ? "#1db954" : "#e22134";
    ui.connText.textContent = connected ? "Connected" : "Disconnected";
    ui.sVol.textContent = `${Math.round(getVol() * 100)}%`;
    ui.sExt.textContent = external.length ? external.map(pretty).join(", ") : "None";
    ui.sState.textContent = !connected ? "Disconnected" : !cfg.enabled ? "Disabled"
      : restoring || restoreTimer ? "Restoring…" : ducked ? "Ducked" : "Normal";
  }

  function openSettings() {
    const root = buildUI();
    Spicetify.PopupModal.display({ title: "AutoDuck", content: root, isLarge: false });
    render();
    clearInterval(uiTimer);
    uiTimer = setInterval(() => {
      if (!document.body.contains(root)) { clearInterval(uiTimer); ui = null; return; }
      render();
    }, 500);
  }

  new Spicetify.Menu.Item("AutoDuck settings", false, openSettings).register();

  // Playbar button (next to the volume controls) → opens settings.
  try {
    new Spicetify.Playbar.Button("AutoDuck", "volume", openSettings, false, false, true);
  } catch (e) {
    log("Playbar button could not be created, use the profile menu instead:", e);
  }

  // ───────────────────────── Startup / shutdown ─────────────────────────
  // Spicetify/Spotify reloaded while ducked → restore the saved volume.
  try {
    const pending = JSON.parse(Spicetify.LocalStorage.get(PENDING_KEY) || "null");
    if (pending && Number.isFinite(pending.previousVolume)) {
      previousVolume = pending.previousVolume;
      ducked = true;
      lastSet = getVol();
      startWatch();
      log("found a pending duck; volume will be restored unless the helper reports active audio");
      scheduleRestore();
    }
  } catch {}

  window.addEventListener("beforeunload", () => {
    if (ducked && !manualOverride) { setVol(restoreTarget()); clearPersist(); }
  });

  connect();
  log("ready");
})();