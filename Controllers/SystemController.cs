using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace CustomerDepositRemaining.Controllers
{
    /// <summary>health/version + PWA (manifest, service worker) เหมือนระบบเดิม</summary>
    public class SystemController : Controller
    {
        private readonly IConfiguration _config;
        public SystemController(IConfiguration config) => _config = config;

        private object VersionInfo() => new
        {
            version = _config["AppVersion"] ?? "dev",
            commit = _config["BuildCommit"] ?? "unknown",
            build_time = _config["BuildTime"] ?? "unknown"
        };

        [HttpGet("/health")]
        public IActionResult Health()
        {
            Response.Headers.CacheControl = "no-store";
            return Json(new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["version"] = _config["AppVersion"] ?? "dev",
                ["commit"] = _config["BuildCommit"] ?? "unknown",
                ["build_time"] = _config["BuildTime"] ?? "unknown"
            });
        }

        [HttpGet("/version")]
        public IActionResult Version()
        {
            Response.Headers.CacheControl = "no-store";
            return Json(VersionInfo());
        }

        [HttpGet("/manifest.webmanifest")]
        public IActionResult Manifest()
        {
            var basePath = Request.PathBase.Value ?? "";
            string P(string path) => $"{basePath}{path}";
            var manifest = new Dictionary<string, object?>
            {
                ["id"] = P("/"),
                ["name"] = "Lens Voucher",
                ["short_name"] = "Lens Voucher",
                ["description"] = "ตรวจสอบยอดคงเหลือ ประวัติการใช้เวาเชอร์ และคำสั่งซื้อ",
                ["start_url"] = P("/"),
                ["scope"] = P("/"),
                ["display"] = "standalone",
                ["background_color"] = "#f4f7fb",
                ["theme_color"] = "#0b3455",
                ["lang"] = "th",
                ["categories"] = new[] { "business", "finance", "productivity" },
                ["icons"] = new object[]
                {
                    new { src = P("/lens/img/icon-192.png"), sizes = "192x192", type = "image/png", purpose = "any" },
                    new { src = P("/lens/img/icon-512.png"), sizes = "512x512", type = "image/png", purpose = "any" },
                    new { src = P("/lens/img/maskable-icon-512.png"), sizes = "512x512", type = "image/png", purpose = "maskable" }
                }
            };
            Response.Headers.CacheControl = "no-cache, must-revalidate";
            return Content(JsonSerializer.Serialize(manifest), "application/manifest+json", Encoding.UTF8);
        }

        [HttpGet("/sw.js")]
        public IActionResult ServiceWorker()
        {
            var basePath = Request.PathBase.Value ?? "";
            var version = $"{_config["AppVersion"] ?? "dev"}-{_config["BuildCommit"] ?? "local"}";
            var cacheVersion = System.Text.RegularExpressions.Regex.Replace(version, "[^a-zA-Z0-9._-]", "-");

            var source = $$"""
            const CACHE_PREFIX = "lens-voucher-pwa-";
            const CACHE_NAME = CACHE_PREFIX + {{JsonSerializer.Serialize(cacheVersion)}};
            const BASE_PATH = {{JsonSerializer.Serialize(basePath)}};
            const appPath = (path) => BASE_PATH + path;
            const APP_SHELL_URL = new URL(appPath("/"), self.location.origin).href;
            const OFFLINE_URL = new URL(appPath("/lens/offline.html"), self.location.origin).href;
            const PRECACHE_URLS = [
              OFFLINE_URL,
              appPath("/manifest.webmanifest"),
              appPath("/lens/img/favicon.svg"),
              appPath("/lens/img/icon-192.png"),
              appPath("/lens/img/icon-512.png"),
              appPath("/lens/img/maskable-icon-512.png"),
              appPath("/lens/img/apple-touch-icon.png"),
            ];

            self.addEventListener("install", (event) => {
              event.waitUntil(caches.open(CACHE_NAME).then((cache) => cache.addAll(PRECACHE_URLS)));
            });

            self.addEventListener("activate", (event) => {
              event.waitUntil(
                caches.keys()
                  .then((keys) => Promise.all(
                    keys.filter((key) => key.startsWith(CACHE_PREFIX) && key !== CACHE_NAME).map((key) => caches.delete(key)),
                  ))
                  .then(() => self.clients.claim()),
              );
            });

            function isNetworkOnly(url) {
              // หน้าข้อมูล/PDF/ผู้ดูแล ต้องดึงสดเสมอ ห้าม cache
              return (
                url.pathname === appPath("/health") ||
                url.pathname === appPath("/version") ||
                url.pathname.startsWith(appPath("/Document/")) ||
                url.pathname.startsWith(appPath("/Admin")) ||
                url.pathname.startsWith(appPath("/Account/")) ||
                url.pathname.startsWith(appPath("/Voucher")) ||
                url.pathname.startsWith(appPath("/Order"))
              );
            }

            function isStaticAsset(url) {
              return url.pathname.startsWith(appPath("/lens/")) || url.pathname === appPath("/manifest.webmanifest");
            }

            async function networkFirstNavigation(request) {
              try {
                return await fetch(request);
              } catch {
                const cache = await caches.open(CACHE_NAME);
                return (await cache.match(OFFLINE_URL));
              }
            }

            async function cacheFirstStatic(request) {
              const cached = await caches.match(request);
              if (cached) return cached;
              const response = await fetch(request);
              if (response.ok) {
                const cache = await caches.open(CACHE_NAME);
                await cache.put(request, response.clone());
              }
              return response;
            }

            self.addEventListener("fetch", (event) => {
              const request = event.request;
              if (request.method !== "GET") return;
              const url = new URL(request.url);
              if (url.origin !== self.location.origin) return;
              if (request.mode === "navigate") { event.respondWith(networkFirstNavigation(request)); return; }
              if (isNetworkOnly(url)) return;
              if (isStaticAsset(url)) event.respondWith(cacheFirstStatic(request));
            });
            """;

            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            Response.Headers["Service-Worker-Allowed"] = $"{basePath}/";
            Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'";
            return Content(source, "application/javascript", Encoding.UTF8);
        }
    }
}
