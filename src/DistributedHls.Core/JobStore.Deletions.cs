namespace DistributedHls.Core;

public sealed partial class JobStore
{
    public IReadOnlyList<DeletionRow> DueDeletions(DateTimeOffset now, int limit) => Read(cmd => Rows(Sql(cmd,
        "SELECT rel_dir,attempts FROM deletions WHERE not_before<=$now ORDER BY not_before,rel_dir LIMIT $limit", ("$now", Ms(now)), ("$limit", limit)),
        r => new DeletionRow(r.GetString(0), r.GetInt32(1))));

    public void CompleteDeletion(string rel) => Write(cmd => Sql(cmd, "DELETE FROM deletions WHERE rel_dir=$rel", ("$rel", rel)).ExecuteNonQuery());

    public void PostponeDeletion(string rel, string error, DateTimeOffset next) => Write(cmd => Sql(cmd,
        "UPDATE deletions SET attempts=attempts+1,last_error=$error,not_before=$next WHERE rel_dir=$rel",
        ("$rel", rel), ("$error", error), ("$next", Ms(next))).ExecuteNonQuery());
}
