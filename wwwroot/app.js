"use strict";

/* WyreMonitor dashboard. The page is a plan of the machine.
   Gold = how busy a part is. Colour = how close it is to its limit. */

const REDUCED_MOTION = matchMedia("(prefers-reduced-motion: reduce)").matches;
const LOCALE = undefined;   // numbers and times follow the Windows regional settings

// ---- Settings ------------------------------------------------------------------
// Kept in the window's browser profile, which belongs to the Windows account. Every value is
// checked against the few allowed ones, so a damaged entry falls back to the default.
const SETTINGS_KEY = "wyremonitor.settings";
const MAX_NAME = 40;
const MAX_ENTRIES = 300;
const LIMIT_KEYS = ["cpu", "gpu", "gpuHot", "dimm"];
const settings = loadSettings();   // after the constants it uses

function loadSettings() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(SETTINGS_KEY) ?? "{}") ?? {}; } catch { /* defaults */ }
  return {
    unit: saved.unit === "F" ? "F" : "C",
    rate: saved.rate === 2 ? 2 : 1,             // readings per second
    fans: saved.fans === "still" ? "still" : "spin",
    rail: saved.rail === true,                  // detail panel shown: closed until asked for
    chart: [300, 900].includes(saved.chart) ? saved.chart : 60,   // seconds shown in the chart
    view: saved.view === "list" ? "list" : "board",
    theme: ["black", "blue", "white"].includes(saved.theme) ? saved.theme : "green",
    size: ["small", "large"].includes(saved.size) ? saved.size : "normal",
    cb: saved.cb === true,                      // colours for colour blindness
    contrast: saved.contrast === true,
    compact: saved.compact === true,
    deco: saved.deco !== false,                 // copper traces, mounting holes, contacts
    anim: saved.anim !== false,
    names: cleanNames(saved.names),             // sensor id -> name chosen by the user
    hidden: cleanHidden(saved.hidden),          // part names and fan sensor ids left off the board
    alerts: cleanAlerts(saved.alerts),
  };
}

function cleanNames(value) {
  // No prototype: a sensor id can never collide with a built-in property.
  const names = Object.create(null);
  if (value && typeof value === "object" && !Array.isArray(value)) {
    for (const [id, name] of Object.entries(value).slice(0, MAX_ENTRIES)) {
      if (typeof name === "string" && name.trim() && id.length <= 256) names[id] = name.trim().slice(0, MAX_NAME);
    }
  }
  return names;
}

function cleanHidden(value) {
  if (!Array.isArray(value)) return [];
  return [...new Set(value.filter((k) => typeof k === "string" && k.length > 0 && k.length <= 256))].slice(0, MAX_ENTRIES);
}

function validLimit(l) {
  return !!l && Number.isFinite(l.warn) && Number.isFinite(l.crit) && l.warn >= 20 && l.crit <= 150 && l.warn < l.crit;
}

function cleanAlerts(value) {
  const a = value && typeof value === "object" ? value : {};
  const limits = {};
  for (const key of LIMIT_KEYS) {
    const l = a.limits?.[key];
    if (validLimit(l)) limits[key] = { warn: l.warn, crit: l.crit };
  }
  return { temps: a.temps !== false, fans: a.fans !== false, space: a.space !== false, wear: a.wear !== false, limits };
}

function saveSettings() {
  try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings)); } catch { /* kept for this session only */ }
  mirrorToApp();
}

// The icon next to the clock raises Windows notifications and shows the CPU temperature while
// the window is closed: it gets a copy of what it needs. Only inside the WyreMonitor window.
function mirrorToApp() {
  try {
    window.chrome?.webview?.postMessage({
      type: "settings", unit: settings.unit, names: settings.names, hidden: settings.hidden, alerts: settings.alerts,
      theme: settings.theme, palette: settings.cb ? "cb" : "standard",
    });
  } catch { /* not in the app window */ }
}

// ---- Limits -------------------------------------------------------------------
// Per part: the temperature that deserves attention (warn) and the critical one (crit).
const LIMITS = {
  cpuAmd: { warn: 90, crit: 95 },     // Ryzen and Threadripper throttle at 95 °C
  cpuIntel: { warn: 95, crit: 100 },  // Intel Core parts throttle at 100 °C
  gpu: { warn: 83, crit: 88 },
  gpuHot: { warn: 95, crit: 105 },
  dimm: { warn: 60, crit: 75 },
  board: { warn: 80, crit: 95 },
  nvme: { warn: 70, crit: 80 },       // replaced by the limits the drive reports, when present
  ssd: { warn: 60, crit: 70 },
  hdd: { warn: 50, crit: 55 },
};
// Free space only raises alerts for the drive Windows runs from.
const FREE_WARN = 0.05;   // under 5 % free
const FREE_CRIT = 0.01;   // under 1 % free
const LIFE_WARN = 20;     // SSD remaining life, %
const LIFE_CRIT = 10;

// ---- Formatting ---------------------------------------------------------------
const fixed = (v, digits = 0) =>
  new Intl.NumberFormat(LOCALE, { maximumFractionDigits: digits, minimumFractionDigits: digits }).format(v);
const upTo = (v, digits = 1) =>
  new Intl.NumberFormat(LOCALE, { maximumFractionDigits: digits }).format(v);

// Temperatures arrive and are compared in °C; only what is shown is converted.
const toUnit = (c) => (settings.unit === "F" ? c * 9 / 5 + 32 : c);
const degrees = () => (settings.unit === "F" ? "°F" : "°C");
const temp = (c, digits = 0) => `${digits ? upTo(toUnit(c), digits) : fixed(toUnit(c))} ${degrees()}`;

const formatBytes = (gb) => (gb >= 1000 ? `${upTo(gb / 1000, 2)} TB` : `${upTo(gb, gb < 100 ? 1 : 0)} GB`);

function formatRate(bytesPerSecond) {
  if (bytesPerSecond >= 1e9) return `${upTo(bytesPerSecond / 1e9, 2)} GB/s`;
  if (bytesPerSecond >= 1e6) return `${upTo(bytesPerSecond / 1e6, 1)} MB/s`;
  if (bytesPerSecond >= 1e3) return `${upTo(bytesPerSecond / 1e3, 0)} kB/s`;
  return `${upTo(bytesPerSecond, 0)} B/s`;
}

function formatValue(type, value, name = "") {
  switch (type) {
    case "Temperature": return temp(value, 1);
    case "Load":
    case "Level":
    case "Control": return `${upTo(value, value < 10 ? 1 : 0)} %`;
    case "Fan": return `${fixed(value)} RPM`;
    case "Power": return `${upTo(value, 1)} W`;
    case "Clock": return `${fixed(value)} MHz`;
    case "Voltage": return `${fixed(value, 2)} V`;
    case "Current": return `${upTo(value, 1)} A`;
    case "Data": return formatBytes(value);
    case "SmallData": return value >= 1024 ? `${upTo(value / 1024, 1)} GB` : `${fixed(value)} MB`;
    case "Throughput": return formatRate(value);
    case "Factor": return /^Power On/.test(name) ? fixed(value) : `${upTo(value, 2)} x`;
    case "Energy": return `${fixed(value)} mWh`;
    default: return upTo(value, 2);
  }
}

// Clearer names for the sensors whose library names are cryptic.
const SENSOR_NAMES = [
  [/^Fan #(\d+)$/, "Fan $1"],
  [/^Temperature #(\d+)$/, "Probe $1"],
  [/^Voltage #(\d+)$/, "Voltage $1"],
  [/^CPU Core #(\d+)$/, "Thread $1"],
  [/^CPU Total$/, "Total load"],
  [/^CPU Core Max$/, "Busiest thread"],
  [/^Cores \(Average\)$/, "Core average"],
  [/^Cores \(Average Effective\)$/, "Effective core average"],
  [/^Package$/, "Whole package"],
  [/^Life$/, "Remaining life"],
  [/^Memory Used$/, "Used"],
  [/^Memory Available$/, "Available"],
  [/^Memory$/, "Usage"],
  [/^Core \(Tctl\/Tdie\)$/, "CPU temperature"],
  [/^Core \(Tctl\)$/, "CPU temperature"],
  [/^CPU Package$/, "CPU temperature"],
  [/^CCD(\d+) \(Tdie\)$/, "Chiplet CCD$1"],
  [/^CCDs Max \(Tdie\)$/, "Hottest chiplet"],
  [/^CCDs Average \(Tdie\)$/, "Chiplet average"],
  [/^Core \(SVI2 TFN\)$/, "Core voltage"],
  [/^SoC \(SVI2 TFN\)$/, "SoC voltage"],
  [/^Bus Speed$/, "Bus clock"],
  [/^GPU Core$/, "GPU"],
  [/^GPU Hot Spot$/, "Hot spot"],
  [/^GPU Memory Junction$/, "Memory junction"],
];

function sensorLabel(name) {
  for (const [pattern, label] of SENSOR_NAMES) {
    if (pattern.test(name)) return name.replace(pattern, label);
  }
  return name;
}

const TYPE_ORDER = ["Temperature", "Load", "Fan", "Control", "Power", "Clock", "Voltage", "Current",
  "Level", "Data", "SmallData", "Throughput", "Energy", "Factor", "Timing"];
const TYPE_TITLES = {
  Temperature: "Temperatures", Load: "Load", Fan: "Fans", Control: "Fan control",
  Power: "Power", Clock: "Clocks", Voltage: "Voltages", Current: "Current", Level: "Levels",
  Data: "Data", SmallData: "Data", Throughput: "Throughput", Energy: "Energy",
  Factor: "Counters and multipliers", Timing: "Memory timings",
};

// Limits and settings reported as temperatures, not measurements.
const NOT_A_READING = /Resolution|Limit|^Warning Temperature$|^Critical Temperature$/;
const isRealTemp = (s) => s.type === "Temperature" && !NOT_A_READING.test(s.name);

// Readings only someone digging for detail needs: hidden behind "Show N more sensors".
function isSecondary(s) {
  return s.type === "Timing"
    || /^CPU Core #\d+/.test(s.name)
    || /^Core #\d+/.test(s.name)
    || /^D3D /.test(s.name)
    || NOT_A_READING.test(s.name);
}

// ---- Heat colour ----------------------------------------------------------------
// Depends on how close a reading is to the limit of THAT part, not on absolute degrees:
// 45 °C is fine for a CPU and a lot for a hard disk.
// Standard: cool blue, warm white, orange, red: no stop near the gold that means load. For colour blindness (Okabe-Ito): sky, yellow, orange, purple,
// whose lightness steps stay apart for red-green deficiencies too.
const RAMPS = {
  standard: [[0.40, [125, 184, 196]], [0.70, [222, 222, 200]], [1.00, [255, 138, 61]], [1.15, [255, 92, 51]]],
  cb: [[0.40, [86, 180, 233]], [0.70, [240, 228, 66]], [1.00, [230, 159, 0]], [1.15, [204, 121, 167]]],
};

// The heat colour for a reading, or "" (the plain text colour) while it is comfortably below its
// warning threshold: colour appears only when it means something.
const HUE_FROM = 0.7;
function hue(temp, warn) { return temp / warn >= HUE_FROM ? heat(temp, warn) : ""; }

function heat(temp, warn) {
  const RAMP = RAMPS[settings.cb ? "cb" : "standard"];
  const r = temp / warn;
  if (r <= RAMP[0][0]) return `rgb(${RAMP[0][1].join(",")})`;
  for (let i = 1; i < RAMP.length; i++) {
    if (r <= RAMP[i][0]) {
      const [p0, c0] = RAMP[i - 1];
      const [p1, c1] = RAMP[i];
      const t = (r - p0) / (p1 - p0);
      return `rgb(${c0.map((c, k) => Math.round(c + (c1[k] - c) * t)).join(",")})`;
    }
  }
  return `rgb(${RAMP[RAMP.length - 1][1].join(",")})`;
}

// ---- Reading the hardware --------------------------------------------------------
const find = (group, type, pattern) => group?.sensors.find((s) => s.type === type && pattern.test(s.name));
const lastSegment = (name) => name.split(" › ").pop();

// The user's limits when set (Settings > Alerts), otherwise the part's own.
function defaultLimit(key, cpu) {
  if (key === "cpu") return /intel/i.test(cpu?.name ?? "") ? LIMITS.cpuIntel : LIMITS.cpuAmd;
  return LIMITS[key];
}
const limit = (key, cpu) => settings.alerts.limits[key] ?? defaultLimit(key, cpu);
const cpuLimits = (cpu) => limit("cpu", cpu);

// Names chosen by the user win over the library's.
const displayName = (s) => settings.names[s.id] ?? sensorLabel(s.name);
const fanName = (f) => settings.names[f.sensor.id] ?? `Fan ${f.label}`;
const shown = (key) => !settings.hidden.includes(key);

function shortCpuName(name) {
  return name.replace(/^AMD /, "").replace(/^Intel\(R\) Core\(TM\) /, "Core ").replace(/^Intel /, "")
    .replace(/\(R\)|\(TM\)/g, "").replace(/ CPU @.*$/, "").replace(/ \d+-Core Processor$/, "").trim();
}

const shortGpuName = (name) => name.replace(/^(NVIDIA|AMD|Intel\(R\)) /, "").replace(/^GeForce /, "")
  .replace(/^Radeon\(TM\) /, "Radeon ").replace(/\(R\)|\(TM\)/g, "").trim();

const shortDriveName = (name) => name.replace(/^Samsung SSD /, "").replace(/^WDC (WD\w+)-\w+$/, "$1");

// CCD temperatures: present on Ryzen and Threadripper, one per chiplet. The active chiplets
// are not always numbered in sequence (a 3960X reports CCD1, CCD3, CCD5 and CCD7).
function ccdSensors(cpu) {
  return cpu.sensors
    .map((s) => ({ s, m: s.type === "Temperature" && s.name.match(/^CCD ?#?(\d+)(?: \(Tdie\))?$/) }))
    .filter((x) => x.m)
    .map((x) => ({ ccd: +x.m[1], sensor: x.s }))
    .sort((a, b) => a.ccd - b.ccd);
}

// The CPU drawn as its chiplets (dies) holding cores, each core holding its threads.
function cpuLayout(cpu) {
  const loads = cpu.sensors
    .map((s) => ({ s, m: s.type === "Load" && s.name.match(/^CPU Core #(\d+)(?: Thread #(\d+))?$/) }))
    .filter((x) => x.m);

  // "CPU Core #3 Thread #2" names the core: group threads by it. Plain "CPU Core #7" is
  // one entry per logical processor, and which core it belongs to is not reported.
  const byCore = new Map();
  for (const { s, m } of loads) {
    const key = m[2] ? +m[1] : `t${m[1]}`;
    if (!byCore.has(key)) byCore.set(key, { order: +m[1], threads: [] });
    byCore.get(key).threads.push({ id: s.id, thread: m[2] ? +m[2] : 1 });
  }
  const cores = [...byCore.values()].sort((a, b) => a.order - b.order);
  cores.forEach((c) => c.threads.sort((a, b) => a.thread - b.thread));

  // Cores are split evenly across the chiplets, in order. This matches how Windows
  // numbers processors on Ryzen, but the library does not report it.
  const ccds = ccdSensors(cpu);
  if (ccds.length > 1 && cores.length % ccds.length === 0) {
    const per = cores.length / ccds.length;
    return ccds.map((c, i) => ({ label: `CCD${c.ccd}`, temp: c.sensor.id, cores: cores.slice(i * per, (i + 1) * per) }));
  }
  return [{ label: ccds.length === 1 ? `CCD${ccds[0].ccd}` : "Cores", temp: ccds[0]?.sensor.id ?? null, cores }];
}

function gpuReadings(g) {
  const temps = g.sensors.filter(isRealTemp);
  const pick = (re) => temps.find((s) => re.test(s.name));
  const used = find(g, "SmallData", /^GPU Memory Used$/) ?? find(g, "SmallData", /^D3D Dedicated Memory Used$/);
  return {
    temp: (pick(/^GPU Core$/) ?? pick(/^GPU$|Edge/) ?? temps.find((s) => !/Hot Spot|Memory/.test(s.name)))?.value ?? null,
    hot: pick(/Hot Spot/)?.value ?? null,
    load: (find(g, "Load", /^GPU Core$/) ?? find(g, "Load", /^D3D 3D$/))?.value ?? null,
    power: (find(g, "Power", /Package|Total|Board/) ?? g.sensors.find((s) => s.type === "Power"))?.value ?? null,
    vramUsed: used?.value ?? null,
    vramTotal: find(g, "SmallData", /^GPU Memory Total$/)?.value ?? null,
    fans: g.sensors.filter((s) => s.type === "Fan"),
  };
}

function readModel(snapshot) {
  const hw = snapshot.hardware;
  const drives = [];
  let hiddenReaders = 0;

  for (const g of hw.filter((h) => h.type === "Storage")) {
    const total = find(g, "Data", /^Total Space$/);
    if (!total || total.value === 0) { hiddenReaders++; continue; }
    const temps = g.sensors.filter(isRealTemp);
    const life = find(g, "Level", /^Life$/);
    const kind = temps.length > 1 || /nvme/i.test(g.name) ? "nvme" : life ? "ssd" : "hdd";
    drives.push({
      group: g,
      kind,
      short: shortDriveName(g.name),
      isSystem: !!snapshot.systemDrive && g.name.trim().toLowerCase() === snapshot.systemDrive.trim().toLowerCase(),
      temp: find(g, "Temperature", /^Temperature$/) ?? temps[0],
      total,
      free: find(g, "Data", /^Free Space$/),
      used: find(g, "Load", /^Used Space$/),
      life,
      limit: {
        warn: find(g, "Temperature", /^Warning Temperature$/)?.value || LIMITS[kind].warn,
        crit: find(g, "Temperature", /^Critical Temperature$/)?.value || LIMITS[kind].crit,
      },
    });
  }
  // M.2 first, then SATA SSDs, then hard disks; larger first within each kind.
  const order = { nvme: 0, ssd: 1, hdd: 2 };
  drives.sort((a, b) => order[a.kind] - order[b.kind] || b.total.value - a.total.value);

  const cpu = hw.find((h) => h.type === "Cpu");
  // Discrete cards first: on laptops the integrated GPU is listed too.
  const gpuRank = (t) => (t === "GpuNvidia" || t === "GpuAmd" ? 0 : 1);
  const gpus = hw.filter((h) => h.type.startsWith("Gpu")).sort((a, b) => gpuRank(a.type) - gpuRank(b.type));
  const memory = hw.find((h) => h.type === "Memory" && h.name === "Total Memory")
    ?? hw.find((h) => h.type === "Memory" && !/Virtual|\(#\d+\)$/.test(h.name));
  const dimms = hw.filter((h) => h.type === "Memory" && /\(#\d+\)$/.test(h.name));
  const boards = hw.filter((h) => (h.type === "SuperIO" || h.type === "EmbeddedController")
    && h.sensors.some((s) => isRealTemp(s) || s.type === "Fan"));
  const battery = hw.find((h) => h.type === "Battery");

  // Fans by controller: A = first chip, B = second, and so on. Labels are given before parts
  // are hidden, so a fan keeps its label whatever else is hidden.
  const fans = [];
  boards.forEach((g, c) => {
    g.sensors.filter((s) => s.type === "Fan").forEach((s, i) => {
      fans.push({ group: g, sensor: s, label: `${String.fromCharCode(65 + c)}${i + 1}` });
    });
  });

  // Parts the user hid are left off the board and raise no alerts.
  const ids = new Set(hw.flatMap((h) => [h.name, ...h.sensors.map((x) => x.id)]));
  const show = (g) => (g && shown(g.name) ? g : undefined);
  return {
    snapshot,
    cpu: show(cpu),
    gpus: gpus.filter(show),
    memory: show(memory),
    dimms: dimms.filter(show),
    boards: boards.filter(show),
    battery: show(battery),
    fans: fans.filter((f) => shown(f.sensor.id)),
    drives: drives.filter((d) => shown(d.group.name)),
    hiddenReaders,
    hiddenParts: settings.hidden.filter((k) => ids.has(k)).length,
  };
}

// Whether a hardware group can be chosen on the board as it is drawn now.
function onBoard(m, name) {
  return [m.cpu, m.memory, m.battery, ...m.gpus, ...m.dimms, ...m.boards, ...m.drives.map((d) => d.group)]
    .some((g) => g?.name === name) || m.fans.some((f) => f.group.name === name);
}

// ---- State ------------------------------------------------------------------------
const state = {
  model: null,
  signature: "",
  selected: null,      // name of the hardware group shown on the right
  chartId: null,       // id of the sensor in the chart
  showSecondary: false,
  lastOk: 0,
  failed: false,
  sensorRows: new Map(),
  verdictKey: "",
  tracesDrawn: false,
  renaming: false,     // the detail panel shows name fields instead of readings
  listRows: new Map(),  // sensor id -> cells of the list view
  listKey: "",          // what the list was built from
  longHistory: null,   // { id, values }: the charted sensor over more than a minute
  hotNets: new Set(),  // trace nets reaching a part with an alert
  level: "off",        // the verdict's state: ok, warn, crit, or off (loading, no data)
  code: "",            // the debug display: the code of the part that needs checking
  announced: "",       // what the screen-reader live region last said (the state, not the values)
  spun: new Set(),     // fan headers seen turning since the window opened
  incident: null,      // the alert in progress: { label, peak, kind, start }
  recovered: null,     // the last one that cleared, shown for a while: { ..., end }
};

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
function setText(el, text) { if (el && el.textContent !== text) el.textContent = text; }

// ---- Building the board -----------------------------------------------------------
const ROTOR = `<span class="rotor" aria-hidden="true"><svg viewBox="0 0 40 40">
  <circle cx="20" cy="20" r="19" fill="none" stroke="currentColor" stroke-width="1"/>
  <g class="blades" fill="currentColor">
    <path d="M20 20C17 13 18 6 24 3c3 5 1 12-4 17z"/>
    <path d="M20 20c7-3 14-2 17 4-5 3-12 1-17-4z"/>
    <path d="M20 20c3 7 2 14-4 17-3-5-1-12 4-17z"/>
    <path d="M20 20c-7 3-14 2-17-4 5-3 12-1 17 4z"/>
  </g>
  <circle cx="20" cy="20" r="4" fill="#141a1a" stroke="currentColor"/>
</svg><img class="spin" alt="" /></span>`;

// A spinning fan shows an animated image turning slowly, at a medium pace or fast, by its RPM;
// a stopped one shows the still icon. An image redraws only its own few pixels: a CSS or script
// animation made the whole window redraw 60 times a second and tripled the app's CPU use.
const FAN_STEPS = [900, 1600];   // RPM from which the icon turns at medium pace, then fast
const FAN_IMAGES = ["/fan-slow.png", "/fan-mid.png", "/fan-fast.png"];

function fanSpeed(rpm, current) {
  // 50 RPM of hysteresis: a fan hovering on a step doesn't make its icon flicker between paces.
  return FAN_STEPS.filter((step, i) => rpm >= step + (current > i ? -50 : 50)).length;
}

function setFanIcon(holder, rpm) {
  const rotor = holder?.querySelector(".rotor");
  if (!rotor) return;
  holder.classList.toggle("off", rpm <= 0);
  const spin = rpm > 0 && settings.fans === "spin" && !REDUCED_MOTION;
  const current = rotor.dataset.speed ? +rotor.dataset.speed : -1;
  const speed = spin ? String(fanSpeed(rpm, current)) : "";
  if (rotor.dataset.speed === speed) return;
  rotor.dataset.speed = speed;
  rotor.classList.toggle("spinning", spin);
  const img = rotor.querySelector(".spin");
  if (spin) img.src = FAN_IMAGES[+speed];
  else img.removeAttribute("src");
}

function buildDie(die) {
  // Cores fill the die in one, two or three rows, depending on how many there are.
  const rows = die.cores.length <= 8 ? 1 : die.cores.length <= 32 ? 2 : 3;
  const columns = Math.max(1, Math.ceil(die.cores.length / rows));
  const cores = die.cores.map((c, i) => `<span class="core" title="${esc(die.label)} core ${i + 1}">${
    c.threads.map((t) => `<span class="th"><i data-load="${esc(t.id)}"></i></span>`).join("")}</span>`).join("");
  return `<span class="ccd" ${die.temp ? `data-dietemp="${esc(die.temp)}"` : ""}>
    <span class="ccd-head"><span>${esc(die.label)}</span><b data-dielabel></b></span>
    <span class="cores" style="--cols: ${columns}">${cores}</span>
  </span>`;
}

function buildCpu(m) {
  if (!m.cpu) return "";
  const dies = cpuLayout(m.cpu);
  const info = `<span class="iod">
      <span class="designator">${esc(shortCpuName(m.cpu.name))}</span>
      <span class="big" id="cpuTemp"></span>
      <span class="stat"><b id="cpuLoad"></b> load</span>
      <span class="stat"><b id="cpuPower"></b></span>
      <span class="stat"><b id="cpuClock"></b></span>
    </span>`;
  // The socket's retention frame: Threadripper and EPYC sockets close with three numbered screws.
  const screws = /threadripper|epyc/i.test(m.cpu.name)
    ? `<i class="screw" data-n="1" aria-hidden="true"></i><i class="screw" data-n="2" aria-hidden="true"></i><i class="screw" data-n="3" aria-hidden="true"></i>`
    : "";
  if (dies.length > 1) {
    // Chiplets around the I/O die, as they sit under the heat spreader.
    const half = Math.ceil(dies.length / 2);
    return `<button type="button" class="part cpu court" data-ref="U1 · CPU" data-sel="${esc(m.cpu.name)}" aria-label="${esc(shortCpuName(m.cpu.name))}">${screws}
      <span class="die-col">${dies.slice(0, half).map(buildDie).join("")}</span>
      ${info}
      <span class="die-col">${dies.slice(half).map(buildDie).join("")}</span>
    </button>`;
  }
  return `<button type="button" class="part cpu single court" data-ref="U1 · CPU" data-sel="${esc(m.cpu.name)}" aria-label="${esc(shortCpuName(m.cpu.name))}">${screws}
    ${info}<span class="die-col">${buildDie(dies[0])}</span>
  </button>`;
}

function buildGpu(g) {
  const r = gpuReadings(g);
  const cell = (label, key, meter, full = false) => `<span class="stat-cell"><span class="designator">${label}</span><b data-gpu="${key}"></b>${
    meter ? `<span class="meter${full ? " full" : ""}"><i data-gpubar="${key}"></i></span>` : ""}</span>`;
  return `<button type="button" class="part gpu" data-sel="${esc(g.name)}">
    ${r.fans.length ? `<span class="gpu-fans">${r.fans.map((s) => `<span data-gpufan="${esc(s.id)}">${ROTOR}</span>`).join("")}</span>` : ""}
    <span>
      <span class="designator">${esc(shortGpuName(g.name))}</span>
      <span class="big" data-gpu="temp"></span>
    </span>
    <span class="gpu-stats">
      ${r.load !== null ? cell("Load", "load", true) : ""}
      ${r.vramUsed !== null ? cell("Video memory", "vram", r.vramTotal !== null, true) : ""}
      ${r.power !== null ? cell("Power", "power") : ""}
      ${r.hot !== null ? cell("Hot spot", "hot") : ""}
    </span>
  </button>`;
}

function buildDimm(g) {
  const n = g.name.match(/\(#(\d+)\)$/)[1];
  // Eight memory chips on the stick: they take the module's heat colour.
  return `<button type="button" class="part dimm" data-sel="${esc(g.name)}" data-dimm="${esc(g.name)}" title="${esc(g.name)}">
    <span class="temp"></span><span class="chips" aria-hidden="true">${"<i></i>".repeat(8)}</span><span class="designator">DIMM${n}</span>
  </button>`;
}

function buildDrive(d, i) {
  return `<button type="button" class="part drive ${d.kind === "nvme" ? "m2" : "sata"}" data-sel="${esc(d.group.name)}" data-drive="${i}" title="${esc(d.group.name)}">
    <span class="drive-head"><span class="drive-name">${esc(d.short)}</span><span class="drive-temp"></span></span>
    <span class="meter full"><i></i></span>
    <span class="drive-note"></span>
  </button>`;
}

// Temperature probes on the motherboard, read by its Super I/O chip: named in plain words, the
// chip's model only in the tooltip and the detail panel. Two chips: A and B, as their fan banks.
function boardChip(g, i, all) {
  const chip = lastSegment(g.name).split("/")[0].replace(/^ITE /, "").replace(/^Nuvoton /, "");
  return `<button type="button" class="part chip grow sio" data-sel="${esc(g.name)}" data-probe="${esc(g.name)}" title="Temperature probes read by the ${esc(chip)} chip">
    <span class="designator">Board probes${all.length > 1 ? ` ${String.fromCharCode(65 + i)}` : ""}</span>
    <span class="big" data-hot></span>
    <span class="note" data-probecount></span>
    <span class="probes" data-probes></span>
  </button>`;
}

// The board's grid depends on which parts this machine has: absent parts take no space.
function boardLayout(m) {
  const hasDimms = m.dimms.length > 0;
  const rows = [["brand"]];
  if (m.fans.length) rows.push(["fans"]);
  rows.push(hasDimms ? ["dl", "cpu", "dr", "side"] : ["cpu", "side"]);
  if (m.gpus.length) rows.push(hasDimms ? ["gpu", "gpu", "gpu", "side"] : ["gpu", "side"]);
  if (m.drives.length) rows.push(["store"]);

  const width = hasDimms ? 4 : 2;
  const desktop = rows.map((r) => `"${(r.length === 1 ? Array(width).fill(r[0]) : r).join(" ")}"`).join(" ");
  const mobile = [["brand", "brand"], m.fans.length && ["fans", "fans"], ["cpu", "cpu"], hasDimms && ["dl", "dr"],
    m.gpus.length && ["gpu", "gpu"], ["side", "side"], m.drives.length && ["store", "store"]]
    .filter(Boolean).map((r) => `"${r.join(" ")}"`).join(" ");
  return {
    areas: desktop,
    columns: hasDimms ? "var(--dimm) minmax(0, 1fr) var(--dimm) 190px" : "minmax(0, 1fr) 210px",
    mobile,
  };
}

function buildBoard(m) {
  const half = Math.ceil(m.dimms.length / 2);
  const motherboard = m.snapshot.hardware.find((h) => h.type === "Motherboard");
  const nvme = m.drives.map((d, i) => [d, i]).filter(([d]) => d.kind === "nvme");
  const sata = m.drives.map((d, i) => [d, i]).filter(([d]) => d.kind !== "nvme");

  const board = $("board");
  const layout = boardLayout(m);
  board.style.setProperty("--areas", layout.areas);
  board.style.setProperty("--columns", layout.columns);
  board.style.setProperty("--areas-mobile", layout.mobile);

  board.innerHTML = `
    <svg class="traces" id="traces" aria-hidden="true"></svg>
    <p class="brand"><span>${esc(motherboard?.name ?? m.snapshot.machineName ?? "This PC")}</span>
      <span class="qcode" aria-hidden="true"><i class="led"></i><b id="qcode"></b><small>DEBUG</small></span></p>
    <span class="maker" aria-hidden="true">WYREMONITOR</span>
    ${m.fans.length ? `<div class="fans">${fanBanks(m).map((bank) => `
      <div class="bank court" data-ref="${esc(bank.ref)}" style="--n: ${bank.fans.length}">${bank.fans.map((f) => `
      <button type="button" class="part fan" data-sel="${esc(f.group.name)}" data-chart="${esc(f.sensor.id)}" data-fan="${esc(f.sensor.id)}" title="${esc(lastSegment(f.group.name))}, ${esc(f.sensor.name)}">
        ${ROTOR}<span><span class="designator" title="Fan ${esc(f.label)}">${esc(fanName(f))}</span><span class="val"></span></span>
      </button>`).join("")}</div>`).join("")}</div>` : ""}
    ${m.dimms.length ? `<div class="dimms l court" data-ref="DRAM">${m.dimms.slice(0, half).map(buildDimm).join("")}</div>` : ""}
    ${buildCpu(m)}
    ${m.dimms.length ? `<div class="dimms r court" data-ref="DRAM">${m.dimms.slice(half).map(buildDimm).join("")}</div>` : ""}
    ${m.gpus.length ? `<div class="gpus court" data-ref="${m.gpus.some((g) => g.type !== "GpuIntel") ? "PCIE · GRAPHICS" : "GRAPHICS"}">${m.gpus.map(buildGpu).join("")}</div>` : ""}
    <div class="side court" data-ref="MEMORY · PROBES">
      ${m.memory ? `<button type="button" class="part chip" data-sel="${esc(m.memory.name)}">
        <span class="designator">Memory</span>
        <span class="big" id="ramLoad"></span>
        <span class="meter full"><i id="ramBar"></i></span>
        <span class="note" id="ramNote"></span>
      </button>` : ""}
      ${m.battery ? `<button type="button" class="part chip" data-sel="${esc(m.battery.name)}">
        <span class="designator">Battery</span>
        <span class="big" id="batteryLevel"></span>
        <span class="meter full"><i id="batteryBar"></i></span>
        <span class="note" id="batteryNote"></span>
      </button>` : ""}
      ${m.boards.filter((g) => g.sensors.some(isRealTemp)).map(boardChip).join("")}
    </div>
    ${m.drives.length ? `<div class="store">
      ${nvme.length ? `<div class="store-row m2-row court" data-ref="M.2">${nvme.map(([d, i]) => buildDrive(d, i)).join("")}</div>` : ""}
      ${sata.length ? `<div class="store-row sata-row court" data-ref="SATA">${sata.map(([d, i]) => buildDrive(d, i)).join("")}</div>` : ""}
    </div>` : ""}`;

  paintQcode();
  drawTraces();
}

// The board's debug display mirrors the verdict: "OK", or the number of things to check.
function paintQcode() {
  setText($("qcode"), { ok: "OK", warn: state.code, crit: state.code }[state.level] ?? "--");
}

// Fan headers by controller (A = first chip, B = second), labelled as on the board.
function fanBanks(m) {
  const banks = [];
  for (const f of m.fans) {
    let bank = banks.find((b) => b.group === f.group);
    if (!bank) {
      const chip = lastSegment(f.group.name).split("/")[0].replace(/^ITE /, "").replace(/^Nuvoton /, "");
      bank = { group: f.group, ref: `FAN_${f.label[0]} · ${chip}`, fans: [] };
      banks.push(bank);
    }
    bank.fans.push(f);
  }
  return banks;
}

// Copper traces: link the CPU to what is really wired to it. Each group of traces is a net that
// takes the alert colour while one of the parts it reaches raises an alert.
function drawTraces() {
  const board = $("board");
  const svg = $("traces");
  if (!svg || getComputedStyle(svg).display === "none") return;
  const origin = board.getBoundingClientRect();
  const z = origin.width / board.offsetWidth || 1;   // the page's size setting (CSS zoom)
  const box = (el) => {
    const r = el.getBoundingClientRect();
    return { l: (r.left - origin.left) / z, r: (r.right - origin.left) / z, t: (r.top - origin.top) / z, b: (r.bottom - origin.top) / z };
  };
  const cpuEl = board.querySelector(".cpu");
  if (!cpuEl) { svg.innerHTML = ""; return; }

  const cpu = box(cpuEl);
  const nets = {};   // net name -> path data
  const add = (net, d) => (nets[net] ??= []).push(d);

  // Memory channels, towards the modules on both sides.
  for (const el of board.querySelectorAll(".dimms")) {
    const d = box(el);
    const fromLeft = d.r <= cpu.l;
    const x1 = fromLeft ? d.r : cpu.r;
    const x2 = fromLeft ? cpu.l : d.l;
    for (let i = 0; i < 6; i++) add(fromLeft ? "dimms-l" : "dimms-r", `M${x1} ${cpu.t + (cpu.b - cpu.t) * (0.2 + i * 0.12)}H${x2}`);
  }
  // PCIe lanes towards the graphics card.
  const gpuEl = board.querySelector(".gpu");
  if (gpuEl) {
    const g = box(gpuEl);
    const mid = (cpu.l + cpu.r) / 2;
    for (let i = -5; i <= 5; i++) add("gpu", `M${mid + i * 12} ${cpu.b}V${g.t}`);
  }
  // Towards the side column and the drives: a 45° bend where the column steps down, as boards route.
  const sideEl = board.querySelector(".side");
  const storeEl = board.querySelector(".store");
  if (sideEl) {
    const s = box(sideEl);
    for (let i = 0; i < 3; i++) add("side", `M${cpu.r} ${cpu.t + 22 + i * 8}H${s.l}`);
    if (storeEl) {
      const st = box(storeEl);
      const x0 = (s.l + s.r) / 2;
      for (let i = -3; i <= 3; i++) add("store", `M${x0 + i * 12} ${s.b}V${st.t}`);
    }
  }

  // Each trace ends on a via: a small plated ring.
  const vias = (d) => {
    const m = d.match(/^M([\d.-]+) ([\d.-]+)([HV])([\d.-]+)$/);
    if (!m) return "";
    const [x, y] = [+m[1], +m[2]];
    const [x2, y2] = m[3] === "H" ? [+m[4], y] : [x, +m[4]];
    return `<circle cx="${x}" cy="${y}" r="2.4"/><circle cx="${x2}" cy="${y2}" r="2.4"/>`;
  };
  svg.innerHTML = Object.entries(nets).map(([net, list]) =>
    `<g data-net="${net}">${list.map((d) => `<path d="${d}" pathLength="1"/>`).join("")}${list.map(vias).join("")}</g>`).join("");
  paintNets();

  // The page's single moment of motion: the traces light up on first load.
  if (!state.tracesDrawn && motionOn()) {
    for (const p of svg.querySelectorAll("path")) {
      p.animate([{ strokeDasharray: "1", strokeDashoffset: 1 }, { strokeDasharray: "1", strokeDashoffset: 0 }],
        { duration: 700, easing: "ease-out" });
    }
  }
  state.tracesDrawn = true;
}

// The nets reaching a part with an alert take the alert colour (state.hotNets, set by updateBoard).
function paintNets() {
  for (const g of document.querySelectorAll("#traces g[data-net]")) g.classList.toggle("hot", state.hotNets.has(g.dataset.net));
}

function netsFor(m, alerts) {
  const hot = new Set();
  const half = Math.ceil(m.dimms.length / 2);
  for (const a of alerts) {
    const dimm = m.dimms.findIndex((g) => g.name === a.target);
    if (dimm >= 0) hot.add(dimm < half ? "dimms-l" : "dimms-r");
    else if (m.gpus.some((g) => g.name === a.target)) hot.add("gpu");
    else if (m.drives.some((d) => d.group.name === a.target)) hot.add("store");
    else if (m.boards.some((g) => g.name === a.target) || m.memory?.name === a.target) hot.add("side");
  }
  return hot;
}

// ---- Updating values (without rebuilding the DOM) ----------------------------------
function valueHtml(el, text, unit, color) {
  if (!el) return;
  const key = `${text}|${unit}`;
  if (el.dataset.key !== key) {
    el.dataset.key = key;
    el.innerHTML = text === null ? `<span class="nd">n/a</span>` : `${esc(text)}<span class="unit">${esc(unit)}</span>`;
  }
  el.style.color = text === null ? "" : (color ?? "");
}

function name(el, text) {
  if (el && el.getAttribute("aria-label") !== text) el.setAttribute("aria-label", text);
}

function bar(el, fraction) {
  if (el) el.style.transform = `scaleX(${Math.max(0, Math.min(1, fraction))})`;
}

function updateBoard(m) {
  const s = m.snapshot.summary;
  const board = $("board");
  const byId = new Map(m.snapshot.hardware.flatMap((h) => h.sensors.map((x) => [x.id, x])));

  if (m.cpu) {
    const limit = cpuLimits(m.cpu);
    valueHtml($("cpuTemp"), s.cpuTemp == null ? null : fixed(toUnit(s.cpuTemp)), degrees(), s.cpuTemp == null ? null : hue(s.cpuTemp, limit.warn));
    setText($("cpuLoad"), s.cpuLoad == null ? "n/a" : `${fixed(s.cpuLoad)} %`);
    setText($("cpuPower"), s.cpuPower == null ? "n/a" : `${fixed(s.cpuPower)} W`);
    const avg = find(m.cpu, "Clock", /^Cores \(Average\)$/)?.value ?? s.cpuClock;
    setText($("cpuClock"), avg ? `${upTo(avg / 1000, 2)} GHz` : "n/a");

    for (const el of board.querySelectorAll("[data-load]")) {
      const t = byId.get(el.dataset.load);
      el.style.transform = `scaleY(${t ? Math.min(1, t.value / 100) : 0})`;
    }
    for (const el of board.querySelectorAll("[data-dietemp]")) {
      const t = byId.get(el.dataset.dietemp);
      const label = el.querySelector("[data-dielabel]");
      if (t && t.value > 0) {
        const c = hue(t.value, limit.warn);
        setText(label, temp(t.value));
        label.style.color = c;
        el.style.background = c ? `color-mix(in srgb, ${c} 16%, var(--epoxy-up))` : "";
      } else {
        setText(label, "");
      }
    }
  }

  for (const g of m.gpus) {
    const el = board.querySelector(`.gpu[data-sel="${CSS.escape(g.name)}"]`);
    if (!el) continue;
    const r = gpuReadings(g);
    const q = (key) => el.querySelector(`[data-gpu="${key}"]`);
    valueHtml(q("temp"), r.temp == null ? null : fixed(toUnit(r.temp)), degrees(), r.temp == null ? null : hue(r.temp, limit("gpu").warn));
    setText(q("load"), r.load == null ? "n/a" : `${fixed(r.load)} %`);
    bar(el.querySelector('[data-gpubar="load"]'), (r.load ?? 0) / 100);
    setText(q("vram"), r.vramUsed == null ? "n/a"
      : r.vramTotal ? `${upTo(r.vramUsed / 1024, 1)} / ${upTo(r.vramTotal / 1024, 1)} GB` : `${upTo(r.vramUsed / 1024, 1)} GB`);
    bar(el.querySelector('[data-gpubar="vram"]'), r.vramUsed != null && r.vramTotal ? r.vramUsed / r.vramTotal : 0);
    setText(q("power"), r.power == null ? "n/a" : `${fixed(r.power)} W`);
    const hotEl = q("hot");
    setText(hotEl, r.hot == null ? "n/a" : temp(r.hot));
    if (hotEl && r.hot != null) hotEl.style.color = hue(r.hot, limit("gpuHot").warn);
    for (const f of r.fans) setFanIcon(el.querySelector(`[data-gpufan="${CSS.escape(f.id)}"]`), f.value);
  }

  for (const f of m.fans) {
    const el = board.querySelector(`[data-fan="${CSS.escape(f.sensor.id)}"]`);
    if (!el) continue;
    const on = f.sensor.value > 0;
    if (f.sensor.value > 200 || (f.sensor.history ?? []).some((v) => v > 200)) state.spun.add(f.sensor.id);
    // A header that has not turned since the window opened is an empty socket: drawn as its pins,
    // quiet. "Stopped" is kept for a fan that was turning.
    const spun = state.spun.has(f.sensor.id);
    el.classList.toggle("empty", !on && !spun);
    setFanIcon(el, f.sensor.value);
    const text = on ? `${fixed(f.sensor.value)} RPM` : spun ? "Stopped" : "0 rpm";
    setText(el.querySelector(".val"), text);
    name(el, `${fanName(f)}, ${on ? text : spun ? "stopped" : "not turning"}`);
  }

  for (const g of m.dimms) {
    const label = board.querySelector(`[data-dimm="${CSS.escape(g.name)}"] .temp`);
    const t = g.sensors.find((x) => isRealTemp(x) && /^DIMM/.test(x.name)) ?? g.sensors.find(isRealTemp);
    if (!label) continue;
    if (t) {
      setText(label, temp(t.value));
      const c = hue(t.value, limit("dimm").warn);
      label.style.color = c;
      label.parentElement.style.setProperty("--chip", c ? `color-mix(in srgb, ${c} 22%, var(--epoxy-up))` : "var(--epoxy-up)");
    } else {
      const capacity = find(g, "Data", /^Capacity$/);
      setText(label, capacity ? formatBytes(capacity.value) : "");
    }
  }

  if (m.memory) {
    const load = find(m.memory, "Load", /^Memory$/)?.value ?? s.ramLoad;
    const used = find(m.memory, "Data", /^Memory Used$/)?.value ?? s.ramUsed;
    const avail = find(m.memory, "Data", /^Memory Available$/)?.value;
    valueHtml($("ramLoad"), load == null ? null : fixed(load), "%");
    bar($("ramBar"), (load ?? 0) / 100);
    setText($("ramNote"), used == null ? "" : `${upTo(used, 1)} GB used${avail != null ? `, ${upTo(avail, 1)} free` : ""}`);
  }

  if (m.battery) {
    const level = find(m.battery, "Level", /Charge Level/)?.value ?? null;
    const charging = find(m.battery, "Power", /^Charge Rate$/)?.value;
    const discharging = find(m.battery, "Power", /^Discharge Rate$/)?.value;
    valueHtml($("batteryLevel"), level == null ? null : fixed(level), "%");
    bar($("batteryBar"), (level ?? 0) / 100);
    setText($("batteryNote"), charging > 0 ? `Charging, ${upTo(charging, 1)} W`
      : discharging > 0 ? `On battery, ${upTo(discharging, 1)} W` : "Plugged in");
  }

  // Board sensors: the hottest probe of each controller, then all of them.
  for (const g of m.boards) {
    const part = board.querySelector(`[data-probe="${CSS.escape(g.name)}"]`);
    if (!part) continue;
    const temps = g.sensors.filter((x) => isRealTemp(x) && x.value > 0);
    const hotEl = part.querySelector("[data-hot]");
    if (!temps.length) { valueHtml(hotEl, null, ""); continue; }
    const hot = Math.max(...temps.map((x) => x.value));
    valueHtml(hotEl, fixed(toUnit(hot)), degrees(), hue(hot, LIMITS.board.warn));
    setText(part.querySelector("[data-probecount]"), `hottest of ${temps.length}`);
    const probes = part.querySelector("[data-probes]");
    if (probes.children.length !== temps.length) {
      probes.replaceChildren(...temps.map(() => document.createElement("span")));
    }
    temps.forEach((t, i) => {
      setText(probes.children[i], temp(t.value));
      probes.children[i].dataset.p = settings.names[t.id] ?? t.name.replace(/^Temperature #(\d+)$/, "Probe $1");
      probes.children[i].style.color = hue(t.value, LIMITS.board.warn);
      probes.children[i].title = displayName(t);
    });
  }

  m.drives.forEach((d, i) => {
    const el = board.querySelector(`[data-drive="${i}"]`);
    if (!el) return;
    name(el, `${d.short}${d.temp ? `, ${temp(d.temp.value)}` : ""}${d.free ? `, ${formatBytes(d.free.value)} free of ${formatBytes(d.total.value)}` : ""}`);
    const tempEl = el.querySelector(".drive-temp");
    if (d.temp) {
      setText(tempEl, temp(d.temp.value));
      tempEl.style.color = hue(d.temp.value, d.limit.warn);
    }
    el.querySelector(".meter i").style.transform = `scaleX(${(d.used?.value ?? 0) / 100})`;
    const freeRatio = d.free && d.total.value ? d.free.value / d.total.value : 1;
    el.classList.toggle("warn", d.isSystem && freeRatio < FREE_WARN);
    // SATA tiles are narrow: remaining life stays in the detail and in the alerts.
    const extra = [d.life && d.kind === "nvme" ? `life ${fixed(d.life.value)} %` : "", d.isSystem ? "Windows" : ""].filter(Boolean);
    setText(el.querySelector(".drive-note"),
      d.free ? `${formatBytes(d.free.value)} free of ${formatBytes(d.total.value)}${extra.length ? `, ${extra.join(", ")}` : ""}` : "");
  });

  // Accessible names that carry the readings (the visible numbers are scattered across each part).
  if (m.cpu && s.cpuTemp != null) {
    name(board.querySelector(".cpu"), `${shortCpuName(m.cpu.name)}, ${temp(s.cpuTemp)}${s.cpuLoad != null ? `, ${fixed(s.cpuLoad)} % load` : ""}${s.cpuPower != null ? `, ${fixed(s.cpuPower)} W` : ""}`);
  }
  for (const g of m.gpus) {
    const r = gpuReadings(g);
    name(board.querySelector(`.gpu[data-sel="${CSS.escape(g.name)}"]`),
      `${shortGpuName(g.name)}${r.temp != null ? `, ${temp(r.temp)}` : ""}${r.load != null ? `, ${fixed(r.load)} % load` : ""}`);
  }
  for (const g of m.dimms) {
    const el = board.querySelector(`[data-dimm="${CSS.escape(g.name)}"]`);
    const t = g.sensors.find((x) => isRealTemp(x) && /^DIMM/.test(x.name)) ?? g.sensors.find(isRealTemp);
    name(el, `Memory module ${g.name.match(/#\d+/)?.[0] ?? ""}${t ? `, ${temp(t.value)}` : ""}`);
  }

  // The part closest to its own limit carries a stronger outline: the board always has a focus.
  let focal = null, worst = -1;
  const consider = (el, value, warn) => { if (el && value != null && value / warn > worst) { worst = value / warn; focal = el; } };
  if (m.cpu) consider(board.querySelector(".cpu"), s.cpuTemp, cpuLimits(m.cpu).warn);
  for (const g of m.gpus) consider(board.querySelector(`.gpu[data-sel="${CSS.escape(g.name)}"]`), gpuReadings(g).temp, limit("gpu").warn);
  for (const g of m.dimms) {
    const t = g.sensors.find((x) => isRealTemp(x) && /^DIMM/.test(x.name));
    consider(board.querySelector(`[data-dimm="${CSS.escape(g.name)}"]`), t?.value, limit("dimm").warn);
  }
  m.drives.forEach((d, i) => consider(board.querySelector(`[data-drive="${i}"]`), d.temp?.value, d.limit.warn));
  if (worst < 0.7) focal = null;   // only a part getting near its limit, as hue() colours it
  for (const el of board.querySelectorAll(".part.focal")) if (el !== focal) el.classList.remove("focal");
  focal?.classList.add("focal");

  // When the chart shows a fan, only that fan is highlighted, not its controller.
  const chartIsFan = m.fans.some((f) => f.sensor.id === state.chartId);
  for (const el of board.querySelectorAll("[data-sel]")) {
    const sameGroup = el.dataset.sel === state.selected;
    const on = el.dataset.fan ? sameGroup && el.dataset.fan === state.chartId : sameGroup && !chartIsFan;
    el.setAttribute("aria-pressed", on ? "true" : "false");
  }

  const hotNets = netsFor(m, m.snapshot.ready === false ? [] : collectAlerts(m));
  if ([...hotNets].join() !== [...state.hotNets].join()) { state.hotNets = hotNets; paintNets(); }

  setText($("keyHidden"), m.hiddenReaders ? `${m.hiddenReaders} empty card ${m.hiddenReaders === 1 ? "reader" : "readers"} hidden` : "");
  const parts = m.hiddenParts ? `${m.hiddenParts} ${m.hiddenParts === 1 ? "part" : "parts"} hidden by you.` : "";
  $("keyParts").hidden = !parts;
  setText($("keyPartsText"), parts);
  $("hiddenNote").hidden = !parts;
  setText($("hiddenCount"), parts);
}

// ---- Verdict ----------------------------------------------------------------------
function collectAlerts(m) {
  const alerts = [];
  const add = (sev, title, detail, target, extra = {}) => alerts.push({ sev, title, detail, target, ...extra });
  const s = m.snapshot.summary;

  const on = settings.alerts;
  const checkTemp = (value, limit, label, target) => {
    if (value == null || !on.temps) return;
    const act = "Check its cooling, or change the limit in Settings.";
    const extra = { kind: "temp", label, value };
    if (value >= limit.crit) add(2, `${label} at ${temp(value)}`, `Above the critical limit of ${temp(limit.crit)}. ${act}`, target, extra);
    else if (value >= limit.warn) add(1, `${label} at ${temp(value)}`, `Above the warning threshold of ${temp(limit.warn)}. ${act}`, target, extra);
  };

  if (m.cpu) checkTemp(s.cpuTemp, cpuLimits(m.cpu), "CPU", m.cpu.name);
  for (const g of m.gpus) {
    const r = gpuReadings(g);
    const name = m.gpus.length > 1 ? shortGpuName(g.name) : "GPU";
    checkTemp(r.temp, limit("gpu"), name, g.name);
    checkTemp(r.hot, limit("gpuHot"), `${name} hot spot`, g.name);
  }
  for (const g of m.dimms) {
    const t = g.sensors.find((x) => isRealTemp(x) && /^DIMM/.test(x.name));
    checkTemp(t?.value, limit("dimm"), `Memory module ${g.name.match(/#\d+/)?.[0] ?? ""}`, g.name);
  }
  for (const d of m.drives) {
    checkTemp(d.temp?.value, d.limit, `Drive ${d.short}`, d.group.name);
    if (on.space && d.isSystem && d.free && d.total.value) {
      const ratio = d.free.value / d.total.value;
      if (ratio < FREE_WARN) {
        add(ratio < FREE_CRIT ? 2 : 1, "Windows drive almost full",
          `${formatBytes(d.free.value)} free of ${formatBytes(d.total.value)}. Free some space: Windows slows down and updates fail when it runs out.`, d.group.name);
      }
    }
    if (on.wear && d.life && d.life.value <= LIFE_WARN) {
      add(d.life.value <= LIFE_CRIT ? 2 : 1, `SSD ${d.short} near end of life`,
        `Remaining life ${fixed(d.life.value)} %. Back up its data and plan a replacement.`, d.group.name);
    }
  }
  // A fan that was spinning and stopped. Graphics card fans are left out: they stop on purpose.
  for (const f of on.fans ? m.fans : []) {
    const h = f.sensor.history ?? [];
    const recent = h.slice(-5);
    if (recent.length === 5 && recent.every((v) => v === 0) && h.some((v) => v > 200)) {
      add(2, `${fanName(f)} stopped`, "It was spinning a moment ago. Check its cable and that nothing blocks it.", f.group.name, { label: fanName(f), code: `F${f.label[0]}` });
    }
  }
  return alerts.sort((a, b) => b.sev - a.sev);
}

function renderVerdict(title, bodyHtml, key, alerts = [], level = "off", code = "") {
  if (state.verdictKey === key) return;
  state.verdictKey = key;
  // The state is shown before any word: the headline's mark and the board's debug display.
  state.level = level;
  state.code = code;
  document.body.dataset.state = level;
  paintQcode();
  // Screen readers hear the state when it changes, not every degree of every reading.
  const spoken = `${level}|${alerts.map((a) => a.target + a.sev).join()}|${level === "off" ? title : ""}`;
  if (spoken !== state.announced) {
    state.announced = spoken;
    setText($("announce"), level === "ok" ? "All clear" : title);
  }
  setText($("verdictTitle"), title);
  $("verdictBody").innerHTML = bodyHtml;
  $("verdictBody").querySelectorAll("[data-alert]").forEach((b) => {
    b.addEventListener("click", () => select(alerts[+b.dataset.alert].target));
  });
  $("verdictBody").querySelector("[data-limits]")?.addEventListener("click", () => openSettings(true, "alerts"));
}

function updateVerdict(m) {
  const s = m.snapshot.summary;

  if (m.snapshot.ready === false) {
    renderVerdict("Reading sensors", "<span>This takes a few seconds at startup.</span>", "loading");
    return;
  }
  if (!m.snapshot.hardware.length) {
    renderVerdict("No sensors found",
      "<span>The WyreMonitor service found no hardware to read. Restart it in Services, or reinstall WyreMonitor.</span>", "none");
    return;
  }
  if (!m.snapshot.elevated) {
    renderVerdict("Temperatures and fans unavailable",
      "<span>The WyreMonitor service is running without administrator rights. Reinstall WyreMonitor to set it up again.</span>",
      "noadmin");
    return;
  }

  const alerts = collectAlerts(m);
  if (!alerts.length) {
    const parts = [];
    if (s.cpuTemp != null) parts.push(`CPU at ${temp(s.cpuTemp)}`);
    const gpuTemp = m.gpus.length ? gpuReadings(m.gpus[0]).temp : null;
    if (gpuTemp != null) parts.push(`GPU at ${temp(gpuTemp)}`);
    // Stopped fans are left out: many boards have empty headers, and one that stops raises its own alert.
    const spinning = m.fans.filter((f) => f.sensor.value > 0).length;
    if (spinning) parts.push(`${spinning} ${spinning === 1 ? "fan" : "fans"} spinning`);
    const text = parts.length ? `${parts.join(", ")}.` : "";
    // An alert that cleared stays in view for a while: the end of the incident, not just silence.
    if (state.incident) { state.recovered = { ...state.incident, end: Date.now() }; state.incident = null; }
    if (state.recovered && Date.now() - state.recovered.end > RECOVERED_FOR) state.recovered = null;
    const r = state.recovered;
    const clock = (t) => new Date(t).toLocaleTimeString(LOCALE, { hour: "2-digit", minute: "2-digit" });
    const back = r ? (r.kind === "temp"
      ? `${r.label} peaked at ${temp(r.peak)} at ${clock(r.start)} · back to normal at ${clock(r.end)}.`
      : `${r.label} at ${clock(r.start)} · back to normal at ${clock(r.end)}.`) : "";
    renderVerdict("All clear", `<span>${esc(text)}</span>${back ? `<span class="recovered">${esc(back)}</span>` : ""}`,
      `ok:${text}|${back}`, [], "ok");
    return;
  }

  // Remember the alert in progress and its peak, for the "back to normal" line.
  const first = alerts[0];
  if (!state.incident) state.incident = { label: first.label ?? first.title, peak: first.value ?? null, kind: first.kind, start: Date.now() };
  else if (first.value != null && (state.incident.peak == null || first.value > state.incident.peak)) state.incident.peak = first.value;

  // One alert: its explanation and two actions, each doing what it says. Several: one link per alert.
  const links = alerts.length === 1
    ? `<span>${esc(first.detail)}</span><span class="alert-actions"><button type="button" class="alert-link" data-alert="0">Show ${esc(first.label ?? "the part")}</button>${
      first.kind === "temp" ? `<button type="button" class="alert-link" data-limits>Change the limit</button>` : ""}</span>`
    : alerts.map((a, i) => `<button type="button" class="alert-link" data-alert="${i}">${esc(a.title)}</button>`).join("");
  const title = alerts.length === 1 ? first.title : `${alerts.length} things to check`;
  renderVerdict(title, links, alerts.map((a) => a.title + a.detail).join("|"), alerts, first.sev === 2 ? "crit" : "warn", codeFor(m, first));
}

const RECOVERED_FOR = 10 * 60 * 1000;

// Two characters on the debug display, naming the part to check, as a board's code names a stage.
function codeFor(m, a) {
  if (a.code) return a.code;
  if (m.cpu?.name === a.target) return "CP";
  if (m.gpus.some((g) => g.name === a.target)) return "GP";
  const dimm = m.dimms.find((g) => g.name === a.target);
  if (dimm) return `D${dimm.name.match(/#(\d+)/)?.[1] ?? ""}`.slice(0, 2);
  const drive = m.drives.find((d) => d.group.name === a.target);
  if (drive) return drive.kind === "nvme" ? "M2" : "SA";
  return "SB";
}

// ---- Detail panel -----------------------------------------------------------------
function groupTitle(g) {
  if (g.type === "Memory") {
    if (g.name === "Total Memory") return "Memory";
    if (g.name === "Virtual Memory") return "Virtual memory";
    const n = g.name.match(/\(#(\d+)\)$/)?.[1];
    return n ? `Memory module ${n}` : g.name;
  }
  if (g.type === "SuperIO" || g.type === "EmbeddedController") return `${lastSegment(g.name).split("/")[0]} sensors`;
  if (g.type === "Cpu") return shortCpuName(g.name);
  if (g.type.startsWith("Gpu")) return shortGpuName(g.name);
  return g.name;
}

function groupSubtitle(g, m) {
  if (g.type === "Memory" && g.name === "Total Memory") return "Physical memory, all modules";
  if (g.type === "Memory" && /\(#/.test(g.name)) return g.name.replace(/ \(#\d+\)$/, "").replace(/ - /, " ");
  if (g.type === "SuperIO") return "The controller doesn't report what each probe measures";
  const d = m.drives.find((x) => x.group === g);
  if (d) return `${{ nvme: "M.2 SSD", ssd: "SATA SSD", hdd: "Hard disk" }[d.kind]}${d.isSystem ? ", Windows drive" : ""}`;
  return "";
}

function defaultChart(g) {
  const pick = (type) => g.sensors.find((s) => s.type === type && !isSecondary(s));
  return (pick("Temperature") ?? pick("Load") ?? pick("Level") ?? pick("Fan") ?? pick("Data") ?? g.sensors[0])?.id ?? null;
}

function select(name, chartId) {
  // Choosing a part means wanting its detail: bring the panel back if it was hidden.
  if (!settings.rail) setRail(true);
  state.selected = name;
  const g = state.model.snapshot.hardware.find((h) => h.name === name);
  state.chartId = chartId ?? (g ? defaultChart(g) : null);
  state.showSecondary = false;
  renderRail();
  updateBoard(state.model);
  updateRail();
  // On narrow screens the detail sits under the board: bring it into view.
  if (matchMedia("(max-width: 1240px)").matches) {
    document.querySelector(".rail").scrollIntoView({ behavior: motionOn() ? "smooth" : "auto" });
  }
}

function renderRail() {
  const m = state.model;
  const g = m.snapshot.hardware.find((h) => h.name === state.selected);
  state.sensorRows.clear();
  if (!g) {
    setText($("railTitle"), "");
    setText($("railSub"), "");
    $("sensors").innerHTML = "";
    $("more").hidden = true;
    return;
  }

  setText($("railTitle"), groupTitle(g));
  setText($("railSub"), groupSubtitle(g, m));
  const target = hideTarget();
  $("hidePart").hidden = !target;
  if (target) setText($("hidePart"), `Hide ${target.label} from the board`);
  setText($("renameToggle"), state.renaming ? "Done renaming" : "Rename sensors");
  $("renameToggle").setAttribute("aria-pressed", String(state.renaming));

  const visible = g.sensors.filter((s) => state.showSecondary || !isSecondary(s));
  const hiddenCount = g.sensors.length - visible.length;

  const byType = new Map();
  for (const s of visible) {
    if (!byType.has(s.type)) byType.set(s.type, []);
    byType.get(s.type).push(s);
  }
  const rank = (t) => { const i = TYPE_ORDER.indexOf(t); return i < 0 ? 99 : i; };
  const types = [...byType.keys()].sort((a, b) => rank(a) - rank(b));

  // Renaming: each sensor becomes a text field; left empty, it goes back to its own name.
  const row = (s) => state.renaming
    ? `<label class="sensor renaming"><span class="visually-hidden">Name for ${esc(sensorLabel(s.name))}</span>
        <input type="text" data-rename="${esc(s.id)}" maxlength="${MAX_NAME}" value="${esc(settings.names[s.id] ?? "")}"
          placeholder="${esc(defaultName(s))}" /></label>`
    : `<button type="button" class="sensor" data-id="${esc(s.id)}" aria-pressed="false">
          <span class="sensor-name" title="${esc(s.name)}">${esc(shownName(s))}</span>
          <span class="sensor-val"></span>
        </button>`;
  $("sensors").innerHTML = types.map((t) => `
    <section class="sensor-group">
      <h3>${esc(TYPE_TITLES[t] ?? t)}</h3>
      ${byType.get(t).map(row).join("")}
    </section>`).join("");

  for (const el of $("sensors").querySelectorAll(".sensor[data-id]")) {
    state.sensorRows.set(el.dataset.id, { el, val: el.querySelector(".sensor-val") });
  }

  const more = $("more");
  more.hidden = !state.showSecondary && hiddenCount === 0;
  setText(more, state.showSecondary ? "Show main sensors only" : `Show ${hiddenCount} more ${hiddenCount === 1 ? "sensor" : "sensors"}`);
}

function updateRail() {
  const m = state.model;
  const g = m.snapshot.hardware.find((h) => h.name === state.selected);
  if (!g) return;

  const byId = new Map(g.sensors.map((s) => [s.id, s]));
  for (const [id, row] of state.sensorRows) {
    const s = byId.get(id);
    if (!s) continue;
    setText(row.val, formatValue(s.type, s.value, s.name));
    row.el.setAttribute("aria-pressed", id === state.chartId ? "true" : "false");
  }

  const chart = byId.get(state.chartId);
  if (!chart) return;
  setText($("scopeName"), shownName(chart));
  setText($("scopeValue"), formatValue(chart.type, chart.value, chart.name));
  const long = settings.chart > 60 && state.longHistory?.id === chart.id ? state.longHistory.values : null;
  const h = long ?? chart.history ?? [];
  for (const b of document.querySelectorAll(".span-pick button")) b.setAttribute("aria-pressed", String(+b.dataset.span === settings.chart));
  if (h.length) {
    setText($("scopeRange"), `min ${formatValue(chart.type, Math.min(...h), chart.name)}, max ${formatValue(chart.type, Math.max(...h), chart.name)}`);
  }
  drawScope(h, settings.chart);
}

// A board fan is named as on its tile ("Fan A1", or the user's name); other sensors by their
// user's name or their own.
function boardFan(sensor) { return state.model.fans.find((f) => f.sensor.id === sensor.id); }
function defaultName(sensor) { const f = boardFan(sensor); return f ? `Fan ${f.label}` : sensorLabel(sensor.name); }
function shownName(sensor) { const f = boardFan(sensor); return f ? fanName(f) : displayName(sensor); }

// What "Hide from the board" hides: the fan shown in the chart when it came from a fan tile,
// otherwise the selected part. A controller reached only through its fans has no tile to hide.
function hideTarget() {
  const m = state.model;
  const fan = m.fans.find((f) => f.sensor.id === state.chartId && f.group.name === state.selected);
  if (fan) return { key: fan.sensor.id, label: fanName(fan) };
  const g = m.snapshot.hardware.find((h) => h.name === state.selected);
  const hasTile = g && [m.cpu, m.memory, m.battery, ...m.gpus, ...m.dimms, ...m.drives.map((d) => d.group)]
    .concat(m.boards.filter((b) => b.sensors.some(isRealTemp))).includes(g);
  return hasTile ? { key: g.name, label: groupTitle(g) } : null;
}

// ---- Chart: a stepped trace, like a signal on a copper track ----------------------
function drawScope(history, slots) {
  const canvas = $("scope");
  const dpr = (devicePixelRatio || 1) * (canvas.getBoundingClientRect().width / canvas.clientWidth || 1);
  const css = getComputedStyle(canvas);
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (!w || !h) return;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
  }
  const ctx = canvas.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  ctx.strokeStyle = css.getPropertyValue("--trace").trim();
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let i = 0; i <= 3; i++) {
    const y = Math.round(4 + (i * (h - 8)) / 3) + 0.5;
    ctx.moveTo(0, y);
    ctx.lineTo(w, y);
  }
  ctx.stroke();

  if (history.length < 2) return;

  let lo = Math.min(...history), hi = Math.max(...history);
  if (hi - lo < 1e-6) { lo -= 1; hi += 1; }
  const pad = (hi - lo) * 0.1;
  lo -= pad; hi += pad;

  const x = (i) => w - ((history.length - 1 - i) / (slots - 1)) * w;
  const y = (v) => 4 + (1 - (v - lo) / (hi - lo)) * (h - 8);

  ctx.strokeStyle = css.getPropertyValue("--silk").trim();   // gold is only for load
  ctx.lineWidth = 2;
  ctx.lineJoin = "miter";
  ctx.beginPath();
  history.forEach((v, i) => {
    if (i === 0) { ctx.moveTo(x(i), y(v)); return; }
    ctx.lineTo(x(i), y(history[i - 1]));
    ctx.lineTo(x(i), y(v));
  });
  ctx.stroke();
}

// ---- Polling ----------------------------------------------------------------------
function signatureOf(m) {
  return m.snapshot.hardware.map((h) => `${h.name}:${h.sensors.length}`).join("|");
}

async function poll() {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 2500);
  try {
    // "fast" asks the service to read the sensors twice a second while this page polls.
    const url = settings.rate === 2 ? "/api/sensors?fast=1" : "/api/sensors";
    const res = await fetch(url, { cache: "no-store", signal: controller.signal });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const snapshot = await res.json();
    const m = readModel(snapshot);
    state.model = m;

    const sig = signatureOf(m);
    if (sig !== state.signature) {
      state.signature = sig;
      buildBoard(m);
    }
    // First data, or the selected part disappeared or was hidden: show the CPU, or the first part.
    if (!selectable(m, state.selected)) selectFirst(m);

    // More than a minute in the chart: the service sends the charted sensor's long history.
    if (settings.chart > 60 && state.chartId) {
      const hr = await fetch(`/api/history?id=${encodeURIComponent(state.chartId)}`, { cache: "no-store", signal: controller.signal });
      if (hr.ok) {
        const j = await hr.json();
        if (Array.isArray(j.history)) state.longHistory = { id: j.id, values: j.history.filter(Number.isFinite) };
      }
    }

    state.lastOk = Date.now();
    if (state.failed) { state.failed = false; state.verdictKey = ""; document.body.classList.remove("stale"); }

    updateBoard(m);
    updateList(m);
    updateRail();
    updateVerdict(m);
    setText($("fresh"), `Updated at ${new Date(snapshot.timestamp).toLocaleTimeString(LOCALE)}`);
  } catch {
    state.failed = true;
    document.body.classList.add("stale");
    const seconds = state.lastOk ? Math.round((Date.now() - state.lastOk) / 1000) : null;
    renderVerdict("The WyreMonitor service isn't responding",
      `<span>${seconds == null ? "No data received." : `No data for ${seconds} seconds.`} Restart the WyreMonitor service in Services, or reinstall WyreMonitor.</span>`,
      `down:${seconds}`);
    setText($("fresh"), "");
  } finally {
    clearTimeout(timer);
    setTimeout(poll, 1000 / settings.rate);
  }
}

function selectable(m, name) {
  if (settings.view === "list") return m.snapshot.hardware.some((h) => h.name === name && shown(h.name));
  return onBoard(m, name);
}

// ---- List view: every sensor of every part, like a classic hardware monitor -------------
// In the order of the board: CPU, graphics, memory, motherboard, drives, then the rest.
const LIST_ORDER = ["Cpu", "Gpu", "Memory", "Motherboard", "SuperIO", "EmbeddedController", "Storage", "Battery"];
function listGroups(m) {
  const rank = (g) => {
    const i = LIST_ORDER.findIndex((t) => g.type.startsWith(t));
    return i < 0 ? LIST_ORDER.length : i;
  };
  return m.snapshot.hardware
    .map((g, i) => ({ g, i }))
    .filter(({ g }) => g.sensors.length && shown(g.name))
    .sort((a, b) => rank(a.g) - rank(b.g) || a.i - b.i)
    .map(({ g }) => g);
}

function buildList(m) {
  state.listRows.clear();
  const rank = (t) => { const i = TYPE_ORDER.indexOf(t); return i < 0 ? 99 : i; };
  $("list").innerHTML = listGroups(m).map((g) => {
    const sensors = [...g.sensors].sort((a, b) => rank(a.type) - rank(b.type));
    return `<section class="list-group">
      <h3>${esc(groupTitle(g))}<small>${esc(groupSubtitle(g, m))}</small></h3>
      <table>
        <thead><tr><th scope="col">Sensor</th><th scope="col">Now</th><th scope="col">Min</th><th scope="col">Max</th></tr></thead>
        <tbody>${sensors.map((s) => `
          <tr data-group="${esc(g.name)}" data-id="${esc(s.id)}" tabindex="0">
            <th scope="row" title="${esc(s.name)}">${esc(shownName(s))}</th><td></td><td></td><td></td>
          </tr>`).join("")}</tbody>
      </table>
    </section>`;
  }).join("");
  for (const row of $("list").querySelectorAll("tr[data-id]")) {
    const [now, min, max] = row.querySelectorAll("td");
    state.listRows.set(row.dataset.id, { row, now, min, max });
  }
}

function updateList(m) {
  if (settings.view !== "list") return;
  const key = `${state.signature}|${settings.hidden.join()}|${JSON.stringify(settings.names)}`;
  if (key !== state.listKey) { state.listKey = key; buildList(m); }
  for (const g of listGroups(m)) {
    for (const s of g.sensors) {
      const cells = state.listRows.get(s.id);
      if (!cells) continue;
      setText(cells.now, formatValue(s.type, s.value, s.name));
      setText(cells.min, s.min == null ? "" : formatValue(s.type, s.min, s.name));
      setText(cells.max, s.max == null ? "" : formatValue(s.type, s.max, s.name));
      cells.row.setAttribute("aria-selected", String(s.id === state.chartId));
    }
  }
}

function applyView() {
  const list = settings.view === "list";
  document.body.classList.toggle("list-view", list);
  $("list").hidden = !list;
  setText($("viewToggle"), list ? "Board view" : "List view");
  $("viewToggle").setAttribute("aria-expanded", String(list));
  state.listKey = "";
  if (state.model) {
    if (!selectable(state.model, state.selected)) selectFirst(state.model);
    updateList(state.model);
    requestAnimationFrame(drawTraces);
  }
}

$("viewToggle").addEventListener("click", () => {
  settings.view = settings.view === "list" ? "board" : "list";
  saveSettings();
  applyView();
});

function chooseRow(row) {
  if (!row) return;
  select(row.dataset.group, row.dataset.id);
  updateList(state.model);
}
$("list").addEventListener("click", (e) => chooseRow(e.target.closest("tr[data-id]")));
$("list").addEventListener("keydown", (e) => {
  if (e.key === "Enter" || e.key === " ") { e.preventDefault(); chooseRow(e.target.closest("tr[data-id]")); }
});

// ---- Appearance ----------------------------------------------------------------------------
const LOOK_SWITCHES = ["cb", "contrast", "compact", "deco", "anim"];
const ZOOM = { small: 0.85, normal: 1, large: 1.2 };

// Animations follow the setting and the system's "reduce motion"; the fans have their own setting.
function motionOn() { return settings.anim && !REDUCED_MOTION; }

function applyLook() {
  const body = document.body;
  body.dataset.theme = settings.theme;
  body.classList.toggle("cb", settings.cb);
  body.classList.toggle("hc", settings.contrast);
  body.classList.toggle("compact", settings.compact);
  body.classList.toggle("no-deco", !settings.deco);
  document.documentElement.style.zoom = settings.size === "normal" ? "" : String(ZOOM[settings.size]);
  // Redrawn after the new layout: traces, heat colours, the chart.
  requestAnimationFrame(() => {
    drawTraces();
    if (state.model) { state.verdictKey = ""; updateBoard(state.model); updateRail(); }
  });
}


// ---- Small window: the app closes this one and opens the small one ----------------------
$("miniButton").hidden = !window.chrome?.webview;
$("miniButton").addEventListener("click", () => {
  try { window.chrome.webview.postMessage({ type: "mini" }); } catch { /* not in the app window */ }
});

// ---- CSV export: the app saves the file (the window itself can't download) --------------
$("exportNote").hidden = !window.chrome?.webview;
$("exportCsv").addEventListener("click", () => {
  try { window.chrome.webview.postMessage({ type: "export" }); } catch { /* not in the app window */ }
  openSettings(false);
});

function selectFirst(m) {
  const first = m.cpu ?? m.gpus[0] ?? m.memory ?? m.drives[0]?.group ?? m.dimms[0] ?? m.boards[0] ?? m.battery ?? null;
  state.selected = first?.name ?? null;
  state.chartId = first ? defaultChart(first) : null;
  state.showSecondary = false;
  renderRail();
}

// After a change to names or hidden parts: the board is drawn again from the last reading.
function rebuild() {
  saveSettings();
  if (!state.model) return;
  const m = readModel(state.model.snapshot);
  state.model = m;
  buildBoard(m);
  if (!selectable(m, state.selected)) selectFirst(m); else renderRail();
  state.listKey = "";
  state.verdictKey = "";
  updateBoard(m);
  updateRail();
  updateVerdict(m);
}

// ---- Interaction ------------------------------------------------------------------
$("board").addEventListener("click", (e) => {
  const part = e.target.closest("[data-sel]");
  if (part) select(part.dataset.sel, part.dataset.chart);
});

$("sensors").addEventListener("click", (e) => {
  const row = e.target.closest("button.sensor");
  if (!row) return;
  state.chartId = row.dataset.id;
  updateBoard(state.model);
  updateRail();
});

document.querySelector(".span-pick").addEventListener("click", (e) => {
  const b = e.target.closest("button[data-span]");
  if (!b) return;
  settings.chart = [300, 900].includes(+b.dataset.span) ? +b.dataset.span : 60;
  saveSettings();
  updateRail();
});

$("renameToggle").addEventListener("click", () => {
  state.renaming = !state.renaming;
  renderRail();
  updateRail();
  if (state.renaming) $("sensors").querySelector("input")?.focus();
});

$("sensors").addEventListener("change", (e) => {
  const id = e.target.dataset?.rename;
  if (id === undefined) return;
  const name = e.target.value.trim().slice(0, MAX_NAME);
  if (name && (id in settings.names || Object.keys(settings.names).length < MAX_ENTRIES)) settings.names[id] = name;
  else delete settings.names[id];
  rebuild();
});

$("sensors").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && e.target.dataset?.rename !== undefined) e.target.blur();
});

$("hidePart").addEventListener("click", () => {
  const target = hideTarget();
  if (!target || settings.hidden.length >= MAX_ENTRIES) return;
  settings.hidden.push(target.key);
  state.renaming = false;
  rebuild();
});

for (const button of document.querySelectorAll(".unhide")) {
  button.addEventListener("click", () => {
    settings.hidden = [];
    rebuild();
  });
}

$("more").addEventListener("click", () => {
  state.showSecondary = !state.showSecondary;
  renderRail();
  updateRail();
});

let resizeFrame = 0;
const resized = new ResizeObserver(() => {
  cancelAnimationFrame(resizeFrame);
  resizeFrame = requestAnimationFrame(() => {
    drawTraces();
    if (state.model) updateRail();
  });
});
resized.observe(document.body);
resized.observe($("board"));   // hiding the detail panel widens the board, not the window

// ---- Detail panel shown or hidden ------------------------------------------------
function setRail(shown) {
  settings.rail = shown;
  saveSettings();
  applyRail();
}

function applyRail() {
  document.body.classList.toggle("no-rail", !settings.rail);
  setText($("railToggle"), settings.rail ? "Hide detail" : "Show detail");
  $("railToggle").setAttribute("aria-expanded", String(settings.rail));
}

$("railToggle").addEventListener("click", () => setRail(!settings.rail));
$("railClose").addEventListener("click", () => { setRail(false); $("railToggle").focus(); });

// At middle widths the panel is a drawer over the board: it starts below the header, so the
// header's buttons stay in reach, and Escape closes it.
const drawer = matchMedia("(min-width: 761px) and (max-width: 1240px)");
new ResizeObserver(() => {
  document.body.style.setProperty("--top-h", `${Math.ceil(document.querySelector(".top").getBoundingClientRect().height)}px`);
}).observe(document.querySelector(".top"));

// ---- Settings panel ---------------------------------------------------------------
const settingsPanel = $("settings");

const LIMIT_LABELS = { cpu: "CPU", gpu: "GPU", gpuHot: "GPU hot spot", dimm: "Memory modules" };

// Limits are typed in the unit shown, and kept in °C.
function renderLimits() {
  const cpu = state.model?.cpu;
  settingsPanel.querySelector(".limits thead th").textContent = `Limits, ${degrees()}`;
  $("limitRows").innerHTML = LIMIT_KEYS.map((key) => {
    const l = limit(key, cpu);
    const cell = (level) => `<td><input type="number" step="1" data-limit="${key}" data-level="${level}"
      value="${Math.round(toUnit(l[level]))}" aria-label="${LIMIT_LABELS[key]}, ${level === "warn" ? "warning" : "critical"}, ${degrees()}" /></td>`;
    return `<tr><th scope="row">${LIMIT_LABELS[key]}</th>${cell("warn")}${cell("crit")}</tr>`;
  }).join("");
  $("limitError").hidden = true;
}

function syncSettingsForm() {
  for (const input of settingsPanel.querySelectorAll("input[type=radio]")) input.checked = String(settings[input.name]) === input.value;
  for (const input of settingsPanel.querySelectorAll("input[name=alert]")) input.checked = settings.alerts[input.value];
  for (const input of settingsPanel.querySelectorAll("input[name=look]")) input.checked = settings[input.value];
  renderLimits();
}

function showTab(id) {
  for (const t of settingsPanel.querySelectorAll("[role=tab]")) {
    const on = t.dataset.tab === id;
    t.setAttribute("aria-selected", String(on));
    t.tabIndex = on ? 0 : -1;
  }
  for (const p of settingsPanel.querySelectorAll("[role=tabpanel]")) p.hidden = p.id !== `tab-${id}`;
}

function openSettings(open, tab) {
  settingsPanel.hidden = !open;
  $("settingsToggle").setAttribute("aria-expanded", String(open));
  if (open) {
    if (tab) showTab(tab);
    syncSettingsForm();
    settingsPanel.querySelector("[role=tab][aria-selected=true]")?.focus();
  }
}

settingsPanel.querySelector("[role=tablist]").addEventListener("click", (e) => {
  const t = e.target.closest("[role=tab]");
  if (t) showTab(t.dataset.tab);
});
settingsPanel.querySelector("[role=tablist]").addEventListener("keydown", (e) => {
  if (!["ArrowRight", "ArrowLeft", "Home", "End"].includes(e.key)) return;
  e.preventDefault();
  const tabs = [...settingsPanel.querySelectorAll("[role=tab]")];
  const i = tabs.findIndex((t) => t.getAttribute("aria-selected") === "true");
  const next = e.key === "Home" ? tabs[0] : e.key === "End" ? tabs[tabs.length - 1]
    : tabs[(i + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
  showTab(next.dataset.tab);
  next.focus();
});
$("settingsClose").addEventListener("click", () => { openSettings(false); $("settingsToggle").focus(); });

function setLimit(key) {
  const inputs = [...settingsPanel.querySelectorAll(`[data-limit="${key}"]`)];
  const toC = (v) => (settings.unit === "F" ? (v - 32) * 5 / 9 : v);
  const [warn, crit] = inputs.map((i) => Math.round(toC(parseFloat(i.value)) * 10) / 10);
  const ok = validLimit({ warn, crit });
  inputs.forEach((i) => i.setAttribute("aria-invalid", String(!ok)));
  $("limitError").hidden = ok;
  setText($("limitError"), `Warning must be lower than critical, between ${temp(20)} and ${temp(150)}.`);
  if (!ok) return false;
  const def = defaultLimit(key, state.model?.cpu);
  if (Math.abs(warn - def.warn) < 0.5 && Math.abs(crit - def.crit) < 0.5) delete settings.alerts.limits[key];
  else settings.alerts.limits[key] = { warn, crit };
  return true;
}

$("resetAlerts").addEventListener("click", () => {
  settings.alerts = cleanAlerts({});
  saveSettings();
  syncSettingsForm();
  refreshView();
});

function refreshView() {
  // Redraw now with the new units, limits and fan icons; a new pace applies from the next reading.
  if (!state.model) return;
  state.verdictKey = "";
  updateBoard(state.model);
  updateRail();
  updateVerdict(state.model);
}

$("settingsToggle").addEventListener("click", () => openSettings(settingsPanel.hidden));

settingsPanel.addEventListener("change", (e) => {
  const { name, value, checked } = e.target;
  if (name === "unit") { settings.unit = value === "F" ? "F" : "C"; renderLimits(); }
  else if (name === "rate") settings.rate = value === "2" ? 2 : 1;
  else if (name === "fans") settings.fans = value === "still" ? "still" : "spin";
  else if (name === "chart") settings.chart = [300, 900].includes(+value) ? +value : 60;
  else if (name === "theme") settings.theme = ["black", "blue", "white"].includes(value) ? value : "green";
  else if (name === "size") settings.size = ["small", "large"].includes(value) ? value : "normal";
  else if (name === "look" && LOOK_SWITCHES.includes(value)) settings[value] = checked;
  else if (name === "alert" && value in settings.alerts && value !== "limits") settings.alerts[value] = checked;
  else if (e.target.dataset.limit) { if (!setLimit(e.target.dataset.limit)) return; }
  else return;
  saveSettings();
  // Applied here, after the choice is stored: a separate listener can run before this one.
  if (["theme", "size", "look"].includes(name)) applyLook();
  refreshView();
});

document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape") return;
  if (!settingsPanel.hidden) {
    openSettings(false);
    $("settingsToggle").focus();
  } else if (settings.rail && drawer.matches) {
    setRail(false);
    $("railToggle").focus();
  }
});

document.addEventListener("pointerdown", (e) => {
  if (!settingsPanel.hidden && !e.target.closest("#settings, #settingsToggle")) openSettings(false);
});

applyRail();
applyView();
applyLook();
mirrorToApp();

poll();
