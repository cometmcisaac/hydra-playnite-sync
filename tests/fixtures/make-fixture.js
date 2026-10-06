// Builds a fixture LevelDB mimicking Hydra's local DB (classic-level, JSON values,
// sublevels like the real hydra-db) for testing .NET LevelDB readers.
const { ClassicLevel } = require("classic-level");
const path = require("path");

async function main() {
  const dir = path.join(__dirname, "hydra-db-fixture");
  const db = new ClassicLevel(dir, {
    keyEncoding: "buffer",
    valueEncoding: "json",
    // Force early memtable flush so data lands in snappy-compressed SST files
    writeBufferSize: 4096,
  });
  await db.open();

  // Mimic sublevel-style prefixed keys (abstract-level sublevels use \x00 prefix)
  const put = (ns, key, value) =>
    db.put(Buffer.concat([Buffer.from("\x00"), Buffer.from(ns), Buffer.from("\x00"), Buffer.from(key)]), value);

  await put("games", "steam:1091500", {
    title: "Cyberpunk 2077",
    objectId: "1091500",
    shop: "steam",
    playTimeInMilliseconds: 73842500,
    lastTimePlayed: new Date("2026-09-20T18:30:00.000Z"),
    achievementCount: 44,
    unlockedAchievementCount: 17,
    favorite: true,
    source: "hydra",
  });

  await put("games", "custom:my-custom-game", {
    title: "Custom Indie Game",
    objectId: "my-custom-game",
    shop: "custom",
    playTimeInMilliseconds: 512000,
    lastTimePlayed: null,
    isDeleted: false,
    executablePath: "C:\\Games\\indie\\game.exe",
  });

  // Non-game entry the reader must ignore
  await put("auth", "auth", {
    accessToken: "xxx",
    refreshToken: "yyy",
    tokenExpirationTimestamp: Date.now() + 3600000,
  });

  // Deleted game (must be skipped)
  await put("games", "steam:999999", {
    title: "Deleted Game",
    objectId: "999999",
    shop: "steam",
    playTimeInMilliseconds: 1000,
    isDeleted: true,
  });

  // Bulk data to trigger L0 SST flush (writeBufferSize=4KB)
  for (let i = 0; i < 60; i++) {
    await put("games", `bulk:${i}`, {
      title: `Bulk Game ${i}`,
      objectId: `bulk-${i}`,
      shop: "custom",
      playTimeInMilliseconds: i * 1000,
      lastTimePlayed: new Date().toISOString(),
      padding: "x".repeat(2000),
    });
  }

  await db.close();
  console.log("fixture written to", dir);
}

main().catch((e) => { console.error(e); process.exit(1); });
