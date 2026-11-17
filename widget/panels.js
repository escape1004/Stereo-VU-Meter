(function () {
"use strict";

const mediaPanel = document.getElementById("media-panel");
const songTitle = document.getElementById("song-title");
const songArtist = document.getElementById("song-artist");
const albumCover = document.getElementById("album-cover");
const coverPlaceholder = document.getElementById("cover-placeholder");
const playPause = document.getElementById("play-pause");
const playIcon = playPause.querySelector(".transport-icon");
let mediaProvider;
let artworkInput;
let artworkTimer;
let artworkRequest;
const seekRow = document.getElementById("seek-row");
const seekSlider = document.getElementById("media-seek");
let timeline;
let pendingSeek;
let confirmedSeekAt = 0;
let scrubbing = false;
let timelineRevision = 0;

function trackKey(input) {
  return encodeURIComponent(JSON.stringify({ title: input.title, artist: input.artist, refresh: input.refresh }));
}

function paintSeekTrack() {
  const start = Number(seekSlider.min);
  const span = Math.max(1, Number(seekSlider.max) - start);
  seekSlider.style.setProperty("--seek-progress", ((Number(seekSlider.value) - start) / span * 100) + "%");
}

function refreshSeekBar() {
  if (!timeline || scrubbing || pendingSeek || mediaPanel.hidden) return;
  const elapsed = timeline.playing && mediaProvider?.isPlaying !== false
    ? Math.max(0, Date.now() - timeline.updated) : 0;
  seekSlider.value = Math.max(timeline.start, Math.min(timeline.end, timeline.position + elapsed));
  paintSeekTrack();
}

function setTimeline(value) {
  const valid = value && [value.start, value.end, value.position, value.updated].every(Number.isFinite)
    && value.end > value.start;
  timeline = valid ? value : null;
  seekRow.classList.toggle("placeholder", !timeline);
  seekSlider.disabled = !timeline?.canSeek || !!pendingSeek;
  if (!timeline) {
    seekSlider.min = 0;
    seekSlider.max = 1;
    seekSlider.value = 0;
    paintSeekTrack();
    return;
  }
  seekSlider.min = timeline.start;
  seekSlider.max = timeline.end;
  seekSlider.step = 1;
  refreshSeekBar();
}

seekSlider.addEventListener("input", () => {
  scrubbing = true;
  paintSeekTrack();
});
seekSlider.addEventListener("pointercancel", () => { scrubbing = false; refreshSeekBar(); });
seekSlider.addEventListener("blur", () => { scrubbing = false; });
function finishSeek(seek) {
  if (pendingSeek !== seek) return;
  clearTimeout(seek.deadline);
  pendingSeek = null;
  seekSlider.disabled = !timeline?.canSeek;
  refreshSeekBar();
  if (artworkInput === seek.input && !mediaPanel.hidden) {
    clearTimeout(artworkTimer);
    artworkTimer = setTimeout(() => readWindowsArtwork(seek.input), 21000);
  }
}

seekSlider.addEventListener("change", async () => {
  scrubbing = false;
  if (pendingSeek || !timeline?.canSeek || !artworkInput) return;
  paintSeekTrack();
  const input = artworkInput;
  const seek = { input, target: Math.round(Number(seekSlider.value)), started: Date.now() };
  pendingSeek = seek;
  seek.deadline = setTimeout(() => finishSeek(seek), 14000);
  seekSlider.disabled = true;
  clearTimeout(artworkTimer);
  artworkRequest?.abort();
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 2500);
  try {
    const url = "http://127.0.0.1:18742/seek?track=" + trackKey(input) + "&ms=" + seek.target;
    const options = { method: "POST", headers: { "X-Xeneon-Control": "1" }, signal: controller.signal };
    let response = await fetch(url, options);
    for (let retry = 0; response.status === 409 && retry < 5 && pendingSeek === seek; retry++) {
      await new Promise(resolve => setTimeout(resolve, 350));
      if (pendingSeek !== seek) return;
      response = await fetch(url, options);
    }
    if (pendingSeek !== seek) return;
    if (response.status === 202) readWindowsArtwork(input, 0, seek);
    else finishSeek(seek);
  } catch {
    finishSeek(seek);
  } finally {
    clearTimeout(timeout);
  }
});
setInterval(refreshSeekBar, 400);

function artworkSquareSize(url) {
  if (!url) return Promise.resolve(0);
  const image = new Image();
  image.src = url;
  return image.decode().then(() => Math.min(image.naturalWidth, image.naturalHeight), () => 0);
}

function setArtwork(url) {
  if (url) albumCover.src = url;
  else albumCover.removeAttribute("src");
  albumCover.hidden = !url;
  coverPlaceholder.hidden = !!url;
}

function decodeMetadata(value) {
  return new TextDecoder().decode(Uint8Array.from(atob(value), char => char.charCodeAt(0)));
}

function normalizeMetadata(value) {
  return value.normalize("NFKC").trim().replace(/\s+/g, " ").toLowerCase();
}

async function readWindowsArtwork(input, attempts = 0, seek = null) {
  if (artworkInput !== input || mediaPanel.hidden || !input.title ||
      (pendingSeek && pendingSeek !== seek) || (seek && pendingSeek !== seek)) return;
  const controller = new AbortController();
  artworkRequest = controller;
  const timeout = setTimeout(() => controller.abort(), 2500);
  try {
    const key = trackKey(input);
    const response = await fetch("http://127.0.0.1:18742/artwork?track=" + key,
      { cache: "no-store", signal: controller.signal });
    if (response.status === 202 && attempts < (seek ? 30 : 12) && artworkInput === input) {
      artworkTimer = setTimeout(() => readWindowsArtwork(input, attempts + 1, seek), seek ? 300 : 900);
      return;
    }
    if (response.status !== 200) {
      if (seek && response.status !== 202) finishSeek(seek);
      return;
    }
    const result = await response.json();
    const image = result.artwork;
    if (normalizeMetadata(decodeMetadata(result.title)) !== normalizeMetadata(input.title)) return;
    const windowsArtist = normalizeMetadata(decodeMetadata(result.artist));
    if (input.artist && windowsArtist && windowsArtist !== normalizeMetadata(input.artist)) return;
    if (artworkInput !== input || mediaPanel.hidden ||
        (pendingSeek && pendingSeek !== seek) || (seek && pendingSeek !== seek)) return;
    if (input.restart && !seek) {
      const next = result.timeline;
      const stillOld = next && next.position > next.start + 5000 &&
        (next.updated < input.restart.started || Math.abs(next.position - input.restart.position) < 5000);
      if (stillOld && input.restart.retries < 3) {
        input.restart.retries++;
        input.refresh = Date.now() + "-" + ++timelineRevision;
        artworkTimer = setTimeout(() => readWindowsArtwork(input), 450);
        return;
      }
      input.restart = null;
    }
    if (seek) {
      const next = result.timeline;
      const confirmed = result.seekApplied === true && next?.position === seek.target &&
        next.updated >= seek.started - 1000;
      if (result.seekApplied === false) {
        finishSeek(seek);
      } else if (!confirmed) {
        if (attempts < 30) artworkTimer = setTimeout(() => readWindowsArtwork(input, attempts + 1, seek), 300);
        return;
      } else {
        confirmedSeekAt = next.updated;
        setTimeline(next);
        finishSeek(seek);
      }
    } else if (!result.timeline || result.timeline.updated >= confirmedSeekAt) {
      setTimeline(result.timeline);
    }
    if (timeline && !seek) artworkTimer = setTimeout(() => readWindowsArtwork(input), 21000);
    if (!/^data:image\/(png|jpeg|webp|gif);base64,[A-Za-z0-9+/=]+$/.test(image)) return;
    const [windowsPixels, icuePixels] = await Promise.all([artworkSquareSize(image), artworkSquareSize(input.thumbnail)]);
    if (artworkInput === input && !mediaPanel.hidden && windowsPixels > icuePixels) {
      setArtwork(image);
    }
  } catch {
    // The iCUE image remains visible if Windows cannot provide a better one.
  } finally {
    clearTimeout(timeout);
    if (artworkRequest === controller) artworkRequest = null;
  }
}

function updateArtwork(title, artist, thumbnail) {
  if (artworkInput?.title === title && artworkInput.artist === artist && artworkInput.thumbnail === thumbnail) return;
  if ((pendingSeek || artworkInput?.restart) && artworkInput?.title === title && artworkInput.artist === artist) {
    artworkInput.thumbnail = thumbnail;
    return;
  }
  if (pendingSeek) finishSeek(pendingSeek);
  clearTimeout(artworkTimer);
  artworkRequest?.abort();
  if (artworkInput?.title !== title || artworkInput?.artist !== artist) confirmedSeekAt = 0;
  artworkInput = { title, artist, thumbnail };
  scrubbing = false;
  setTimeline(null);
  setArtwork(thumbnail);
  artworkTimer = setTimeout(() => readWindowsArtwork(artworkInput), 300);
}

const scrollingLabels = [songTitle, songArtist];

function updateTextOverflow() {
  for (const label of scrollingLabels) {
    const line = label.firstElementChild;
    const overflow = label.clientWidth > 0 ? line.offsetWidth - label.clientWidth : 0;
    if (overflow <= 1) {
      line.classList.remove("sliding");
      continue;
    }
    line.style.setProperty("--track-offset", "-" + overflow + "px");
    line.style.setProperty("--track-duration", Math.max(2.8, overflow / 22 + 1) + "s");
    line.classList.add("sliding");
  }
}

if (typeof ResizeObserver === "function") {
  const textResize = new ResizeObserver(updateTextOverflow);
  for (const label of scrollingLabels) {
    textResize.observe(label);
    textResize.observe(label.firstElementChild);
  }
}
window.addEventListener("resize", updateTextOverflow);

function setTrackText(label, value) {
  const line = label.firstElementChild;
  if (line.textContent === value) return;
  line.classList.remove("sliding");
  line.textContent = value;
  // Measure the new text before restarting from the leading edge.
  void line.offsetWidth;
}

function renderMedia() {
  if (mediaPanel.hidden || !mediaProvider) return;
  const title = mediaProvider.songName || "";
  const artist = mediaProvider.artist || "";
  setTrackText(songTitle, title || "재생 정보 없음");
  setTrackText(songArtist, artist);
  updateTextOverflow();
  const thumbnail = mediaProvider.thumbnail || "";
  const cover = /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(thumbnail) ? thumbnail : "";
  updateArtwork(title, artist, cover);
  const playing = mediaProvider.isPlaying === true;
  playIcon.classList.toggle("pause", playing);
  playIcon.classList.toggle("play", !playing);
  playPause.setAttribute("aria-label", playing ? "일시정지" : "재생");
}

function refreshAfterPreviousTrack() {
  if (!artworkInput?.title || mediaPanel.hidden) return;
  if (pendingSeek) finishSeek(pendingSeek);
  const wasPastStart = timeline && Number(seekSlider.value) > timeline.start + 5000;
  const input = {
    ...artworkInput,
    refresh: Date.now() + "-" + ++timelineRevision,
    restart: wasPastStart ? { started: Date.now(), position: Number(seekSlider.value), retries: 0 } : null,
  };
  clearTimeout(artworkTimer);
  artworkRequest?.abort();
  artworkInput = input;
  confirmedSeekAt = 0;
  scrubbing = false;
  if (wasPastStart) setTimeline(null);
  artworkTimer = setTimeout(() => readWindowsArtwork(input), 450);
}

function bindMedia() {
  const provider = window.plugins?.Mediadataprovider;
  if (provider && provider !== mediaProvider) {
    mediaProvider = provider;
    ["songNameChanged", "artistChanged", "thumbnailChanged", "isPlayingChanged"]
      .forEach(name => provider[name]?.connect?.(renderMedia));
    for (const [id, action] of [
      ["previous-track", "triggerPreviousTrack"],
      ["play-pause", "triggerPlayPause"],
      ["next-track", "triggerNextTrack"],
    ]) {
      const button = document.getElementById(id);
      button.disabled = false;
      button.onclick = () => {
        mediaProvider[action]();
        if (id === "previous-track") refreshAfterPreviousTrack();
      };
    }
  }
  renderMedia();
}

document.addEventListener("vuOptionsChanged", () => {
  if (!mediaPanel.hidden) bindMedia();
  else {
    clearTimeout(artworkTimer);
    artworkRequest?.abort();
    artworkInput = undefined;
    if (pendingSeek) finishSeek(pendingSeek);
    confirmedSeekAt = 0;
    setTimeline(null);
  }
});

const mediaHooks = { onInitialized: bindMedia };
if (typeof pluginMediadataproviderEvents === "object" && pluginMediadataproviderEvents) {
  Object.assign(pluginMediadataproviderEvents, mediaHooks);
} else if (typeof pluginMediadataproviderEvents !== "undefined") {
  pluginMediadataproviderEvents = mediaHooks;
} else {
  window.pluginMediadataproviderEvents = mediaHooks;
}
})();
