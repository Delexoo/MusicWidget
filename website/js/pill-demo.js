(function () {
  var tracks = [
    { title: "Bloom", artist: "Troye Sivan" },
    { title: "On Top Now", artist: "NCK" },
    { title: "Blinding Lights", artist: "The Weeknd" },
    { title: "Levitating", artist: "Dua Lipa" },
    { title: "Dreams", artist: "Fleetwood Mac" },
    { title: "Heat Waves", artist: "Glass Animals" }
  ];

  var pillInfo = document.getElementById("pill-info");
  var pillTitle = document.getElementById("pill-title");
  var pillArtist = document.getElementById("pill-artist");
  var beatsPill = document.getElementById("beats-pill");
  if (!beatsPill || !pillInfo || !pillTitle || !pillArtist) return;

  var currentIndex = 0;
  var fadeMs = window.matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 220;
  var cycleMs = 6000;
  var timer = null;
  var paused = false;

  function pickNextIndex() {
    var next = currentIndex;
    while (next === currentIndex) {
      next = Math.floor(Math.random() * tracks.length);
    }
    return next;
  }

  function updateAria() {
    var t = tracks[currentIndex];
    beatsPill.setAttribute("aria-label", "MusicWidget player - " + t.title + " by " + t.artist);
  }

  function cycleTrack() {
    if (paused) return;
    var nextIndex = pickNextIndex();
    function apply() {
      currentIndex = nextIndex;
      pillTitle.textContent = tracks[currentIndex].title;
      pillArtist.textContent = tracks[currentIndex].artist;
      pillInfo.classList.remove("is-changing");
      updateAria();
    }
    if (fadeMs === 0) {
      apply();
      return;
    }
    pillInfo.classList.add("is-changing");
    window.setTimeout(apply, fadeMs);
  }

  function setPaused(next) {
    paused = next;
  }

  beatsPill.addEventListener("mouseenter", function () { setPaused(true); });
  beatsPill.addEventListener("mouseleave", function () { setPaused(false); });
  beatsPill.addEventListener("focusin", function () { setPaused(true); });
  beatsPill.addEventListener("focusout", function () { setPaused(false); });

  updateAria();
  timer = window.setInterval(cycleTrack, cycleMs);
})();
