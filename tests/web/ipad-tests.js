// Tests for the iPad page (cast/pc/web/ipad.html), run by tests/run-tests.ps1 in headless Chrome
// or Edge: the runner appends this file to a copy of the page, so it shares the page's globals
// (top-level let/const of classic scripts are visible across <script> elements) and tests the
// real code, not a copy. The results are added to the page as a pre element with the id
// test-results, one line per test: "ok    name" or "FAIL  name: why".
"use strict";
(function () {
  const out = [];
  let failed = 0;
  function test(name, fn) {
    try { fn(); out.push("ok    " + name); }
    catch (e) { failed++; out.push("FAIL  " + name + ": " + e.message); }
  }
  function eq(want, got, what) {
    const a = JSON.stringify(want), b = JSON.stringify(got);
    if (a !== b) throw new Error((what || "") + " expected " + a + ", got " + b);
  }
  function ok(cond, what) { if (!cond) throw new Error(what); }

  // every message the page would send to the PC
  let sent = [];
  ws = { readyState: 1, send: m => sent.push(m instanceof Uint8Array ? Array.from(m) : m), close() {} };
  const lastSent = () => sent[sent.length - 1];

  // ---- MP4 boxes (WebCodecs reads avcC from the init segment, tfdt from each fragment) ----
  function box(type, payload) {
    const b = new Uint8Array(8 + payload.length), n = b.length;
    b.set([n >>> 24, (n >> 16) & 255, (n >> 8) & 255, n & 255]);
    for (let i = 0; i < 4; i++) b[4 + i] = type.charCodeAt(i);
    b.set(payload, 8);
    return b;
  }
  function cat(...parts) {
    const n = parts.reduce((s, p) => s + p.length, 0), r = new Uint8Array(n);
    let o = 0; parts.forEach(p => { r.set(p, o); o += p.length; });
    return r;
  }
  test("mp4Box finds a nested box and returns its payload", () => {
    const avcC = box("avcC", [1, 0x42, 0xC0, 0x1F]);
    const mp4 = cat(box("ftyp", [1, 2, 3, 4]), box("moov", cat(box("trak", cat(box("avc1", cat([9, 9], avcC)))))));
    eq([1, 0x42, 0xC0, 0x1F], Array.from(mp4Box(mp4, "avcC")));
  });
  test("mp4Box: a missing box is null", () => {
    eq(null, mp4Box(box("ftyp", [0, 0, 0, 0]), "tfdt"));
  });

  // ---- touch position -> PC screen (0..65535), for each picture size ------------------------
  cfg = { w: 1920, h: 1080, codec: "avc1.42C02A", rate: 48000, control: true };
  const W = innerWidth, H = innerHeight;
  test("norm, stretch: screen corners are the PC's corners", () => {
    fit = "stretch";
    eq([0, 0], norm(0, 0));
    eq([65535, 65535], norm(W, H));
  });
  test("norm, fit: the middle is the middle, the black bars clamp to the edge", () => {
    fit = "contain";
    const m = norm(W / 2, H / 2);
    ok(Math.abs(m[0] - 32768) < 40 && Math.abs(m[1] - 32768) < 40, "middle " + m);
    const s = Math.min(W / 1920, H / 1080), barY = (H - 1080 * s) / 2, barX = (W - 1920 * s) / 2;
    if (barY > 2) eq(0, norm(W / 2, barY / 2)[1], "top bar");
    if (barX > 2) eq(0, norm(barX / 2, H / 2)[0], "left bar");
  });
  test("norm, fill: the picture overhangs, so the screen edge is inside the PC screen", () => {
    fit = "fill";
    const c = norm(0, 0);
    ok(c[0] > 0 || c[1] > 0, "a corner of the iPad is not the PC's corner: " + c);
  });
  test("norm: no picture yet, no input", () => {
    const keep = cfg; cfg = null;
    eq(null, norm(10, 10));
    cfg = keep;
  });

  // ---- input messages (decoded by cast/pc/RemoteInput.cs) ---------------------------------
  test("move: type 1, x and y big-endian", () => {
    sent = []; move([0x1234, 0xABCD]);
    eq([1, 0x12, 0x34, 0xAB, 0xCD], lastSent());
  });
  test("click: button down then up at the same point", () => {
    sent = []; click(1, [256, 512]);
    eq([[2, 1, 1, 1, 0, 2, 0], [2, 1, 0, 1, 0, 2, 0]], sent);
  });
  test("wheel: type 3, signed 16-bit vertical then horizontal", () => {
    sent = []; wheel(-120, 40);
    eq([3, 0xFF, 0x88, 0x00, 0x28], lastSent());
  });
  test("text: type 4 and UTF-8 (Lithuanian letters)", () => {
    sent = []; text("ą");
    eq([4, 0xC4, 0x85], lastSent());
  });
  test("keys: Enter, Backspace and the arrows map to Windows key codes", () => {
    eq([13, 8, 37, 40], [KEYS.Enter, KEYS.Backspace, KEYS.ArrowLeft, KEYS.ArrowDown]);
    sent = []; key(KEYS.Enter);
    eq([5, 13], lastSent());
  });
  test("pushed out by another device: back to the login card, no reconnecting", () => {
    const keep = [started, wantClose];
    started = true; wantClose = false;
    onText({ t: "replaced" });
    eq([false, true], [started, wantClose], "started, wantClose");
    eq(T.replaced, $("msg").textContent, "tells why");
    eq("none", $("stage").style.display, "the picture is hidden");
    $("msg").textContent = ""; $("login").style.display = ""; [started, wantClose] = keep;
  });
  test("nothing is sent while the connection is closed", () => {
    const keep = ws; ws = { readyState: 3, send() { throw new Error("sent on a closed socket"); } };
    move([1, 1]);
    ws = keep;
  });

  // ---- picture size --------------------------------------------------------------------
  test("applyFit: stretch / fit / fill become CSS object-fit fill / contain / cover", () => {
    const got = [];
    for (const f of ["stretch", "contain", "fill"]) { fit = f; applyFit(); got.push(video.style.objectFit); }
    eq(["fill", "contain", "cover"], got);
    eq(["stretch", "contain", "fill"], FITS, "the button's order");
  });

  // ---- "Smoother": the jitter buffer ------------------------------------------------------
  function frame(ms) { return { timestamp: ms * 1000, closed: false, close() { this.closed = true; } }; }
  test("jitter buffer: a frame waits about SMOOTH_MS", () => {
    jitterClear();
    jitterAdd(frame(0));
    const wait = jitterMs();
    ok(wait > SMOOTH_MS - 20 && wait <= SMOOTH_MS, "waits " + wait + " ms");
    jitterClear();
  });
  test("jitter buffer: frames keep their spacing (a burst is shown evenly)", () => {
    jitterClear();
    jitterAdd(frame(0)); jitterAdd(frame(16.7)); jitterAdd(frame(33.4));
    ok(Math.abs((jq[2].at - jq[0].at) - 33.4) < 1, "spacing " + (jq[2].at - jq[0].at));
    jitterClear();
  });
  test("jitter buffer: never more than 40 frames, the oldest are closed", () => {
    jitterClear();
    const fs = [];
    for (let i = 0; i < 45; i++) { const f = frame(i * 16.7); fs.push(f); jitterAdd(f); }
    eq(40, jq.length, "kept");
    eq(5, fs.filter(f => f.closed).length, "closed");
    jitterClear();
    eq(45, fs.filter(f => f.closed).length, "jitterClear closes the rest");
  });

  // ---- sound over the WebSocket: PCM into the ring buffer -----------------------------------
  test("playAudio: 16-bit stereo PCM into the ring, peak measured", () => {
    const keep = [audioCtx, ringNode, ringW, ringR0];
    audioCtx = { sampleRate: 48000 }; ringNode = {}; ringW = 0; ringR0 = 0; audioPeak = 0; ringPos = 0;
    const buf = new ArrayBuffer(1 + 4 * 4), dv = new DataView(buf, 1);
    [[16384, -16384], [0, 0], [-32768, 8192], [100, -100]].forEach((s, i) => { dv.setInt16(i * 4, s[0], true); dv.setInt16(i * 4 + 2, s[1], true); });
    playAudio(buf);
    eq(4, ringFill(), "samples in the ring");
    eq(0.5, ringL[0], "left 16384 = 0.5");
    eq(-0.5, ringR[0], "right -16384 = -0.5");
    eq(1, audioPeak, "peak -32768 = full scale");
    eq(Math.round(4 / 48), bufferedMs(), "buffered ms");
    [audioCtx, ringNode, ringW, ringR0] = keep;
  });

  // ---- texts and the illustrated setup guide -----------------------------------------------
  test("texts: the page title and every guide step exist", () => {
    ok(/CastLink/.test(T.title), "title " + T.title);
    for (const k of ["title", "intro", "s1", "s2", "s3", "s4", "s6", "note", "allow", "profile", "trust", "addHome"])
      ok(typeof G[k] === "string" && G[k].length > 0, "guide text " + k);
    ok(G.s5("pc:47900").indexOf("https://pc:47900/") >= 0, "step 5 links to https");
    ok(/SHA-256/.test(G.s3), "step 3 asks to compare the SHA-256");
  });
  test("guide pictures: five drawings, each an SVG with a highlight", () => {
    eq(["s1", "s2", "s3", "s4", "s6"], Object.keys(SHOTS));
    for (const k in SHOTS) {
      ok(SHOTS[k].indexOf("<svg") === 0, k + " is an SVG");
      ok(SHOTS[k].indexOf("stroke='#4f8cff'") > 0, k + " highlights what to tap");
    }
    ok(SHOTS.s4.indexOf("TV Cast local CA") > 0, "the certificate keeps its name");
  });
  test("guide: text from a page cannot inject markup into a drawing", () => {
    ok(txt(0, 0, "<b>x</b>").indexOf("&lt;b>") > 0, "escaped");
  });
  test("step(): number badge and the picture", () => {
    const h = step(3, "hello", "<svg/>");
    ok(h.indexOf("class='n'>3<") > 0 && h.indexOf("<svg/>") > 0, h);
  });

  // ---- login card --------------------------------------------------------------------------
  test("login: the app icon, the code box and the gesture list are on the page", () => {
    ok(document.querySelector(".login img[src='/icon.png']"), "icon");
    eq("numeric", $("code").getAttribute("inputmode"), "the iPad shows the number keyboard");
    eq((DESKTOP ? T.mouse : T.gestures).length * 2, $("t-hint").children.length, "gesture rows");
  });
  test("toolbar: four buttons, each an SVG icon", () => {
    const bs = document.querySelectorAll("#tools button");
    eq(4, bs.length);
    bs.forEach(b => ok(b.querySelector("svg"), b.id + " has an icon"));
  });

  // ---- a Mac or another computer -----------------------------------------------------------
  test("headless desktop browser: a computer, not an iPad", () => {
    eq([false, true], [IOS, DESKTOP]);
    ok(T.mouse.length === 5 && /⌘/.test(T.mouse[4][1]), "mouse and keyboard help");
  });
  test("physicalKey: editing keys by code, characters as text, shortcuts stay here", () => {
    eq({ vk: 13 }, physicalKey({ key: "Enter" }));
    eq({ vk: 9 }, physicalKey({ key: "Tab" }));
    eq({ text: "a" }, physicalKey({ key: "a" }));
    eq({ text: " " }, physicalKey({ key: " " }));
    eq({ text: "ą" }, physicalKey({ key: "ą", altKey: true }), "Option / AltGr letter");
    eq(null, physicalKey({ key: "c", metaKey: true }), "⌘C");
    eq(null, physicalKey({ key: "v", ctrlKey: true }), "Ctrl+V");
    eq(null, physicalKey({ key: "F5" }), "function keys");
    eq(null, physicalKey({ key: "e", isComposing: true }), "while composing an accented letter");
  });
  test("the keyboard types to the PC once connected, and only with control allowed", () => {
    const keep = [started, cfg];
    started = true; cfg = Object.assign({}, cfg || {}, { control: true });
    sent = [];
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "x", bubbles: true }));
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Backspace", bubbles: true }));
    eq([[4, 0x78], [5, 8]], sent);
    cfg.control = false; sent = [];
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "y", bubbles: true }));
    eq([], sent, "control off on the PC");
    [started, cfg] = keep;
  });
  test("Mac guide: Keychain Access steps, five drawings, the https link", () => {
    const h = buildGuide("mac", "pc:47900");
    ok(h.indexOf(M.title) >= 0 && /Keychain Access/.test(h) && /Always Trust/.test(h), "Keychain steps");
    ok(h.indexOf("https://pc:47900/") > 0, "https link");
    eq(5, (h.match(/<svg/g) || []).length, "drawings");
    eq(5, (h.match(/stroke='#4f8cff'/g) || []).length, "each shows where to click");
    ok(/SHA-256/.test(M.m3), "compare the SHA-256 on the Mac too");
  });
  test("iPad guide still six steps and five drawings", () => {
    const h = buildGuide("ios", "pc:47900");
    eq(6, (h.match(/class='step'/g) || []).length);
    eq(5, (h.match(/<svg/g) || []).length);
  });
  test("the ⌨ button is hidden on a computer", () => {
    onText({ t: "config", w: 1920, h: 1080, codec: "avc1.42C02A", rate: 48000, control: true });
    eq("none", $("b-kbd").style.display);
    stopWc(); stopMse();
  });

  const pre = document.createElement("pre");
  pre.id = "test-results";
  pre.textContent = out.join("\n") + "\n" + (failed ? "FAILED " + failed : "PASSED " + out.length);
  document.body.appendChild(pre);
})();
