# Update check server

A Cloudflare Worker with a D1 (SQLite) database. The recorder asks it once at
start-up and then daily whether a newer release exists; the Worker answers from
GitHub's release list (cached for an hour) and counts the check.

```
GET /v1/check?v=0.2.0&os=linux&arch=arm64&flavor=gui&id=<32 hex>
→ { "latest": "0.2.0", "url": "https://github.com/Racing-League-Tools/RLT-Recorder/releases/tag/v0.2.0" }
```

What is stored is exactly those five query fields and the date, one row per
install per day ([schema.sql](schema.sql)). No IP address or other request data
is kept. A check with anything out of the expected shape is answered but not
counted. When the Worker is unreachable, the recorder asks GitHub directly, so
new versions are still announced.

## Deploying

Needs Node.js and a Cloudflare account with the domain on it.

It runs at `rlt-recorder.kaac.uk`, the hostname built into the recorder as
`Endpoint` in `src/RltUdpClient.Core/UpdateChecker.cs`; `wrangler.toml` already
names that domain and its database. Updating it:

```
cd server
npm install
npx wrangler login        # or set CLOUDFLARE_API_TOKEN
npx wrangler deploy
```

Setting it up from scratch elsewhere: `npx wrangler d1 create rlt-recorder`,
put the printed id and the new hostname into `wrangler.toml`, change `Endpoint`
to match, then:

```
npx wrangler d1 execute rlt-recorder --remote --file schema.sql
npx wrangler deploy
```

A token needs Account: Workers Scripts Edit, D1 Edit, Account Settings Read;
Zone (just that domain): Workers Routes Edit, DNS Edit; User: User Details Read,
Memberships Read.

`custom_domain = true` makes Wrangler create the DNS record and certificate
itself. Check it with an id that is not counted, so the test stays out of the
numbers:

```
curl "https://rlt-recorder.kaac.uk/v1/check?v=0.0.1&os=linux&arch=x64&flavor=cli&id=not-counted"
```

## Running locally

```
npx wrangler d1 execute rlt-recorder --local --file schema.sql
npx wrangler dev --local --host localhost
```

and point a recorder at it with `RLT_RECORDER_UPDATE_URL=http://127.0.0.1:8787/v1/check`.

## Numbers

```
npx wrangler d1 execute rlt-recorder --remote --command "<query>"
```

Installs active in the last 30 days, by platform:

```sql
SELECT os, arch, flavor, COUNT(DISTINCT install_id) AS installs
FROM checks WHERE day >= date('now', '-30 days')
GROUP BY os, arch, flavor ORDER BY installs DESC;
```

Versions in use over the last 7 days:

```sql
SELECT version, COUNT(DISTINCT install_id) AS installs
FROM checks WHERE day >= date('now', '-7 days')
GROUP BY version ORDER BY installs DESC;
```

Daily active installs:

```sql
SELECT day, COUNT(*) AS installs FROM checks GROUP BY day ORDER BY day DESC LIMIT 30;
```
