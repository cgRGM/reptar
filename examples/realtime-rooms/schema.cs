using Reptar;
using static Reptar.ReptarApi;

public static partial class Schema
{
    // Best-effort persisted broadcast snapshots. A relayed frame is not a
    // durability acknowledgement; queue drops/write failures are explicit stats.
    // Storage schema lives in TableDef (never on the DTO records) per Reptar rules.
    public static readonly TableDef RoomMessages = DefineTable("room_messages",
        Col.Text("id").PrimaryKey(),
        Col.Text("room_id").NotNull().Indexed(),
        Col.Text("sender_id").NotNull(),
        Col.Text("payload").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );

    public static readonly TableDef[] All = [RoomMessages];
}
