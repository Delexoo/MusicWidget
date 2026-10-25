(function () {
  const ASSET = "MusicWidget-Setup-x64.exe";
  const repoMeta = document.querySelector('meta[name="beats-repo"]');
  const repo = repoMeta && repoMeta.content && !repoMeta.content.includes("YOUR_USERNAME")
    ? repoMeta.content.trim()
    : null;

  const downloadBtn = document.getElementById("download-btn");
  const downloadBtnSection = document.getElementById("download-btn-section");
  const githubBtnSecondary = document.getElementById("github-btn-secondary");
  const githubBtnSection = document.getElementById("github-btn-section");
  const versionMetaSection = document.getElementById("version-meta-section");
  const versionMetaHome = document.getElementById("version-meta-home");

  function setVersionMeta(text) {
    if (versionMetaSection) versionMetaSection.textContent = text;
    if (versionMetaHome) versionMetaHome.textContent = text;
  }

  function releaseDownloadUrl(tagOrVersion) {
    var v = String(tagOrVersion || "").replace(/^v/i, "");
    if (!v) {
      return "https://github.com/" + repo + "/releases/latest/download/" + ASSET;
    }
    return "https://github.com/" + repo + "/releases/download/v" + v + "/" + ASSET;
  }

  function wireDownload(url) {
    if (!url) return;
    [downloadBtn, downloadBtnSection].forEach(function (btn) {
      if (!btn) return;
      btn.href = url;
      btn.setAttribute("download", ASSET);
    });
  }

  function formatReleaseMeta(data) {
    if (!data || !data.tag_name) return null;
    var v = data.tag_name.replace(/^v/i, "");
    var asset = (data.assets || []).find(function (a) { return a.name === ASSET; });
    var size = asset && asset.size ? " | " + Math.round(asset.size / (1024 * 1024)) + " MB" : "";
    return "v" + v + size + " | Windows 10 / 11 | 64-bit";
  }

  function formatLocalMeta(data) {
    if (!data || !data.version) return null;
    var size = data.sizeMb ? " | " + data.sizeMb + " MB" : "";
    return "v" + data.version + size + " | Windows 10 / 11 | 64-bit";
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

  if (!repo) {
    if (githubBtnSecondary) githubBtnSecondary.style.display = "none";
    if (githubBtnSection) githubBtnSection.style.display = "none";
    return;
  }

  var repoUrl = "https://github.com/" + repo;
  if (githubBtnSecondary) githubBtnSecondary.href = repoUrl;
  if (githubBtnSection) githubBtnSection.href = repoUrl;

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
        // version.json is ahead of the latest GitHub release — avoid a 404.
        downloadVersion = apiVer;
      } else if (!downloadVersion) {
        downloadVersion = apiVer;
      }
    }

    if (downloadVersion) {
      wireDownload(releaseDownloadUrl(downloadVersion));
      if (api && api.tag_name && downloadVersion === api.tag_name.replace(/^v/i, "")) {
        var meta = formatReleaseMeta(api);
        if (meta) setVersionMeta(meta);
      } else if (local) {
        var localMeta = formatLocalMeta(local);
        if (localMeta) setVersionMeta(localMeta);
      }
    } else {
      wireDownload(releaseDownloadUrl(null));
    }
  });
})();
