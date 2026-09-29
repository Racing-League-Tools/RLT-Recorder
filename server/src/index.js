// Update check and usage counter for RLT Recorder.
//
// GET /v1/check?v=0.2.0&os=linux&arch=arm64&flavor=gui&id=<32 hex>
//   -> { "latest": "0.3.0", "url": "https://github.com/.../releases/tag/v0.3.0" }
//
// Each check is also counted: one row per install per day, holding exactly the
// query fields above. No IP address or other request data is stored. A check
// that fails validation still gets an answer; it just is not counted.

const REPO = "Racing-League-Tools/RLT-Recorder";

const OS = new Set(["windows", "macos", "linux"]);
const ARCH = new Set(["x64", "arm64", "arm", "x86"]);
const FLAVOR = new Set(["gui", "cli"]);
const VERSION = /^\d{1,4}\.\d{1,4}\.\d{1,4}$/;
const INSTALL_ID = /^[0-9a-f]{32}$/;

// GitHub allows 60 unauthenticated API calls an hour per address, and every
// recorder asks at start-up, so the answer is cached rather than fetched per check.
const RELEASE_CACHE_SECONDS = 3600;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (url.pathname !== "/v1/check")
      return new Response("Not found", { status: 404 });
    if (request.method !== "GET")
      return new Response("Method not allowed", { status: 405 });

    const check = parseCheck(url.searchParams);
    if (check)
      ctx.waitUntil(record(env, check)); // counted after the answer goes out

    const latest = await latestRelease(ctx);
    return Response.json(latest ?? {}, { headers: { "Cache-Control": "no-store" } });
  },
};

function parseCheck(q) {
  const check = {
    id: q.get("id") ?? "",
    version: q.get("v") ?? "",
    os: q.get("os") ?? "",
    arch: q.get("arch") ?? "",
    flavor: q.get("flavor") ?? "",
  };

  const valid = INSTALL_ID.test(check.id) && VERSION.test(check.version)
    && OS.has(check.os) && ARCH.has(check.arch) && FLAVOR.has(check.flavor);

  return valid ? check : null;
}

async function record(env, check) {
  // A second check from the same install on the same day updates its row, so
  // restarts do not inflate the numbers and the table stays small.
  await env.DB.prepare(
    `INSERT INTO checks (day, install_id, version, os, arch, flavor)
     VALUES (date('now'), ?1, ?2, ?3, ?4, ?5)
     ON CONFLICT (day, install_id) DO UPDATE SET
       version = excluded.version, os = excluded.os,
       arch = excluded.arch, flavor = excluded.flavor`
  ).bind(check.id, check.version, check.os, check.arch, check.flavor).run();
}

async function latestRelease(ctx) {
  const cache = caches.default;
  const key = new Request("https://cache.internal/latest-release");

  const cached = await cache.match(key);
  if (cached)
    return cached.json();

  // The newest published release, pre-releases included: until 1.0 every
  // release is one, and /releases/latest skips them.
  const response = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=10`, {
    headers: { "User-Agent": "rlt-recorder-update-check", Accept: "application/vnd.github+json" },
  });
  if (!response.ok)
    return null;

  const release = (await response.json()).find((r) => !r.draft);
  if (!release)
    return null;

  const latest = { latest: release.tag_name.replace(/^v/, ""), url: release.html_url };

  ctx.waitUntil(cache.put(key, new Response(JSON.stringify(latest), {
    headers: { "Content-Type": "application/json", "Cache-Control": `max-age=${RELEASE_CACHE_SECONDS}` },
  })));

  return latest;
}
