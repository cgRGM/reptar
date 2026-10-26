-- Reptar Rooms hub: persisted broadcast snapshots.
-- Matches Schema.RoomMessages (examples/realtime-rooms/schema.cs).
CREATE TABLE IF NOT EXISTS "room_messages" (
  "id" TEXT PRIMARY KEY,
  "room_id" TEXT NOT NULL,
  "sender_id" TEXT NOT NULL,
  "payload" TEXT NOT NULL,
  "created_at" TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS "room_messages_room_id_idx" ON "room_messages" ("room_id");
