"use strict";

const bandCount = 24;
const captureButton = document.querySelector("#capture");
const statusLabel = document.querySelector("#status");
const bandContainer = document.querySelector("#spectrum");
const bandFills = Array.from({ length: bandCount }, () => {
  const band = document.createElement("div");
  band.className = "band";
  const fill = band.appendChild(document.createElement("i"));
  bandContainer.appendChild(band);
  return fill;
});
const tracks = ["left", "right"].map((id) => ({
  fill: document.getElementById(id),
  label: document.getElementById(id + "Db"),
  db: -90,
}));
const bands = Array(bandCount).fill(0);
let timer;
let pending = false;
let changing = false;
let captureEnabled = false;
let appliedSource = null;

const analogMeter = document.getElementById("analog-meter");
const sweep = 104;
const ledCount = 40;

function gaugeAngle(fraction) {
  return (Math.min(1, Math.max(0, fraction)) - .5) * sweep;
}

function makeGauge(dial) {
  const channel = dial.dataset.channel;
  const isLevel = channel === "L" || channel === "R";
  const pivotY = 130;
  const radius = 91;
  const point = (angle, length) => {
    const radians = angle * Math.PI / 180;
    return [120 + Math.sin(radians) * length, pivotY - Math.cos(radians) * length];
  };
  const ticks = [];
  for (let i = 0; i <= 20; i++) {
    const angle = gaugeAngle(i / 20);
    const major = i % 4 === 0;
    const [x1, y1] = point(angle, radius);
    const [x2, y2] = point(angle, radius + (major ? 10 : 5));
    const color = i >= 16 ? "#9a2115" : "#342216";
    ticks.push(`<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${color}" stroke-width="${major ? 2.4 : 1.2}"/>`);
    if (major) {
      const [x, y] = point(angle, radius + 20);
      const value = isLevel ? i * 3 - 60 : i * 5;
      ticks.push(`<text x="${x}" y="${y}" dominant-baseline="middle" text-anchor="middle" font-size="12" fill="${color}">${value}</text>`);
    }
  }
  const [x1, y1] = point(gaugeAngle(0), radius);
  const [x2, y2] = point(gaugeAngle(1), radius);
  const [redX, redY] = point(gaugeAngle(.8), radius);
  dial.innerHTML = `<svg viewBox="0 0 240 104" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="${channel}">
    <g font-family="Bahnschrift, Segoe UI, sans-serif">
      <path d="M ${x1} ${y1} A ${radius} ${radius} 0 0 1 ${x2} ${y2}" fill="none" stroke="#49301a" stroke-width="2"/>
      <path d="M ${redX} ${redY} A ${radius} ${radius} 0 0 1 ${x2} ${y2}" fill="none" stroke="#a11e13" stroke-width="3"/>
      ${ticks.join("")}
      <text x="223" y="94" text-anchor="end" font-size="12" font-weight="700" fill="#51331a">${isLevel ? "dBFS" : "%"}</text>
      <line class="needle" x1="120" y1="${pivotY}" x2="120" y2="${pivotY - radius - 9}" stroke="#272016" stroke-width="2.1" stroke-linecap="round"/>
    </g>
  </svg>`;
  const needle = dial.querySelector(".needle");
  return fraction => needle.setAttribute("transform", `rotate(${gaugeAngle(fraction)} 120 ${pivotY})`);
}

const channelNeedles = Array.from(analogMeter.querySelectorAll(".rack-dial"), makeGauge);
const ledRows = Array.from(analogMeter.querySelectorAll(".rack-led-row"), row =>
  Array.from({ length: ledCount }, (_, index) => {
    const led = document.createElement("span");
    led.className = "rack-led";
    led.dataset.zone = index < 28 ? "green" : index < 34 ? "yellow" : "red";
    row.appendChild(led);
    return led;
  }));

function renderAnalog() {
  const stereo = tracks.map(track => Math.max(0, Math.min(1, (track.db + 60) / 60)));
  const split = bandCount / 2;
  const low = Math.max(...bands.slice(0, split));
  const high = Math.max(...bands.slice(split));
  [stereo[0], stereo[1], low, high].forEach((value, index) => channelNeedles[index](value));
  ledRows.forEach((row, index) => {
    const lit = Math.floor(stereo[index] * ledCount);
    row.forEach((led, index) => led.classList.toggle("lit", index < lit));
  });
}

document.addEventListener("vuOptionsChanged", () => {
  if (!analogMeter.hidden) renderAnalog();
});
renderAnalog();

function resetMeters() {
  tracks.forEach((track) => { track.db = -90; });
  bands.fill(0);
  render();
}

function setStatus(label, live = false) {
  statusLabel.textContent = label;
  statusLabel.classList.toggle("live", live);
}

function render(frame) {
  tracks.forEach((track, index) => {
    const target = Math.max(-90, Math.min(0, frame?.vu[index] ?? -90));
    track.db = Math.max(target, track.db - 3);
    if (!analogMeter.hidden) return;
    const fraction = Math.max(0, Math.round((track.db + 60) / 0.75) / 80);
    track.fill.style.clipPath = "inset(0 " + (100 - fraction * 100) + "% 0 0)";
    track.label.textContent = track.db < -60 ? "−∞" : Math.round(track.db);
  });
  bandFills.forEach((fill, index) => {
    bands[index] = Math.max(frame?.bands[index] ?? 0, bands[index] - 0.11);
    const segments = Math.round(Math.max(0, Math.min(1, bands[index])) * 10);
    fill.style.clipPath = "inset(" + (100 - segments * 10) + "% 0 0)";
  });
  if (!analogMeter.hidden) renderAnalog();
}

function isFrame(frame) {
  return Array.isArray(frame?.vu) && frame.vu.length === 2 &&
    frame.vu.every(Number.isFinite) && Array.isArray(frame.bands) &&
    frame.bands.length === bandCount && frame.bands.every(Number.isFinite);
}

async function poll() {
  if (document.hidden || pending || changing) return;
  pending = true;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 700);
  let delay = 50;
  try {
    const response = await fetch("http://127.0.0.1:18742/levels", { cache: "no-store", signal: controller.signal });
    if (!response.ok) throw new Error("Bridge unavailable");
    const frame = await response.json();
    if (!isFrame(frame)) throw new Error("Invalid audio frame");
    if (changing) return;
    const desiredSource = document.documentElement.dataset.audioSource || "output";
    if (frame.source !== "input" && frame.source !== "output" && desiredSource === "input") {
      setStatus("UPDATE AUDIO BRIDGE");
      captureButton.disabled = true;
      resetMeters();
      delay = 1000;
      return;
    }
    if (frame.source && appliedSource !== desiredSource && frame.source !== desiredSource) {
      const path = desiredSource === "input"
        ? "http://127.0.0.1:18742/capture/source/input"
        : "http://127.0.0.1:18742/capture/source/output";
      setStatus("SWITCHING SOURCE");
      captureButton.disabled = true;
      resetMeters();
      const update = await fetch(path, { method: "POST", headers: { "X-Xeneon-Control": "1" }, signal: controller.signal });
      if (!update.ok) {
        setStatus("SOURCE UNAVAILABLE");
        delay = 1000;
        return;
      }
      appliedSource = desiredSource;
      return;
    }
    appliedSource = desiredSource;
    const supportsControl = typeof frame.enabled === "boolean";
    captureEnabled = frame.enabled !== false;
    captureButton.disabled = !supportsControl;
    captureButton.textContent = captureEnabled ? "STOP" : "START";
    captureButton.setAttribute("aria-label", captureEnabled ? "캡처 중지" : "캡처 시작");
    captureButton.setAttribute("aria-pressed", String(captureEnabled));
    if (!captureEnabled) {
      setStatus("CAPTURE OFF");
      resetMeters();
      delay = 1000;
    } else {
      const label = frame.source === "input" ? "LIVE · INPUT" : "LIVE";
      setStatus(frame.ready ? label : "WAITING FOR AUDIO DEVICE", frame.ready);
      render(frame);
      if (!frame.ready || !frame.active) delay = frame.ready ? 160 : 750;
    }
  } catch {
    appliedSource = null;
    setStatus("START AUDIO BRIDGE");
    captureButton.disabled = true;
    captureButton.setAttribute("aria-pressed", "false");
    resetMeters();
    delay = 1000;
  } finally {
    pending = false;
    clearTimeout(timeout);
    if (!document.hidden) timer = setTimeout(poll, delay);
  }
}


captureButton.addEventListener("click", async () => {
  if (changing || captureButton.disabled) return;
  changing = true;
  captureButton.disabled = true;
  clearTimeout(timer);
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 1500);
  const options = { method: "POST", headers: { "X-Xeneon-Control": "1" }, signal: controller.signal };
  try {
    const response = captureEnabled
      ? await fetch("http://127.0.0.1:18742/capture/stop", options)
      : await fetch("http://127.0.0.1:18742/capture/start", options);
    if (!response.ok) throw new Error("Capture command failed");
  } catch {
    setStatus("CONTROL UNAVAILABLE");
  } finally {
    changing = false;
    clearTimeout(timeout);
    clearTimeout(timer);
    poll();
  }
});

document.addEventListener("visibilitychange", () => {
  clearTimeout(timer);
  if (!document.hidden) poll();
});
poll();
