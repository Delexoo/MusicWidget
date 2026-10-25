(function () {
  var ASSET = "MusicWidget-Setup-x64.exe";
  var repo = (document.querySelector('meta[name="beats-repo"]') || {}).content || "Delexoo/beats";
  var dl = document.getElementById("download-link");

  function releaseDownloadUrl(version) {
    var v = String(version || "").replace(/^v/i, "");
    if (!v) {
      return "https://github.com/" + repo + "/releases/latest/download/" + ASSET;
    }
    return "https://github.com/" + repo + "/releases/download/v" + v + "/" + ASSET;
  }

  function parseVersionParts(value) {
    return String(value || "").replace(/^v/i, "").split(".").map(function (n) {
      return parseInt(n, 10) || 0;
    });
  }

  function isNewerVersion(a, b) {
    var av = parseVersionParts(a);
    var bv = parseVersionParts(b);
    for (var i = 0; i < 3; i++) {
      if (av[i] > bv[i]) return true;
      if (av[i] < bv[i]) return false;
    }
    return false;
  }

  function setDownloadUrl(version) {
    if (dl) dl.href = releaseDownloadUrl(version);
  }

  Promise.all([
    fetch("version.json").then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; }),
    fetch("https://api.github.com/repos/" + repo + "/releases/latest").then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; })
  ]).then(function (results) {
    var local = results[0];
    var api = results[1];
    var localVer = local && local.version ? local.version : null;
    var downloadVersion = localVer;

    if (api && api.tag_name) {
      var apiVer = api.tag_name.replace(/^v/i, "");
      if (localVer && isNewerVersion(localVer, api.tag_name)) {
        downloadVersion = apiVer;
      } else if (!downloadVersion) {
        downloadVersion = apiVer;
      }
    }

    setDownloadUrl(downloadVersion);
  });

  var links = document.querySelectorAll(".toc a");
  var sections = [];
  links.forEach(function (a) {
    var el = document.getElementById(a.getAttribute("href").slice(1));
    if (el) sections.push({ link: a, el: el });
  });

  function scrollToId(id) {
    if (!id) return;
    var el = document.getElementById(id);
    if (!el) return;
    el.scrollIntoView({ behavior: "smooth", block: "start" });
    try { history.replaceState(null, "", "#" + id); } catch (_) { /* noop */ }
  }

  links.forEach(function (a) {
    a.addEventListener("click", function (e) {
      var href = a.getAttribute("href") || "";
      if (!href.startsWith("#")) return;
      var id = href.slice(1);
      e.preventDefault();
      scrollToId(id);
    });
  });

  function onScroll() {
    var y = window.scrollY + 100;
    var cur = sections[0];
    sections.forEach(function (s) {
      if (s.el.offsetTop <= y) cur = s;
    });
    links.forEach(function (l) { l.classList.remove("active"); });
    if (cur) cur.link.classList.add("active");
  }

  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();
})();
