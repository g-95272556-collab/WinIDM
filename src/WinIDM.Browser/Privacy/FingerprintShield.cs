using System.Text.Json;

namespace WinIDM.Browser;

/// <summary>
/// Fingerprinting protection in the style of Brave "farbling": the values fingerprinting scripts read back are
/// changed by a tiny amount that is stable for one site in one browser session and different everywhere else, so
/// a script cannot link visits across sites. It is a page script, so it is weaker than Brave's engine-level change.
/// <para>
/// Only read-back paths are touched: canvas <c>toDataURL</c>/<c>toBlob</c>/<c>getImageData</c>, the WebGL GPU
/// strings, <c>OfflineAudioContext</c> rendering (never used to play sound) and <c>hardwareConcurrency</c>.
/// Video and audio elements, Media Source Extensions, EME and live Web Audio playback are left as they are, so
/// streams, previews and media capture behave exactly as without the shield.
/// </para>
/// </summary>
public static class FingerprintShield
{
    /// <summary>The document-start script.</summary>
    /// <param name="sessionKey">Random per browser window; a new window gives every site new values.</param>
    /// <param name="exemptSites">Sites (host or registrable domain) where the shield is switched off.</param>
    public static string BuildScript(string sessionKey, IEnumerable<string> exemptSites) =>
        Template
            .Replace("__KEY__", JsonSerializer.Serialize(sessionKey), StringComparison.Ordinal)
            .Replace("__EXEMPT__", JsonSerializer.Serialize(exemptSites.Select(HostUtil.NormalizeHost).Distinct().ToArray()), StringComparison.Ordinal);

    private const string Template = """
(function(){
  'use strict';
  var KEY = __KEY__, EXEMPT = __EXEMPT__;
  var host = (location.hostname || '').toLowerCase();
  for (var i = 0; i < EXEMPT.length; i++) {
    if (host === EXEMPT[i] || host.slice(-(EXEMPT[i].length + 1)) === '.' + EXEMPT[i]) return;
  }
  var labels = host.split('.');
  var site = labels.length > 2 ? labels.slice(-2).join('.') : host;

  function fnv(s) { var x = 2166136261; for (var j = 0; j < s.length; j++) { x ^= s.charCodeAt(j); x = Math.imul(x, 16777619); } return x >>> 0; }
  var seed = fnv(KEY + '|' + site);
  function rnd(n) { var s = (seed ^ Math.imul(n | 0, 2654435761)) >>> 0; s ^= s << 13; s >>>= 0; s ^= s >>> 17; s ^= s << 5; return s >>> 0; }

  // Replaced functions keep the original name and still print as native code, so tamper checks keep working.
  var natives = new WeakMap(), fnToString = Function.prototype.toString;
  function patch(obj, name, fn) {
    var d = obj && Object.getOwnPropertyDescriptor(obj, name);
    if (!d || typeof d.value !== 'function') return null;
    natives.set(fn, fnToString.call(d.value));
    try { Object.defineProperty(fn, 'name', { value: name, configurable: true }); } catch (e) {}
    d.value = fn;
    Object.defineProperty(obj, name, d);
    return fn;
  }
  var toStringShim = function toString() { return natives.has(this) ? natives.get(this) : fnToString.call(this); };
  natives.set(toStringShim, fnToString.call(fnToString));
  try { Object.defineProperty(Function.prototype, 'toString', { value: toStringShim, writable: true, configurable: true, enumerable: false }); } catch (e) {}

  // ---- canvas: flip the lowest bit of one channel in about 64 pixels, the same pixels every time
  function noise(data, w, h) {
    var n = data.length >> 2; if (!n) return;
    var step = Math.max(1, Math.floor(n / 64));
    for (var p = rnd(w * 31 + h) % step; p < n; p += step) { var k = p * 4 + rnd(p) % 3; data[k] ^= 1; }
  }
  var C2D = window.CanvasRenderingContext2D && CanvasRenderingContext2D.prototype;
  var getImageData = C2D && C2D.getImageData;
  if (getImageData) {
    patch(C2D, 'getImageData', function () {
      var r = getImageData.apply(this, arguments);
      try { noise(r.data, r.width, r.height); } catch (e) {}
      return r;
    });
  }
  // A noisy copy; null (use the original canvas) when it is empty, huge or cross-origin tainted.
  function farbled(canvas) {
    var w = canvas.width, h = canvas.height;
    if (!getImageData || !w || !h || w * h > 16777216) return null;
    var copy = document.createElement('canvas'); copy.width = w; copy.height = h;
    var ctx = copy.getContext('2d'); ctx.drawImage(canvas, 0, 0);
    var img = getImageData.call(ctx, 0, 0, w, h);
    noise(img.data, w, h); ctx.putImageData(img, 0, 0);
    return copy;
  }
  var CE = window.HTMLCanvasElement && HTMLCanvasElement.prototype;
  if (CE) {
    var toDataURL = CE.toDataURL, toBlob = CE.toBlob;
    if (toDataURL) patch(CE, 'toDataURL', function () {
      var c = null; try { c = farbled(this); } catch (e) {}
      return toDataURL.apply(c || this, arguments);
    });
    if (toBlob) patch(CE, 'toBlob', function () {
      var c = null; try { c = farbled(this); } catch (e) {}
      return toBlob.apply(c || this, arguments);
    });
  }

  // ---- WebGL: hide the exact GPU model
  ['WebGLRenderingContext', 'WebGL2RenderingContext'].forEach(function (n) {
    var P = window[n] && window[n].prototype; if (!P) return;
    var getParameter = P.getParameter;
    patch(P, 'getParameter', function (p) {
      if (p === 0x9245) return 'Google Inc.';
      if (p === 0x9246) return 'ANGLE (Generic GPU)';
      return getParameter.apply(this, arguments);
    });
  });

  // ---- audio: only offline rendering, which never reaches the speakers
  var OAC = window.OfflineAudioContext && OfflineAudioContext.prototype;
  if (OAC && OAC.startRendering) {
    var startRendering = OAC.startRendering;
    patch(OAC, 'startRendering', function () {
      return startRendering.apply(this, arguments).then(function (buf) {
        try {
          for (var c = 0; c < buf.numberOfChannels; c++) {
            var d = buf.getChannelData(c);
            for (var i = rnd(c) % 97; i < d.length; i += 97) d[i] += (rnd(i) & 1 ? 1 : -1) * 1e-7;
          }
        } catch (e) {}
        return buf;
      });
    });
  }

  // ---- CPU core count: a value between 2 and the real count
  try {
    var cores = navigator.hardwareConcurrency;
    if (cores > 2) {
      var fake = 2 + rnd(7) % (cores - 1);
      Object.defineProperty(Navigator.prototype, 'hardwareConcurrency', { get: function () { return fake; }, configurable: true, enumerable: true });
    }
  } catch (e) {}
})();
""";
}
