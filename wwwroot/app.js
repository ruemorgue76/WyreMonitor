"use strict";

/* WyreMonitor dashboard. The page is a plan of the machine.
   Gold = how busy a part is. Colour = how close it is to its limit. */

const POLL_MS = 1000;
const REDUCED_MOTION = matchMedia("(prefers-reduced-motion: reduce)").matches;
const LOCALE = undefined;   // numbers and times follow the Windows regional settings

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

const formatBytes = (gb) => (gb >= 1000 ? `${upTo(gb / 1000, 2)} TB` : `${upTo(gb, gb < 100 ? 1 : 0)} GB`);

function formatRate(bytesPerSecond) {
  if (bytesPerSecond >= 1e9) return `${upTo(bytesPerSecond / 1e9, 2)} GB/s`;
  if (bytesPerSecond >= 1e6) return `${upTo(bytesPerSecond / 1e6, 1)} MB/s`;
  if (bytesPerSecond >= 1e3) return `${upTo(bytesPerSecond / 1e3, 0)} kB/s`;
  return `${upTo(bytesPerSecond, 0)} B/s`;
}

function formatValue(type, value, name = "") {
  switch (type) {
    case "Temperature": return `${upTo(value, 1)} °C`;
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
const RAMP = [
  [0.40, [125, 184, 196]],
  [0.70, [214, 212, 138]],
  [1.00, [242, 169, 59]],
  [1.15, [255, 92, 51]],
];

function heat(temp, warn) {
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

function cpuLimits(cpu) {
  return /intel/i.test(cpu?.name ?? "") ? LIMITS.cpuIntel : LIMITS.cpuAmd;
}

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

  // Fans by controller: A = first chip, B = second, and so on.
  const fans = [];
  boards.forEach((g, c) => {
    g.sensors.filter((s) => s.type === "Fan").forEach((s, i) => {
      fans.push({ group: g, sensor: s, label: `${String.fromCharCode(65 + c)}${i + 1}` });
    });
  });

  return { snapshot, cpu, gpus, memory, dimms, boards, battery, fans, drives, hiddenReaders };
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
</svg><img class="spin" src="/fan-spin.png" alt="" /></span>`;

// A spinning fan shows an animated image at a fixed speed; a stopped one shows the still icon.
// An image redraws only its own few pixels, 12 times a second: a CSS or script animation made
// the whole window redraw 60 times a second and tripled the app's CPU use.
function setFanIcon(holder, rpm) {
  holder?.querySelector(".rotor")?.classList.toggle("spinning", rpm > 0 && !REDUCED_MOTION);
  holder?.classList.toggle("off", rpm <= 0);
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
  if (dies.length > 1) {
    // Chiplets around the I/O die, as they sit under the heat spreader.
    const half = Math.ceil(dies.length / 2);
    return `<button type="button" class="part cpu" data-sel="${esc(m.cpu.name)}">
      <span class="die-col">${dies.slice(0, half).map(buildDie).join("")}</span>
      ${info}
      <span class="die-col">${dies.slice(half).map(buildDie).join("")}</span>
    </button>`;
  }
  return `<button type="button" class="part cpu single" data-sel="${esc(m.cpu.name)}">
    ${info}<span class="die-col">${buildDie(dies[0])}</span>
  </button>`;
}

function buildGpu(g) {
  const r = gpuReadings(g);
  const cell = (label, key, meter) => `<span class="stat-cell"><span class="designator">${label}</span><b data-gpu="${key}"></b>${
    meter ? `<span class="meter"><i data-gpubar="${key}"></i></span>` : ""}</span>`;
  return `<button type="button" class="part gpu" data-sel="${esc(g.name)}">
    ${r.fans.length ? `<span class="gpu-fans">${r.fans.map((s) => `<span data-gpufan="${esc(s.id)}">${ROTOR}</span>`).join("")}</span>` : ""}
    <span>
      <span class="designator">${esc(shortGpuName(g.name))}</span>
      <span class="big" data-gpu="temp"></span>
    </span>
    <span class="gpu-stats">
      ${r.load !== null ? cell("Load", "load", true) : ""}
      ${r.vramUsed !== null ? cell("Video memory", "vram", r.vramTotal !== null) : ""}
      ${r.power !== null ? cell("Power", "power") : ""}
      ${r.hot !== null ? cell("Hot spot", "hot") : ""}
    </span>
  </button>`;
}

function buildDimm(g) {
  const n = g.name.match(/\(#(\d+)\)$/)[1];
  return `<button type="button" class="part dimm" data-sel="${esc(g.name)}" data-dimm="${esc(g.name)}" title="${esc(g.name)}">
    <span class="temp"></span><span class="designator">DIMM${n}</span>
  </button>`;
}

function buildDrive(d, i) {
  return `<button type="button" class="part drive ${d.kind === "nvme" ? "m2" : "sata"}" data-sel="${esc(d.group.name)}" data-drive="${i}" title="${esc(d.group.name)}">
    <span class="drive-head"><span class="drive-name">${esc(d.short)}</span><span class="drive-temp"></span></span>
    <span class="meter"><i></i></span>
    <span class="drive-note"></span>
  </button>`;
}

function boardChip(g) {
  const chip = lastSegment(g.name).split("/")[0].replace(/^ITE /, "").replace(/^Nuvoton /, "");
  return `<button type="button" class="part chip grow" data-sel="${esc(g.name)}" data-probe="${esc(g.name)}">
    <span class="designator">${esc(chip)} sensors</span>
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
    <p class="brand">${esc(motherboard?.name ?? m.snapshot.machineName ?? "This PC")}</p>
    ${m.fans.length ? `<div class="fans">${m.fans.map((f) => `
      <button type="button" class="part fan" data-sel="${esc(f.group.name)}" data-chart="${esc(f.sensor.id)}" data-fan="${esc(f.sensor.id)}" title="${esc(lastSegment(f.group.name))}, ${esc(f.sensor.name)}">
        ${ROTOR}<span><span class="designator">Fan ${esc(f.label)}</span><span class="val"></span></span>
      </button>`).join("")}</div>` : ""}
    ${m.dimms.length ? `<div class="dimms l">${m.dimms.slice(0, half).map(buildDimm).join("")}</div>` : ""}
    ${buildCpu(m)}
    ${m.dimms.length ? `<div class="dimms r">${m.dimms.slice(half).map(buildDimm).join("")}</div>` : ""}
    ${m.gpus.length ? `<div class="gpus">${m.gpus.map(buildGpu).join("")}</div>` : ""}
    <div class="side">
      ${m.memory ? `<button type="button" class="part chip" data-sel="${esc(m.memory.name)}">
        <span class="designator">Memory</span>
        <span class="big" id="ramLoad"></span>
        <span class="meter"><i id="ramBar"></i></span>
        <span class="note" id="ramNote"></span>
      </button>` : ""}
      ${m.battery ? `<button type="button" class="part chip" data-sel="${esc(m.battery.name)}">
        <span class="designator">Battery</span>
        <span class="big" id="batteryLevel"></span>
        <span class="meter"><i id="batteryBar"></i></span>
        <span class="note" id="batteryNote"></span>
      </button>` : ""}
      ${m.boards.filter((g) => g.sensors.some(isRealTemp)).map(boardChip).join("")}
    </div>
    ${m.drives.length ? `<div class="store">
      ${nvme.length ? `<div class="store-row m2-row">${nvme.map(([d, i]) => buildDrive(d, i)).join("")}</div>` : ""}
      ${sata.length ? `<div class="store-row sata-row">${sata.map(([d, i]) => buildDrive(d, i)).join("")}</div>` : ""}
    </div>` : ""}`;

  // The fan icons stay still on purpose: spinning them made the window redraw 60 times a
  // second and tripled the app's CPU use. The RPM is written next to each one.
  drawTraces();
}

// Copper traces: link the CPU to what is really wired to it.
function drawTraces() {
  const board = $("board");
  const svg = $("traces");
  if (!svg || getComputedStyle(svg).display === "none") return;
  const origin = board.getBoundingClientRect();
  const box = (el) => {
    const r = el.getBoundingClientRect();
    return { l: r.left - origin.left, r: r.right - origin.left, t: r.top - origin.top, b: r.bottom - origin.top };
  };
  const cpuEl = board.querySelector(".cpu");
  if (!cpuEl) { svg.innerHTML = ""; return; }

  const cpu = box(cpuEl);
  const paths = [];

  // Memory channels, towards the modules on both sides.
  for (const el of board.querySelectorAll(".dimms")) {
    const d = box(el);
    const fromLeft = d.r <= cpu.l;
    const x1 = fromLeft ? d.r : cpu.r;
    const x2 = fromLeft ? cpu.l : d.l;
    for (let i = 0; i < 6; i++) paths.push(`M${x1} ${cpu.t + (cpu.b - cpu.t) * (0.2 + i * 0.12)}H${x2}`);
  }
  // PCIe lanes towards the graphics card.
  const gpuEl = board.querySelector(".gpu");
  if (gpuEl) {
    const g = box(gpuEl);
    const mid = (cpu.l + cpu.r) / 2;
    for (let i = -5; i <= 5; i++) paths.push(`M${mid + i * 12} ${cpu.b}V${g.t}`);
  }
  // Towards the side column and the drives.
  const sideEl = board.querySelector(".side");
  const storeEl = board.querySelector(".store");
  if (sideEl) {
    const s = box(sideEl);
    for (let i = 0; i < 3; i++) paths.push(`M${cpu.r} ${cpu.t + 22 + i * 8}H${s.l}`);
    if (storeEl) {
      const st = box(storeEl);
      const x0 = (s.l + s.r) / 2;
      for (let i = -3; i <= 3; i++) paths.push(`M${x0 + i * 12} ${s.b}V${st.t}`);
    }
  }

  svg.innerHTML = paths.map((d) => `<path d="${d}" pathLength="1"/>`).join("");

  // The page's single moment of motion: the traces light up on first load.
  if (!state.tracesDrawn && !REDUCED_MOTION) {
    for (const p of svg.querySelectorAll("path")) {
      p.animate([{ strokeDasharray: "1", strokeDashoffset: 1 }, { strokeDasharray: "1", strokeDashoffset: 0 }],
        { duration: 700, easing: "ease-out" });
    }
  }
  state.tracesDrawn = true;
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

function bar(el, fraction) {
  if (el) el.style.transform = `scaleX(${Math.max(0, Math.min(1, fraction))})`;
}

function updateBoard(m) {
  const s = m.snapshot.summary;
  const board = $("board");
  const byId = new Map(m.snapshot.hardware.flatMap((h) => h.sensors.map((x) => [x.id, x])));

  if (m.cpu) {
    const limit = cpuLimits(m.cpu);
    valueHtml($("cpuTemp"), s.cpuTemp == null ? null : fixed(s.cpuTemp), "°C", s.cpuTemp == null ? null : heat(s.cpuTemp, limit.warn));
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
        const c = heat(t.value, limit.warn);
        setText(label, `${fixed(t.value)} °C`);
        label.style.color = c;
        el.style.background = `color-mix(in srgb, ${c} 16%, #20282a)`;
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
    valueHtml(q("temp"), r.temp == null ? null : fixed(r.temp), "°C", r.temp == null ? null : heat(r.temp, LIMITS.gpu.warn));
    setText(q("load"), r.load == null ? "n/a" : `${fixed(r.load)} %`);
    bar(el.querySelector('[data-gpubar="load"]'), (r.load ?? 0) / 100);
    setText(q("vram"), r.vramUsed == null ? "n/a"
      : r.vramTotal ? `${upTo(r.vramUsed / 1024, 1)} / ${upTo(r.vramTotal / 1024, 1)} GB` : `${upTo(r.vramUsed / 1024, 1)} GB`);
    bar(el.querySelector('[data-gpubar="vram"]'), r.vramUsed != null && r.vramTotal ? r.vramUsed / r.vramTotal : 0);
    setText(q("power"), r.power == null ? "n/a" : `${fixed(r.power)} W`);
    const hotEl = q("hot");
    setText(hotEl, r.hot == null ? "n/a" : `${fixed(r.hot)} °C`);
    if (hotEl && r.hot != null) hotEl.style.color = heat(r.hot, LIMITS.gpuHot.warn);
    for (const f of r.fans) setFanIcon(el.querySelector(`[data-gpufan="${CSS.escape(f.id)}"]`), f.value);
  }

  for (const f of m.fans) {
    const el = board.querySelector(`[data-fan="${CSS.escape(f.sensor.id)}"]`);
    if (!el) continue;
    const on = f.sensor.value > 0;
    setFanIcon(el, f.sensor.value);
    setText(el.querySelector(".val"), on ? `${fixed(f.sensor.value)} RPM` : "Stopped");
  }

  for (const g of m.dimms) {
    const label = board.querySelector(`[data-dimm="${CSS.escape(g.name)}"] .temp`);
    const t = g.sensors.find((x) => isRealTemp(x) && /^DIMM/.test(x.name)) ?? g.sensors.find(isRealTemp);
    if (!label) continue;
    if (t) {
      setText(label, `${fixed(t.value)} °C`);
      label.style.color = heat(t.value, LIMITS.dimm.warn);
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
    valueHtml(hotEl, fixed(hot), "°C", heat(hot, LIMITS.board.warn));
    setText(part.querySelector("[data-probecount]"), `hottest of ${temps.length}`);
    const probes = part.querySelector("[data-probes]");
    if (probes.children.length !== temps.length) {
      probes.replaceChildren(...temps.map(() => document.createElement("span")));
    }
    temps.forEach((t, i) => {
      setText(probes.children[i], `${fixed(t.value)}°`);
      probes.children[i].style.color = heat(t.value, LIMITS.board.warn);
      probes.children[i].title = sensorLabel(t.name);
    });
  }

  m.drives.forEach((d, i) => {
    const el = board.querySelector(`[data-drive="${i}"]`);
    if (!el) return;
    const tempEl = el.querySelector(".drive-temp");
    if (d.temp) {
      setText(tempEl, `${fixed(d.temp.value)} °C`);
      tempEl.style.color = heat(d.temp.value, d.limit.warn);
    }
    el.querySelector(".meter i").style.transform = `scaleX(${(d.used?.value ?? 0) / 100})`;
    const freeRatio = d.free && d.total.value ? d.free.value / d.total.value : 1;
    el.classList.toggle("warn", d.isSystem && freeRatio < FREE_WARN);
    // SATA tiles are narrow: remaining life stays in the detail and in the alerts.
    const extra = [d.life && d.kind === "nvme" ? `life ${fixed(d.life.value)} %` : "", d.isSystem ? "Windows" : ""].filter(Boolean);
    setText(el.querySelector(".drive-note"),
      d.free ? `${formatBytes(d.free.value)} free of ${formatBytes(d.total.value)}${extra.length ? `, ${extra.join(", ")}` : ""}` : "");
  });

  // When the chart shows a fan, only that fan is highlighted, not its controller.
  const chartIsFan = m.fans.some((f) => f.sensor.id === state.chartId);
  for (const el of board.querySelectorAll("[data-sel]")) {
    const sameGroup = el.dataset.sel === state.selected;
    const on = el.dataset.fan ? sameGroup && el.dataset.fan === state.chartId : sameGroup && !chartIsFan;
    el.setAttribute("aria-pressed", on ? "true" : "false");
  }

  setText($("keyHidden"), m.hiddenReaders ? `${m.hiddenReaders} empty card ${m.hiddenReaders === 1 ? "reader" : "readers"} hidden` : "");
}

// ---- Verdict ----------------------------------------------------------------------
function collectAlerts(m) {
  const alerts = [];
  const add = (sev, title, detail, target) => alerts.push({ sev, title, detail, target });
  const s = m.snapshot.summary;

  const checkTemp = (temp, limit, label, target) => {
    if (temp == null) return;
    if (temp >= limit.crit) add(2, `${label} at ${fixed(temp)} °C`, `Above the critical limit of ${limit.crit} °C.`, target);
    else if (temp >= limit.warn) add(1, `${label} at ${fixed(temp)} °C`, `Above the warning threshold of ${limit.warn} °C.`, target);
  };

  if (m.cpu) checkTemp(s.cpuTemp, cpuLimits(m.cpu), "CPU", m.cpu.name);
  for (const g of m.gpus) {
    const r = gpuReadings(g);
    const name = m.gpus.length > 1 ? shortGpuName(g.name) : "GPU";
    checkTemp(r.temp, LIMITS.gpu, name, g.name);
    checkTemp(r.hot, LIMITS.gpuHot, `${name} hot spot`, g.name);
  }
  for (const g of m.dimms) {
    const t = g.sensors.find((x) => isRealTemp(x) && /^DIMM/.test(x.name));
    checkTemp(t?.value, LIMITS.dimm, `Memory module ${g.name.match(/#\d+/)?.[0] ?? ""}`, g.name);
  }
  for (const d of m.drives) {
    checkTemp(d.temp?.value, d.limit, `Drive ${d.short}`, d.group.name);
    if (d.isSystem && d.free && d.total.value) {
      const ratio = d.free.value / d.total.value;
      if (ratio < FREE_WARN) {
        add(ratio < FREE_CRIT ? 2 : 1, "Windows drive almost full",
          `${formatBytes(d.free.value)} free of ${formatBytes(d.total.value)}.`, d.group.name);
      }
    }
    if (d.life && d.life.value <= LIFE_WARN) {
      add(d.life.value <= LIFE_CRIT ? 2 : 1, `SSD ${d.short} near end of life`,
        `Remaining life ${fixed(d.life.value)} %.`, d.group.name);
    }
  }
  // A fan that was spinning and stopped. Graphics card fans are left out: they stop on purpose.
  for (const f of m.fans) {
    const h = f.sensor.history ?? [];
    const recent = h.slice(-5);
    if (recent.length === 5 && recent.every((v) => v === 0) && h.some((v) => v > 200)) {
      add(2, `Fan ${f.label} stopped`, "It was spinning a moment ago.", f.group.name);
    }
  }
  return alerts.sort((a, b) => b.sev - a.sev);
}

function renderVerdict(title, bodyHtml, key, alerts = []) {
  if (state.verdictKey === key) return;
  state.verdictKey = key;
  setText($("verdictTitle"), title);
  $("verdictBody").innerHTML = bodyHtml;
  $("verdictBody").querySelectorAll("[data-alert]").forEach((b) => {
    b.addEventListener("click", () => select(alerts[+b.dataset.alert].target));
  });
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
    if (s.cpuTemp != null) parts.push(`CPU at ${fixed(s.cpuTemp)} °C`);
    const gpuTemp = m.gpus.length ? gpuReadings(m.gpus[0]).temp : null;
    if (gpuTemp != null) parts.push(`GPU at ${fixed(gpuTemp)} °C`);
    if (m.fans.length) parts.push(`${m.fans.filter((f) => f.sensor.value > 0).length} of ${m.fans.length} fans spinning`);
    const text = parts.length ? `${parts.join(", ")}.` : "";
    renderVerdict("All clear", `<span>${esc(text)}</span>`, `ok:${text}`);
    return;
  }

  const lower = (t) => t[0].toLowerCase() + t.slice(1);
  const links = alerts.map((a, i) => `<button type="button" class="alert-link" data-alert="${i}">${
    esc(alerts.length === 1 ? a.detail : `${a.title}: ${lower(a.detail)}`)}</button>`).join("");
  const title = alerts.length === 1 ? alerts[0].title : `${alerts.length} things to check`;
  renderVerdict(title, links, alerts.map((a) => a.title + a.detail).join("|"), alerts);
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
  state.selected = name;
  const g = state.model.snapshot.hardware.find((h) => h.name === name);
  state.chartId = chartId ?? (g ? defaultChart(g) : null);
  state.showSecondary = false;
  renderRail();
  updateBoard(state.model);
  updateRail();
  // On narrow screens the detail sits under the board: bring it into view.
  if (matchMedia("(max-width: 1240px)").matches) {
    document.querySelector(".rail").scrollIntoView({ behavior: REDUCED_MOTION ? "auto" : "smooth" });
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

  const visible = g.sensors.filter((s) => state.showSecondary || !isSecondary(s));
  const hiddenCount = g.sensors.length - visible.length;

  const byType = new Map();
  for (const s of visible) {
    if (!byType.has(s.type)) byType.set(s.type, []);
    byType.get(s.type).push(s);
  }
  const rank = (t) => { const i = TYPE_ORDER.indexOf(t); return i < 0 ? 99 : i; };
  const types = [...byType.keys()].sort((a, b) => rank(a) - rank(b));

  $("sensors").innerHTML = types.map((t) => `
    <section class="sensor-group">
      <h3>${esc(TYPE_TITLES[t] ?? t)}</h3>
      ${byType.get(t).map((s) => `
        <button type="button" class="sensor" data-id="${esc(s.id)}" aria-pressed="false">
          <span class="sensor-name" title="${esc(s.name)}">${esc(sensorLabel(s.name))}</span>
          <span class="sensor-val"></span>
        </button>`).join("")}
    </section>`).join("");

  for (const el of $("sensors").querySelectorAll(".sensor")) {
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
  setText($("scopeName"), sensorLabel(chart.name));
  setText($("scopeValue"), formatValue(chart.type, chart.value, chart.name));
  const h = chart.history ?? [];
  if (h.length) {
    setText($("scopeRange"), `min ${formatValue(chart.type, Math.min(...h), chart.name)}, max ${formatValue(chart.type, Math.max(...h), chart.name)}`);
  }
  drawScope(h);
}

// ---- Chart: a stepped trace, like a signal on a copper track ----------------------
function drawScope(history) {
  const canvas = $("scope");
  const dpr = devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (!w || !h) return;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
  }
  const ctx = canvas.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  ctx.strokeStyle = "#26695a";
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

  const SLOTS = 60;
  const x = (i) => w - ((history.length - 1 - i) / (SLOTS - 1)) * w;
  const y = (v) => 4 + (1 - (v - lo) / (hi - lo)) * (h - 8);

  ctx.strokeStyle = "#d8b45a";
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
    const res = await fetch("/api/sensors", { cache: "no-store", signal: controller.signal });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const snapshot = await res.json();
    const m = readModel(snapshot);
    state.model = m;

    const sig = signatureOf(m);
    if (sig !== state.signature) {
      state.signature = sig;
      buildBoard(m);
    }
    // First data, or the selected part disappeared: show the CPU, or the first part found.
    if (!snapshot.hardware.some((h) => h.name === state.selected)) {
      const first = m.cpu ?? snapshot.hardware[0];
      state.selected = first?.name ?? null;
      state.chartId = first ? defaultChart(first) : null;
      state.showSecondary = false;
      renderRail();
    }

    state.lastOk = Date.now();
    if (state.failed) { state.failed = false; state.verdictKey = ""; document.body.classList.remove("stale"); }

    updateBoard(m);
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
    setTimeout(poll, POLL_MS);
  }
}

// ---- Interaction ------------------------------------------------------------------
$("board").addEventListener("click", (e) => {
  const part = e.target.closest("[data-sel]");
  if (part) select(part.dataset.sel, part.dataset.chart);
});

$("sensors").addEventListener("click", (e) => {
  const row = e.target.closest(".sensor");
  if (!row) return;
  state.chartId = row.dataset.id;
  updateBoard(state.model);
  updateRail();
});

$("more").addEventListener("click", () => {
  state.showSecondary = !state.showSecondary;
  renderRail();
  updateRail();
});

let resizeFrame = 0;
new ResizeObserver(() => {
  cancelAnimationFrame(resizeFrame);
  resizeFrame = requestAnimationFrame(() => {
    drawTraces();
    if (state.model) updateRail();
  });
}).observe(document.body);

poll();
