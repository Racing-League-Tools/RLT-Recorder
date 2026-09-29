-- One row per install per day. Nothing else about the request is kept.
CREATE TABLE IF NOT EXISTS checks (
  day        TEXT NOT NULL,   -- YYYY-MM-DD, UTC
  install_id TEXT NOT NULL,   -- random, generated on the recorder's first run
  version    TEXT NOT NULL,
  os         TEXT NOT NULL,   -- windows | macos | linux
  arch       TEXT NOT NULL,   -- x64 | arm64 | arm | x86
  flavor     TEXT NOT NULL,   -- gui | cli
  PRIMARY KEY (day, install_id)
) WITHOUT ROWID;
